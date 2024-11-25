using System;
using System.Linq;
using cAlgo.API;
using cAlgo.API.Indicators;
using cAlgo.API.Internals;

namespace cAlgo.Robots
{
    [Robot(TimeZone = TimeZones.UTC, AccessRights = AccessRights.None)]
    public class CombinedTradingBot : Robot
    {
        [Parameter("Bandwidth", DefaultValue = 45)]
        public int Bandwidth { get; set; }

        [Parameter("SD Lookback", DefaultValue = 150)]
        public int SdLookback { get; set; }

        [Parameter("SD Multiplier", DefaultValue = 2.0)]
        public double SdMultiplier { get; set; }

        [Parameter("Volume Period Length", DefaultValue = 20)]
        public int VolumeLength { get; set; }

        [Parameter("Stop Loss (pips)", DefaultValue = 35)]
        public int StopLossPips { get; set; }

        [Parameter("Take Profit (pips)", DefaultValue = 70)]
        public int TakeProfitPips { get; set; }

        [Parameter("Trade Volume (lots)", DefaultValue = 1.0)]
        public double TradeLots { get; set; }

        private MovingAverage epanechnikovMA;
        private MovingAverage logisticMA;
        private MovingAverage waveMA;
        private StandardDeviation stdDev;
        private double prevAverage;
        private double prevUpperBand;
        private double prevLowerBand;
        private double normalizedTradeVolume;

        private double[] volumeArray;
        private double[] cnvArray;
        private double[] cnvTbArray;
        private bool prevInBullishZone = false;
        private bool prevInBearishZone = false;
        private bool inBullishZone = false;
        private bool inBearishZone = false;
        private bool wasOversold = false;
        private bool wasOverbought = false;

        protected override void OnStart()
        {
            epanechnikovMA = Indicators.MovingAverage(Bars.ClosePrices, Bandwidth, MovingAverageType.Exponential);
            logisticMA = Indicators.MovingAverage(Bars.ClosePrices, Bandwidth, MovingAverageType.Weighted);
            waveMA = Indicators.MovingAverage(Bars.ClosePrices, Bandwidth, MovingAverageType.Simple);
            stdDev = Indicators.StandardDeviation(Bars.ClosePrices, SdLookback, MovingAverageType.Simple);

            InitializeArrays();
            
            // Convert lots to units
            double volumeInUnits = TradeLots * Symbol.LotSize;
            normalizedTradeVolume = Symbol.NormalizeVolumeInUnits(volumeInUnits, RoundingMode.ToNearest);
            Print($"Normalized trade volume: {normalizedTradeVolume} units ({TradeLots} lots)");
        }

        private void InitializeArrays()
        {
            int size = Math.Max(Bars.Count, VolumeLength * 2);
            volumeArray = new double[size];
            cnvArray = new double[size];
            cnvTbArray = new double[size];
        }

        protected override void OnTick()
        {
            if (Bars.Count < Math.Max(Math.Max(Bandwidth, SdLookback), VolumeLength) + 1)
                return;

            if (Bars.Count > volumeArray.Length)
            {
                InitializeArrays();
            }

            try
            {
                CalculateVolumes();
                UpdateTripleConfirmation();
                CheckForSignals();
                
                // Update previous zone states
                prevInBullishZone = inBullishZone;
                prevInBearishZone = inBearishZone;
            }
            catch (Exception ex)
            {
                Print($"Error in OnTick: {ex.Message}");
            }
        }

        private void CalculateVolumes()
        {
            int currentIndex = Math.Min(Bars.Count - 1, volumeArray.Length - 1);
            if (currentIndex < 1) return;

            double priceChange = Bars.ClosePrices[currentIndex] - Bars.ClosePrices[currentIndex - 1];
            volumeArray[currentIndex] = priceChange > 0 ? Bars.TickVolumes[currentIndex] : 
                                      priceChange < 0 ? -Bars.TickVolumes[currentIndex] : 0;

            double cumNetVolume = 0;
            for (int i = Math.Max(0, currentIndex - VolumeLength); i <= currentIndex; i++)
            {
                cumNetVolume += volumeArray[i];
                cnvArray[i] = cumNetVolume;
            }

            if (currentIndex >= VolumeLength)
            {
                double sma = 0;
                for (int i = currentIndex - VolumeLength + 1; i <= currentIndex; i++)
                {
                    sma += cnvArray[i];
                }
                sma /= VolumeLength;
                cnvTbArray[currentIndex] = cnvArray[currentIndex] - sma;

                inBullishZone = cnvTbArray[currentIndex] > 0;
                inBearishZone = cnvTbArray[currentIndex] <= 0;
            }
        }

        private void UpdateTripleConfirmation()
        {
            if (Bars.Count < Math.Max(Bandwidth, SdLookback) + 1) return;

            double currentAverage = (epanechnikovMA.Result.Last() + logisticMA.Result.Last() + waveMA.Result.Last()) / 3;
            double sd = stdDev.Result.Last();
            double currentUpperBand = currentAverage + (sd * SdMultiplier);
            double currentLowerBand = currentAverage - (sd * SdMultiplier);

            double currentPrice = Bars.ClosePrices.Last();
            double prevPrice = Bars.ClosePrices.Last(1);
            
            wasOversold = prevPrice <= prevLowerBand;
            wasOverbought = prevPrice >= prevUpperBand;

            prevAverage = currentAverage;
            prevUpperBand = currentUpperBand;
            prevLowerBand = currentLowerBand;
        }

        private void CheckForSignals()
        {
            if (!CanTrade()) return;

            double currentPrice = Bars.ClosePrices.Last();
            
            // Buy when price was oversold and we're transitioning from bearish to bullish zone
            if (wasOversold && prevInBearishZone && inBullishZone)
            {
                ExecuteBuy();
            }
            // Sell when price was overbought and we're transitioning from bullish to bearish zone
            else if (wasOverbought && prevInBullishZone && inBearishZone)
            {
                ExecuteSell();
            }
        }

        private void ExecuteBuy()
        {
            try
            {
                Print("Buy signal detected - Executing buy order...");
                var result = ExecuteMarketOrder(TradeType.Buy, SymbolName, normalizedTradeVolume, "Combined_Buy", StopLossPips, TakeProfitPips);
                if (result.IsSuccessful)
                    Print($"Buy order executed at {result.Position.EntryPrice}");
                else
                    Print($"Buy order failed: {result.Error}");
            }
            catch (Exception ex)
            {
                Print($"Error executing buy order: {ex.Message}");
            }
        }

        private void ExecuteSell()
        {
            try
            {
                Print("Sell signal detected - Executing sell order...");
                var result = ExecuteMarketOrder(TradeType.Sell, SymbolName, normalizedTradeVolume, "Combined_Sell", StopLossPips, TakeProfitPips);
                if (result.IsSuccessful)
                    Print($"Sell order executed at {result.Position.EntryPrice}");
                else
                    Print($"Sell order failed: {result.Error}");
            }
            catch (Exception ex)
            {
                Print($"Error executing sell order: {ex.Message}");
            }
        }

        private bool CanTrade()
        {
            var buyPositions = Positions.FindAll("Combined_Buy", SymbolName);
            var sellPositions = Positions.FindAll("Combined_Sell", SymbolName);
            return buyPositions.Length == 0 && sellPositions.Length == 0;
        }
    }
}