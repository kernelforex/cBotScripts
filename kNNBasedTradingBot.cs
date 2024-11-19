using System;
using System.Collections.Generic;
using System.Linq;
using cAlgo.API;
using cAlgo.API.Indicators;

namespace cAlgo.Robots
{
    [Robot(TimeZone = TimeZones.UTC, AccessRights = AccessRights.None)]
    public class KNNTradingBot : Robot
    {
        [Parameter("RSI Short Period", DefaultValue = 14, MinValue = 1)]
        public int ShortWindow { get; set; }

        [Parameter("RSI Long Period", DefaultValue = 28, MinValue = 2)]
        public int LongWindow { get; set; }

        [Parameter("Base K Neighbors", DefaultValue = 252, MinValue = 5)]
        public int BaseK { get; set; }

        [Parameter("Stop Loss (pips)", DefaultValue = 20, MinValue = 1)]
        public double StopLossPips { get; set; }

        [Parameter("Take Profit (pips)", DefaultValue = 40, MinValue = 1)]
        public double TakeProfitPips { get; set; }

        [Parameter("Order Volume (lots)", DefaultValue = 0.1, MinValue = 0.01)]
        public double OrderVolume { get; set; }

        [Parameter("Minimum Signal Strength", DefaultValue = 3, MinValue = 1)]
        public int MinSignalStrength { get; set; }

        [Parameter("MA Period", DefaultValue = 200, MinValue = 10)]
        public int MAPeriod { get; set; }

        [Parameter("ATR Period", DefaultValue = 14, MinValue = 1)]
        public int AtrPeriod { get; set; }

        [Parameter("Max Consecutive Losses", DefaultValue = 3, MinValue = 1)]
        public int MaxConsecutiveLosses { get; set; }

        [Parameter("Trade Timeout Minutes", DefaultValue = 60, MinValue = 1)]
        public int TradeTimeoutMinutes { get; set; }

        private RelativeStrengthIndex rsiLong;
        private RelativeStrengthIndex rsiShort;
        private MovingAverage ma;
        private AverageTrueRange atr;
        private List<double> feature1;
        private List<double> feature2;
        private List<int> directions;
        private List<int> predictions;
        private int k;
        private int minimumBarsRequired;
        private double tradeVolume;
        private int consecutiveLosses;
        private bool isInTradeTimeout;
        private DateTime lastTradeTime;

        protected override void OnStart()
        {
            rsiLong = Indicators.RelativeStrengthIndex(Bars.ClosePrices, LongWindow);
            rsiShort = Indicators.RelativeStrengthIndex(Bars.ClosePrices, ShortWindow);
            ma = Indicators.MovingAverage(Bars.ClosePrices, MAPeriod, MovingAverageType.Exponential);
            atr = Indicators.AverageTrueRange(AtrPeriod, MovingAverageType.Exponential);

            feature1 = new List<double>();
            feature2 = new List<double>();
            directions = new List<int>();
            predictions = new List<int>();

            k = (int)Math.Floor(Math.Sqrt(BaseK));
            minimumBarsRequired = Math.Max(Math.Max(LongWindow, MAPeriod), BaseK);
            tradeVolume = OrderVolume * Symbol.LotSize;
            consecutiveLosses = 0;
            isInTradeTimeout = false;
            lastTradeTime = DateTime.MinValue;

            Print($"Bot Initialized:");
            Print($"Max Consecutive Losses: {MaxConsecutiveLosses}");
            Print($"Trade Timeout Period: {TradeTimeoutMinutes} minutes");
            Print($"Stop Loss: {StopLossPips} pips");
            Print($"Take Profit: {TakeProfitPips} pips");
            Print($"Order Volume: {OrderVolume} lots ({tradeVolume} units)");
        }

        private bool IsGoodTradingHour()
{
    // Convert server time to UTC/GMT
    int currentHour = Server.Time.Hour;
    
    // Check if we're in London or New York session
    bool isLondonSession = currentHour >= 8 && currentHour < 16;
    bool isNewYorkSession = currentHour >= 13 && currentHour < 21;
    
    // Only trade during major sessions
    return isLondonSession || isNewYorkSession;
}

        protected override void OnBar()
        {
            try
            {

                  if (!IsGoodTradingHour())
        {
            return;
        }
        
                if (Bars.Count < minimumBarsRequired)
                {
                    Print($"Not enough bars: {Bars.Count}/{minimumBarsRequired}");
                    return;
                }

                // Check trade timeout
                if (isInTradeTimeout)
                {
                    if ((Server.Time - lastTradeTime).TotalMinutes >= TradeTimeoutMinutes)
                    {
                        isInTradeTimeout = false;
                        Print("Trade timeout period ended");
                    }
                    else
                    {
                        return;
                    }
                }

                // Check if we're in a clear trend
                bool isUptrend = IsUptrend();
                bool isDowntrend = IsDowntrend();

                if (!isUptrend && !isDowntrend)
                {
                    Print("No clear trend direction");
                    return;
                }

                UpdateTrainingData();
                double prediction = GetPrediction();

                // Only trade in trend direction with strong signals
                bool longSignal = prediction >= MinSignalStrength && isUptrend;
                bool shortSignal = prediction <= -MinSignalStrength && isDowntrend;

                if (longSignal || shortSignal)
                {
                    ExecuteTrades(longSignal, shortSignal);
                }
            }
            catch (Exception ex)
            {
                Print($"Error in OnBar: {ex.Message}");
            }
        }

        private bool IsUptrend()
        {
            return Bars.ClosePrices.Last(0) > ma.Result.Last(0) &&
                   ma.Result.Last(0) > ma.Result.Last(1) &&
                   Bars.ClosePrices.Last(0) > Bars.ClosePrices.Last(1);
        }

        private bool IsDowntrend()
        {
            return Bars.ClosePrices.Last(0) < ma.Result.Last(0) &&
                   ma.Result.Last(0) < ma.Result.Last(1) &&
                   Bars.ClosePrices.Last(0) < Bars.ClosePrices.Last(1);
        }

        private void UpdateTrainingData()
        {
            double f1 = rsiLong.Result.Last(0);
            double f2 = rsiShort.Result.Last(0);
            int direction = Math.Sign(Bars.ClosePrices.Last(2) - Bars.ClosePrices.Last(1));

            feature1.Add(f1);
            feature2.Add(f2);
            directions.Add(direction);

            while (feature1.Count > BaseK)
            {
                feature1.RemoveAt(0);
                feature2.RemoveAt(0);
                directions.RemoveAt(0);
            }
        }

        private double GetPrediction()
        {
            if (directions.Count < k) return 0;

            predictions.Clear();
            var distances = new List<Tuple<double, int>>();
            double currentF1 = rsiLong.Result.Last(0);
            double currentF2 = rsiShort.Result.Last(0);

            for (int i = 0; i < directions.Count; i++)
            {
                double distance = Math.Sqrt(
                    Math.Pow(currentF1 - feature1[i], 2) + 
                    Math.Pow(currentF2 - feature2[i], 2)
                );
                distances.Add(new Tuple<double, int>(distance, directions[i]));
            }

            var kNearest = distances.OrderBy(x => x.Item1).Take(k);
            return kNearest.Sum(x => x.Item2);
        }

        private void ExecuteTrades(bool longSignal, bool shortSignal)
        {
            if (consecutiveLosses >= MaxConsecutiveLosses)
            {
                Print($"Max consecutive losses ({MaxConsecutiveLosses}) reached. Taking a break.");
                return;
            }

            foreach (var position in Positions)
            {
                if ((position.TradeType == TradeType.Buy && shortSignal) ||
                    (position.TradeType == TradeType.Sell && longSignal))
                {
                    ClosePosition(position);
                    HandlePositionClosed(position);
                }
            }

            if (Positions.Count == 0)
            {
                // Use ATR for dynamic stop loss and take profit
                double currentAtr = atr.Result.Last(0);
                double dynamicStopLoss = Math.Max(StopLossPips, currentAtr * 1.5);
                double dynamicTakeProfit = Math.Max(TakeProfitPips, currentAtr * 2);

                Print($"Dynamic SL: {dynamicStopLoss:F1} pips, TP: {dynamicTakeProfit:F1} pips");

                if (longSignal)
                {
                    var result = ExecuteMarketOrder(
                        TradeType.Buy,
                        SymbolName,
                        tradeVolume,
                        "KNN_Long",
                        dynamicStopLoss,
                        dynamicTakeProfit
                    );

                    if (result.IsSuccessful)
                    {
                        Print($"Opened Long at {Symbol.Ask}, Volume: {tradeVolume / Symbol.LotSize:F2} lots");
                        lastTradeTime = Server.Time;
                    }
                    else
                    {
                        Print($"Failed to open Long. Error: {result.Error}");
                    }
                }
                else if (shortSignal)
                {
                    var result = ExecuteMarketOrder(
                        TradeType.Sell,
                        SymbolName,
                        tradeVolume,
                        "KNN_Short",
                        dynamicStopLoss,
                        dynamicTakeProfit
                    );

                    if (result.IsSuccessful)
                    {
                        Print($"Opened Short at {Symbol.Bid}, Volume: {tradeVolume / Symbol.LotSize:F2} lots");
                        lastTradeTime = Server.Time;
                    }
                    else
                    {
                        Print($"Failed to open Short. Error: {result.Error}");
                    }
                }
            }
        }

        private void HandlePositionClosed(Position position)
        {
            if (position.NetProfit < 0)
            {
                consecutiveLosses++;
                if (consecutiveLosses >= MaxConsecutiveLosses)
                {
                    isInTradeTimeout = true;
                    lastTradeTime = Server.Time;
                    Print($"Taking a break after {consecutiveLosses} consecutive losses for {TradeTimeoutMinutes} minutes");
                }
            }
            else
            {
                consecutiveLosses = 0;
                Print("Winning trade! Consecutive losses reset to 0");
            }
        }
    }
}