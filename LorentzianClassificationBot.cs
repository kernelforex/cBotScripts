using System;
using System.Linq;
using System.Collections.Generic;
using cAlgo.API;
using cAlgo.API.Indicators;
using cAlgo.API.Internals;


namespace cAlgo.Robots
{
    [Robot(TimeZone = TimeZones.UTC, AccessRights = AccessRights.None)]
    public class LorentzianClassificationBot : Robot
    {
        [Parameter("Source", DefaultValue = "Close")]
        public DataSeries Source { get; set; }

        [Parameter("Neighbors Count", DefaultValue = 8, MinValue = 1, MaxValue = 100)]
        public int NeighborsCount { get; set; }

        [Parameter("Stop Loss (Pips)", DefaultValue = 20)]
        public double StopLossPips { get; set; }

        [Parameter("Take Profit (Pips)", DefaultValue = 40)]
        public double TakeProfitPips { get; set; }

        [Parameter("Position Volume (Lots)", DefaultValue = 0.1, MinValue = 0.01)]
        public double PositionVolumeLots { get; set; }


        [Parameter("RSI Period", DefaultValue = 14)]
        public int RsiPeriod { get; set; }


        [Parameter("ADX Period", DefaultValue = 14)]
        public int AdxPeriod { get; set; }


        [Parameter("Trend Threshold", DefaultValue = 0.1, MinValue = 0.01)]
        public double TrendThreshold { get; set; }

        private RelativeStrengthIndex rsi;
        private DirectionalMovementSystem adx;
        private List<double> features;
        private List<int> predictions;
        private List<double> distances;
        private const int MAX_BARS_BACK = 2000;
        private const int FEATURE_COUNT = 2;
        private const int FUTURE_BARS = 4;
        private double volumeInUnits;

        protected override void OnStart()
        {
            volumeInUnits = PositionVolumeLots * Symbol.LotSize;
            ValidateTradeParameters();
            
            rsi = Indicators.RelativeStrengthIndex(Source, RsiPeriod);
            adx = Indicators.DirectionalMovementSystem(AdxPeriod);
            features = new List<double>();
            predictions = new List<int>();
            distances = new List<double>();
        }

        private void ValidateTradeParameters()
        {
            if (volumeInUnits < Symbol.VolumeInUnitsMin || volumeInUnits > Symbol.VolumeInUnitsMax)
            {
                throw new Exception($"Invalid position volume. Must be between {Symbol.VolumeInUnitsMin / Symbol.LotSize} and {Symbol.VolumeInUnitsMax / Symbol.LotSize} lots");
            }

            if (volumeInUnits % Symbol.VolumeInUnitsStep != 0)
            {
                volumeInUnits = Math.Round(volumeInUnits / Symbol.VolumeInUnitsStep) * Symbol.VolumeInUnitsStep;
                Print($"Volume adjusted to {volumeInUnits / Symbol.LotSize} lots to match symbol requirements");
            }
        }

        protected override void OnBar()
        {
            if (Bars.ClosePrices.Count < MAX_BARS_BACK)
                return;

            double currentRsi = rsi.Result.Last();
            double currentAdx = adx.ADX.Last();
            
            features.Clear();
            features.Add(currentRsi);
            features.Add(currentAdx);

            var (longProbability, shortProbability) = CalculateProbabilities();
            
            // Debug output
            Print($"Long Probability: {longProbability:F4}, Short Probability: {shortProbability:F4}");

            bool longSignal = longProbability > TrendThreshold && FilterConditions(TradeType.Buy);
            bool shortSignal = shortProbability > TrendThreshold && FilterConditions(TradeType.Sell);

            ExecuteTrades(longSignal, shortSignal);
        }

        private (double longProb, double shortProb) CalculateProbabilities()
        {
            distances.Clear();
            predictions.Clear();

            List<(double distance, int direction)> neighborData = new List<(double, int)>();

            // Collect historical patterns
            for (int i = 0; i < Math.Min(MAX_BARS_BACK - 1, Bars.ClosePrices.Count - 1); i++)
            {
                if (i % 4 != 0) continue;

                double distance = CalculateLorentzianDistance(i);
                int direction = DetermineDirection(i);
                
                neighborData.Add((distance, direction));
            }

            // Sort by distance and take nearest neighbors
            var nearestNeighbors = neighborData
                .OrderBy(x => x.distance)
                .Take(NeighborsCount)
                .ToList();

            int longCount = nearestNeighbors.Count(x => x.direction == 1);
            int shortCount = nearestNeighbors.Count(x => x.direction == -1);

            double longProbability = (double)longCount / NeighborsCount;
            double shortProbability = (double)shortCount / NeighborsCount;

            return (longProbability, shortProbability);
        }

        private double CalculateLorentzianDistance(int index)
        {
            double rsiDiff = Math.Abs(rsi.Result.Last() - rsi.Result[index]);
            double adxDiff = Math.Abs(adx.ADX.Last() - adx.ADX[index]);
            
            // Base distance calculation using RSI and ADX
            return Math.Log(1 + rsiDiff) + Math.Log(1 + adxDiff);
        }

        private int DetermineDirection(int index)
        {
            if (index + FUTURE_BARS >= Bars.ClosePrices.Count)
                return 0;

            double currentPrice = Bars.ClosePrices[index];
            double futurePrice = Bars.ClosePrices[index + FUTURE_BARS];
            
            // Calculate percentage change
            double priceChange = (futurePrice - currentPrice) / currentPrice;
            
            // Add minimum threshold for trend determination
            double minThreshold = 0.0005; // 0.05% minimum change
            
            if (priceChange < -minThreshold)
                return -1;
            else if (priceChange > minThreshold)
                return 1;
            return 0;
        }

        private bool FilterConditions(TradeType tradeType)
        {
            bool volatilityFilter = true;
            bool regimeFilter = adx.ADX.Last() > 20;
            bool adxFilter = adx.ADX.Last() > 25;

            // Add direction-specific RSI filters
            bool rsiFilter = tradeType == TradeType.Buy ? 
                rsi.Result.Last() > 50 :
                rsi.Result.Last() < 50;

            return volatilityFilter && regimeFilter && adxFilter && rsiFilter;
        }

        private void ExecuteTrades(bool longSignal, bool shortSignal)
        {
            try
            {
                if ((longSignal && Positions.Find("Short") != null) ||
                    (shortSignal && Positions.Find("Long") != null))
                {
                    ClosePositions();
                }

                if (longSignal && Positions.Find("Long") == null)
                {
                    var result = ExecuteMarketOrder(TradeType.Buy, SymbolName, volumeInUnits, "Long", StopLossPips, TakeProfitPips);
                    if (!string.IsNullOrEmpty(result.Error.ToString()))
                    {
                        Print($"Error executing long position: {result.Error}");
                    }
                }
                else if (shortSignal && Positions.Find("Short") == null)
                {
                    var result = ExecuteMarketOrder(TradeType.Sell, SymbolName, volumeInUnits, "Short", StopLossPips, TakeProfitPips);
                    if (!string.IsNullOrEmpty(result.Error.ToString()))
                    {
                        Print($"Error executing short position: {result.Error}");
                    }
                }
            }
            catch (Exception ex)
            {
                Print($"Error in ExecuteTrades: {ex.Message}");
            }
        }

        private void ClosePositions()
        {
            foreach (var position in Positions)
            {
                ClosePosition(position);
            }
        }
    }
}