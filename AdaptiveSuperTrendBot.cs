using System;
using cAlgo.API;
using cAlgo.API.Indicators;
using System.Collections.Generic;
using System.Linq;

namespace cAlgo.Robots
{
    [Robot(TimeZone = TimeZones.UTC, AccessRights = AccessRights.None)]
    public class ImprovedAdaptiveSuperTrendBot : Robot
    {
        [Parameter("ATR Length", DefaultValue = 10, Group = "SuperTrend Settings")]
        public int AtrLength { get; set; }

        [Parameter("SuperTrend Factor", DefaultValue = 2.0, Group = "SuperTrend Settings")]
        public double Factor { get; set; }

        [Parameter("Training Data Length", DefaultValue = 50, Group = "Volatility Settings")]
        public int TrainingDataPeriod { get; set; }

        [Parameter("Dynamic Factor Adjustment", DefaultValue = true, Group = "Volatility Settings")]
        public bool UseDynamicFactor { get; set; }

        [Parameter("Stop Loss (pips)", DefaultValue = 20)]
        public double StopLossPips { get; set; }

        [Parameter("Take Profit (pips)", DefaultValue = 40)]
        public double TakeProfitPips { get; set; }

        [Parameter("Minimum Bars Between Trades", DefaultValue = 2)]
        public int MinBarsBetweenTrades { get; set; }

        [Parameter("Use Price Action Confirmation", DefaultValue = true)]
        public bool UsePriceActionConfirmation { get; set; }

        private AverageTrueRange atr;
        private ExponentialMovingAverage ema20;
        private ExponentialMovingAverage ema50;
        private RelativeStrengthIndex rsi;
        private MacdCrossOver macd;
        private Queue<double> upperBand;
        private Queue<double> lowerBand;
        private Queue<int> direction;
        private List<double> volatilityData;
        private DateTime lastTradeTime = DateTime.MinValue;
        private const int QueueSize = 3;

        protected override void OnStart()
        {
            // Initialize indicators
            atr = Indicators.AverageTrueRange(AtrLength, MovingAverageType.Exponential);
            ema20 = Indicators.ExponentialMovingAverage(MarketSeries.Close, 20);
            ema50 = Indicators.ExponentialMovingAverage(MarketSeries.Close, 50);
            rsi = Indicators.RelativeStrengthIndex(MarketSeries.Close, 14);
            macd = Indicators.MacdCrossOver(MarketSeries.Close, 12, 26, 9);
            
            // Initialize collections
            volatilityData = new List<double>();
            upperBand = new Queue<double>(QueueSize);
            lowerBand = new Queue<double>(QueueSize);
            direction = new Queue<int>(QueueSize);

            InitializeQueues();
        }

        private void InitializeQueues()
        {
            for (int i = 0; i < QueueSize; i++)
            {
                upperBand.Enqueue(double.MaxValue);
                lowerBand.Enqueue(double.MinValue);
                direction.Enqueue(1);
            }
        }

        protected override void OnTick()
        {
            if (Bars.Count < TrainingDataPeriod + 1) return;

            UpdateVolatilityData();
            CalculateAdaptiveSuperTrend();
            ExecuteTrading();
        }

        private void UpdateVolatilityData()
        {
            volatilityData.Clear();
            for (int i = 0; i < Math.Min(TrainingDataPeriod, Bars.Count); i++)
            {
                if (atr.Result[i] > 0)
                    volatilityData.Add(atr.Result[i]);
            }
        }

        private double CalculateDynamicFactor()
        {
            if (!UseDynamicFactor || volatilityData.Count == 0) 
                return Factor;

            double currentVol = atr.Result.Last(0);
            double avgVol = volatilityData.Average();
            double stdDev = Math.Sqrt(volatilityData.Select(x => Math.Pow(x - avgVol, 2)).Average());
            
            if (currentVol > avgVol + stdDev)
                return Factor * 1.5;
            else if (currentVol < avgVol - stdDev)
                return Factor * 0.75;
            
            return Factor;
        }

        private void CalculateAdaptiveSuperTrend()
        {
            double dynamicFactor = CalculateDynamicFactor();
            double src = (MarketSeries.High.Last(0) + MarketSeries.Low.Last(0)) / 2;
            double atrValue = atr.Result.Last(0);
            
            double newUpperBand = src + dynamicFactor * atrValue;
            double newLowerBand = src - dynamicFactor * atrValue;

            if (MarketSeries.Close.Last(1) > upperBand.Last())
                newUpperBand = Math.Min(newUpperBand, upperBand.Last());
            if (MarketSeries.Close.Last(1) < lowerBand.Last())
                newLowerBand = Math.Max(newLowerBand, lowerBand.Last());

            UpdateDirectionAndBands(newUpperBand, newLowerBand);
        }

        private void UpdateDirectionAndBands(double newUpperBand, double newLowerBand)
        {
            int newDirection = direction.Last();
            if (MarketSeries.Close.Last(0) > upperBand.Last())
                newDirection = 1;
            else if (MarketSeries.Close.Last(0) < lowerBand.Last())
                newDirection = -1;

            // Update queues
            upperBand.Enqueue(newUpperBand);
            lowerBand.Enqueue(newLowerBand);
            direction.Enqueue(newDirection);

            // Maintain queue size
            while (upperBand.Count > QueueSize) upperBand.Dequeue();
            while (lowerBand.Count > QueueSize) lowerBand.Dequeue();
            while (direction.Count > QueueSize) direction.Dequeue();
        }

        private bool HasSufficientTimePassed()
        {
            if (lastTradeTime == DateTime.MinValue) return true;
            
            var currentTime = MarketSeries.OpenTime.Last();
            var timeFrame = TimeFrame.ToString();
            int requiredMinutes = MinBarsBetweenTrades * GetTimeframeMinutes();
            
            return currentTime >= lastTradeTime.AddMinutes(requiredMinutes);
        }

        private int GetTimeframeMinutes()
        {
            var timeFrame = TimeFrame.ToString();
            
            if (timeFrame.Contains("Minute"))
                return timeFrame == "Minute" ? 1 : int.Parse(timeFrame.Replace("Minute", ""));
            else if (timeFrame.Contains("Hour"))
                return timeFrame == "Hour" ? 60 : int.Parse(timeFrame.Replace("Hour", "")) * 60;
            else if (timeFrame == "Daily")
                return 1440;
                
            return 1;
        }

        private bool IsTrendConfirmed(TradeType tradeType)
        {
            if (!UsePriceActionConfirmation) return true;

            double currentPrice = MarketSeries.Close.Last(0);
            double ema20Value = ema20.Result.Last(0);
            double ema50Value = ema50.Result.Last(0);
            double rsiValue = rsi.Result.Last(0);
            
            double macdMain = macd.MACD.Last(0);
            double macdSignal = macd.Signal.Last(0);
            bool macdBullish = macdMain > macdSignal;
            bool macdBearish = macdMain < macdSignal;

            if (tradeType == TradeType.Buy)
            {
                bool priceAboveEma = currentPrice > ema20Value && ema20Value > ema50Value;
                bool rsiSupport = rsiValue > 30 && rsiValue < 70;
                return priceAboveEma && (rsiSupport || macdBullish);
            }
            else
            {
                bool priceBelowEma = currentPrice < ema20Value && ema20Value < ema50Value;
                bool rsiSupport = rsiValue > 30 && rsiValue < 70;
                return priceBelowEma && (rsiSupport || macdBearish);
            }
        }

        private void ExecuteTrading()
        {
            if (direction.Count < 2 || !HasSufficientTimePassed() || HasOpenPosition())
                return;

            var dirArray = direction.ToArray();
            bool signalChange = dirArray[dirArray.Length - 1] != dirArray[dirArray.Length - 2];
            
            if (!signalChange) return;

            double volume = Symbol.NormalizeVolumeInUnits(Symbol.QuantityToVolumeInUnits(1.0));
            bool isBuySignal = dirArray[dirArray.Length - 1] == 1;

            if (isBuySignal && IsTrendConfirmed(TradeType.Buy))
            {
                var result = ExecuteMarketOrder(TradeType.Buy, SymbolName, volume, "Adaptive SuperTrend Buy", 
                    StopLossPips, TakeProfitPips);
                    
                if (result.IsSuccessful)
                    lastTradeTime = MarketSeries.OpenTime.Last();
            }
            else if (!isBuySignal && IsTrendConfirmed(TradeType.Sell))
            {
                var result = ExecuteMarketOrder(TradeType.Sell, SymbolName, volume, "Adaptive SuperTrend Sell", 
                    StopLossPips, TakeProfitPips);
                    
                if (result.IsSuccessful)
                    lastTradeTime = MarketSeries.OpenTime.Last();
            }
        }

        private bool HasOpenPosition()
        {
            return Positions.Count > 0;
        }

        protected override void OnStop()
        {
            // Cleanup if needed
        }
    }
}