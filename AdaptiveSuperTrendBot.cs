using System;
using cAlgo.API;
using cAlgo.API.Indicators;
using System.Collections.Generic;
using System.Linq;

namespace cAlgo.Robots
{
    [Robot(TimeZone = TimeZones.UTC, AccessRights = AccessRights.None)]
    public class AdaptiveSuperTrendBot : Robot
    {
        [Parameter("ATR Length", DefaultValue = 10, Group = "SuperTrend Settings")]
        public int AtrLength { get; set; }

        [Parameter("SuperTrend Factor", DefaultValue = 3.0, Group = "SuperTrend Settings")]
        public double Factor { get; set; }

        [Parameter("Training Data Length", DefaultValue = 100, Group = "K-Means Settings")]
        public int TrainingDataPeriod { get; set; }

        [Parameter("High Volatility Percentile", DefaultValue = 0.75, Group = "K-Means Settings")]
        public double HighVolPercentile { get; set; }

        [Parameter("Medium Volatility Percentile", DefaultValue = 0.5, Group = "K-Means Settings")]
        public double MedVolPercentile { get; set; }

        [Parameter("Low Volatility Percentile", DefaultValue = 0.25, Group = "K-Means Settings")]
        public double LowVolPercentile { get; set; }

        [Parameter("Stop Loss (pips)", DefaultValue = 30)]
        public double StopLossPips { get; set; }

        [Parameter("Take Profit (pips)", DefaultValue = 60)]
        public double TakeProfitPips { get; set; }

        [Parameter("Minimum RSI", DefaultValue = 30)]
        public double MinRSI { get; set; }

        [Parameter("Maximum RSI", DefaultValue = 70)]
        public double MaxRSI { get; set; }

        private AverageTrueRange atr;
        private RelativeStrengthIndex rsi;
        private Queue<double> upperBand;
        private Queue<double> lowerBand;
        private Queue<int> direction;
        private Queue<double> superTrend;
        private List<double> volatilityData;
        private int currentCluster;
        private const int QueueSize = 3;
        private double lastTradePrice = 0;
        private const int MinBarsSinceLastTrade = 5;
        private DateTime lastTradeTime = DateTime.MinValue;

        protected override void OnStart()
        {
            atr = Indicators.AverageTrueRange(AtrLength, MovingAverageType.Simple);
            rsi = Indicators.RelativeStrengthIndex(MarketSeries.Close, 14);
            volatilityData = new List<double>();
            
            upperBand = new Queue<double>(QueueSize);
            lowerBand = new Queue<double>(QueueSize);
            direction = new Queue<int>(QueueSize);
            superTrend = new Queue<double>(QueueSize);

            for (int i = 0; i < QueueSize; i++)
            {
                upperBand.Enqueue(0);
                lowerBand.Enqueue(0);
                direction.Enqueue(1);
                superTrend.Enqueue(0);
            }
        }

        private void MaintainQueueSize(Queue<double> queue)
        {
            while (queue.Count > QueueSize)
                queue.Dequeue();
        }

        private void MaintainQueueSize(Queue<int> queue)
        {
            while (queue.Count > QueueSize)
                queue.Dequeue();
        }

        protected override void OnTick()
        {
            if (Bars.Count < TrainingDataPeriod + 1) return;

            UpdateVolatilityData();
            CalculateSuperTrend();
            HandleTrading();
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

        private void CalculateSuperTrend()
        {
            if (volatilityData.Count == 0) return;

            double assignedCentroid = GetVolatilityCluster();
            double src = (MarketSeries.High.Last(0) + MarketSeries.Low.Last(0)) / 2;
            
            double newUpperBand = src + Factor * assignedCentroid;
            double newLowerBand = src - Factor * assignedCentroid;

            if (Bars.ClosePrices.Last(1) > upperBand.Last())
                newUpperBand = Math.Min(newUpperBand, upperBand.Last());
            if (Bars.ClosePrices.Last(1) < lowerBand.Last())
                newLowerBand = Math.Max(newLowerBand, lowerBand.Last());

            int newDirection = direction.Last();
            if (Bars.ClosePrices.Last(0) > upperBand.Last())
                newDirection = -1;
            else if (Bars.ClosePrices.Last(0) < lowerBand.Last())
                newDirection = 1;

            upperBand.Enqueue(newUpperBand);
            lowerBand.Enqueue(newLowerBand);
            direction.Enqueue(newDirection);
            superTrend.Enqueue(newDirection == -1 ? newLowerBand : newUpperBand);

            MaintainQueueSize(upperBand);
            MaintainQueueSize(lowerBand);
            MaintainQueueSize(direction);
            MaintainQueueSize(superTrend);
        }

        private double GetVolatilityCluster()
        {
            if (volatilityData.Count == 0) return 0;

            var sortedVol = volatilityData.OrderBy(x => x).ToList();
            int highIndex = Math.Min((int)(sortedVol.Count * HighVolPercentile), sortedVol.Count - 1);
            int medIndex = Math.Min((int)(sortedVol.Count * MedVolPercentile), sortedVol.Count - 1);
            int lowIndex = Math.Min((int)(sortedVol.Count * LowVolPercentile), sortedVol.Count - 1);

            double highVol = sortedVol[highIndex];
            double medVol = sortedVol[medIndex];
            double lowVol = sortedVol[lowIndex];

            double currentVol = atr.Result.Last(0);
            
            var distances = new[]
            {
                Math.Abs(currentVol - highVol),
                Math.Abs(currentVol - medVol),
                Math.Abs(currentVol - lowVol)
            };
            
            currentCluster = Array.IndexOf(distances, distances.Min());
            return new[] { highVol, medVol, lowVol }[currentCluster];
        }

        private bool IsTrendConfirmed(TradeType tradeType)
        {
            double currentRsi = rsi.Result.Last(0);
            
            if (tradeType == TradeType.Buy)
                return currentRsi > MinRSI && currentRsi < 50;
            else
                return currentRsi < MaxRSI && currentRsi > 50;
        }

private bool HasSufficientTimePassed()
{
    TimeSpan timeSinceLastTrade = MarketSeries.OpenTime.Last() - lastTradeTime;
    int minutesPerBar;
    var timeFrame = TimeFrame.ToString();
    
    if (timeFrame.Contains("Minute"))
    {
        if (timeFrame == "Minute")
            minutesPerBar = 1;
        else
            minutesPerBar = int.Parse(timeFrame.Replace("Minute", ""));
    }
    else if (timeFrame.Contains("Hour"))
    {
        if (timeFrame == "Hour")
            minutesPerBar = 60;
        else
            minutesPerBar = int.Parse(timeFrame.Replace("Hour", "")) * 60;
    }
    else if (timeFrame == "Daily")
        minutesPerBar = 1440;
    else
        minutesPerBar = 1;
    
    return timeSinceLastTrade.TotalMinutes >= MinBarsSinceLastTrade * minutesPerBar;
}

        private void HandleTrading()
        {
            if (direction.Count < 2) return;

            var dirArray = direction.ToArray();
            bool bullishCross = dirArray[dirArray.Length - 1] < dirArray[dirArray.Length - 2];
            bool bearishCross = dirArray[dirArray.Length - 1] > dirArray[dirArray.Length - 2];

            double volume = Symbol.NormalizeVolumeInUnits(Symbol.QuantityToVolumeInUnits(1.0));
            double currentPrice = Symbol.Bid;

            if (!HasSufficientTimePassed()) return;

            if (bullishCross && !HasOpenPosition() && IsTrendConfirmed(TradeType.Buy))
            {
                var result = ExecuteMarketOrder(TradeType.Buy, SymbolName, volume, "Adaptive SuperTrend Buy", 
                    StopLossPips, TakeProfitPips);
                    
                if (result.IsSuccessful)
                {
                    lastTradePrice = result.Position.EntryPrice;
                    lastTradeTime = MarketSeries.OpenTime.Last();
                }
            }
            else if (bearishCross && !HasOpenPosition() && IsTrendConfirmed(TradeType.Sell))
            {
                var result = ExecuteMarketOrder(TradeType.Sell, SymbolName, volume, "Adaptive SuperTrend Sell", 
                    StopLossPips, TakeProfitPips);
                    
                if (result.IsSuccessful)
                {
                    lastTradePrice = result.Position.EntryPrice;
                    lastTradeTime = MarketSeries.OpenTime.Last();
                }
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