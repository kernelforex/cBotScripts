using System;
using System.Linq;
using System.Collections.Generic;
using cAlgo.API;
using cAlgo.API.Indicators;
using cAlgo.API.Internals;

namespace cAlgo.Robots
{
    [Robot(TimeZone = TimeZones.UTC, AccessRights = AccessRights.None)]
    public class EnhancedLorentzianClassificationBot : Robot
    {
        [Parameter("Source", DefaultValue = "Close")]
        public DataSeries Source { get; set; }

        [Parameter("Neighbors Count", DefaultValue = 12, MinValue = 1, MaxValue = 100)]
        public int NeighborsCount { get; set; }

        [Parameter("Stop Loss (Pips)", DefaultValue = 25)]
        public double StopLossPips { get; set; }

        [Parameter("Take Profit (Pips)", DefaultValue = 50)]
        public double TakeProfitPips { get; set; }

        [Parameter("Position Volume (Lots)", DefaultValue = 0.1, MinValue = 0.01)]
        public double PositionVolumeLots { get; set; }

        [Parameter("RSI Period", DefaultValue = 14)]
        public int RsiPeriod { get; set; }

        [Parameter("ADX Period", DefaultValue = 14)]
        public int AdxPeriod { get; set; }

        [Parameter("MA Period", DefaultValue = 200)]
        public int MaPeriod { get; set; }

        [Parameter("Volatility Period", DefaultValue = 20)]
        public int VolatilityPeriod { get; set; }

        [Parameter("Trend Probability Threshold", DefaultValue = 0.65, MinValue = 0.5, MaxValue = 1.0)]
        public double TrendThreshold { get; set; }

        [Parameter("Minimum Pattern Similarity", DefaultValue = 0.75, MinValue = 0.1, MaxValue = 1.0)]
        public double MinPatternSimilarity { get; set; }

        [Parameter("MA Type", DefaultValue = MovingAverageType.Exponential)]
        public MovingAverageType MAType { get; set; }

        private RelativeStrengthIndex rsi;
        private DirectionalMovementSystem adx;
        private MovingAverage ma;
        private StandardDeviation volatility;
        private List<double> features;
        private List<int> predictions;
        private List<double> distances;
        private const int MAX_BARS_BACK = 3000;
        private const int FEATURE_COUNT = 4;
        private const int FUTURE_BARS = 6;
        private double volumeInUnits;
        private double previousVolatility;

        protected override void OnStart()
        {
            volumeInUnits = PositionVolumeLots * Symbol.LotSize;
            ValidateTradeParameters();
            
            rsi = Indicators.RelativeStrengthIndex(Source, RsiPeriod);
            adx = Indicators.DirectionalMovementSystem(AdxPeriod);
            ma = Indicators.MovingAverage(Source, MaPeriod, MAType);
            volatility = Indicators.StandardDeviation(Source, VolatilityPeriod, MAType);
            
            features = new List<double>();
            predictions = new List<int>();
            distances = new List<double>();
            previousVolatility = 0;
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

            UpdateFeatures();

            var (longProbability, shortProbability) = CalculateProbabilities();
            
            Print($"Long Probability: {longProbability:F4}, Short Probability: {shortProbability:F4}");

            bool longSignal = longProbability > TrendThreshold && FilterConditions(TradeType.Buy);
            bool shortSignal = shortProbability > TrendThreshold && FilterConditions(TradeType.Sell);

            if (IsVolatilityStable())
            {
                ExecuteTrades(longSignal, shortSignal);
            }
            
            previousVolatility = volatility.Result.Last();
        }

        private void UpdateFeatures()
        {
            features.Clear();
            features.Add(NormalizeRsi(rsi.Result.Last()));
            features.Add(NormalizeAdx(adx.ADX.Last()));
            features.Add(CalculatePricePosition());
            features.Add(NormalizeVolatility(volatility.Result.Last()));
        }

        private double NormalizeRsi(double value)
        {
            return value / 100.0;
        }

        private double NormalizeAdx(double value)
        {
            return value / 100.0;
        }

        private double NormalizeVolatility(double value)
        {
            double maxVol = volatility.Result.Take(500).Max();
            return value / (maxVol > 0 ? maxVol : 1);
        }

        private double CalculatePricePosition()
        {
            double currentPrice = Source.Last();
            double maValue = ma.Result.Last();
            return (currentPrice - maValue) / maValue;
        }

        private bool IsVolatilityStable()
        {
            if (previousVolatility == 0) return true;
            
            double volatilityChange = Math.Abs(volatility.Result.Last() - previousVolatility) / previousVolatility;
            Print($"VolatilityChange: {volatilityChange:F4}");
            return volatilityChange < 0.15; // Relaxed from 0.1 to 0.15
        }

private (double longProb, double shortProb) CalculateProbabilities()
{
    distances.Clear();
    predictions.Clear();

    List<(double distance, int direction, double similarity)> neighborData = new List<(double, int, double)>();

    // Calculate future price direction for more bars to get better distribution
    for (int i = FUTURE_BARS; i < Math.Min(MAX_BARS_BACK - FUTURE_BARS, Bars.ClosePrices.Count - FUTURE_BARS); i++)
    {
        if (i % 2 != 0) continue; // Sample more frequently

        double distance = CalculateLorentzianDistance(i);
        double similarity = CalculatePatternSimilarity(i);
        
        if (similarity < MinPatternSimilarity) continue;
        
        int direction = DetermineDirection(i);
        if (direction != 0) // Only add non-zero directions
        {
            neighborData.Add((distance, direction, similarity));
        }
    }

    var nearestNeighbors = neighborData
        .OrderByDescending(x => x.similarity)
        .ThenBy(x => x.distance)
        .Take(NeighborsCount)
        .ToList();

    if (!nearestNeighbors.Any()) return (0, 0);

    double totalWeight = nearestNeighbors.Sum(x => x.similarity);
    double longProbability = nearestNeighbors.Where(x => x.direction == 1).Sum(x => x.similarity) / totalWeight;
    double shortProbability = nearestNeighbors.Where(x => x.direction == -1).Sum(x => x.similarity) / totalWeight;

    // Ensure the probabilities sum to 1
    double total = longProbability + shortProbability;
    if (total > 0)
    {
        longProbability /= total;
        shortProbability /= total;
    }

    return (longProbability, shortProbability);
}

        private double CalculateLorentzianDistance(int index)
        {
            double distance = 0;
            for (int i = 0; i < features.Count; i++)
            {
                double diff = Math.Abs(features[i] - GetHistoricalFeature(index, i));
                distance += Math.Log(1 + diff);
            }
            return distance;
        }

        private double CalculatePatternSimilarity(int index)
        {
            const int patternLength = 5;
            double similarity = 0;
            
            for (int i = 0; i < patternLength; i++)
            {
                if (index + i >= Bars.ClosePrices.Count) break;
                
                double currentDiff = Math.Abs(Source[i] - Source[index + i]);
                similarity += 1.0 / (1.0 + currentDiff);
            }
            
            return similarity / patternLength;
        }

        private double GetHistoricalFeature(int index, int featureIndex)
        {
            switch (featureIndex)
            {
                case 0: return NormalizeRsi(rsi.Result[index]);
                case 1: return NormalizeAdx(adx.ADX[index]);
                case 2: return (Source[index] - ma.Result[index]) / ma.Result[index];
                case 3: return NormalizeVolatility(volatility.Result[index]);
                default: return 0;
            }
        }

        private int DetermineDirection(int index)
        {
            if (index + FUTURE_BARS >= Bars.ClosePrices.Count)
                return 0;

            double currentPrice = Source[index];
            double futurePrice = Source[index + FUTURE_BARS];
            double priceChange = (futurePrice - currentPrice) / currentPrice;
            
            double volatilityThreshold = volatility.Result[index] * 0.5;
            
            if (Math.Abs(priceChange) < volatilityThreshold)
                return 0;
                
            return priceChange > 0 ? 1 : -1;
        }

private bool FilterConditions(TradeType tradeType)
{
    double currentPrice = Source.Last();
    double maValue = ma.Result.Last();
    
    bool trendFilter = true;
    Print($"TrendFilter: {trendFilter}");
    
    // Removed volatility average constraint
    bool volatilityFilter = true;
    Print($"VolatilityFilter: {volatilityFilter}");
    
    // Further lowered ADX requirement
    bool adxFilter = adx.ADX.Last() > 10;
    Print($"ADXFilter: {adxFilter}, ADX: {adx.ADX.Last()}");
    
    bool rsiFilter = tradeType == TradeType.Buy ? 
        rsi.Result.Last() > 35 && rsi.Result.Last() < 80 :
        rsi.Result.Last() < 65 && rsi.Result.Last() > 20;
    Print($"RSIFilter: {rsiFilter}, RSI: {rsi.Result.Last()}");

    return trendFilter && volatilityFilter && adxFilter && rsiFilter;
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