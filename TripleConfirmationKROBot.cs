using System;
using System.Linq;
using cAlgo.API;
using cAlgo.API.Indicators;
using cAlgo.API.Internals;

namespace cAlgo.Robots
{
    [Robot(TimeZone = TimeZones.UTC, AccessRights = AccessRights.None)]
    public class TripleConfirmationKROBot : Robot
    {
        [Parameter("Bandwidth", DefaultValue = 45)]
        public int Bandwidth { get; set; }

        [Parameter("SD Lookback", DefaultValue = 150)]
        public int SdLookback { get; set; }

        [Parameter("SD Multiplier", DefaultValue = 2.0)]
        public double SdMultiplier { get; set; }

        [Parameter("Stop Loss (pips)", DefaultValue = 50)]
        public int StopLossPips { get; set; }

        [Parameter("Take Profit (pips)", DefaultValue = 100)]
        public int TakeProfitPips { get; set; }

        private MovingAverage epanechnikovMA;
        private MovingAverage logisticMA;
        private MovingAverage waveMA;
        private StandardDeviation stdDev;
        private double prevAverage;
        private double prevUpperBand;
        private double prevLowerBand;
        private double tradeVolume;

        protected override void OnStart()
        {
            // Initialize indicators
            epanechnikovMA = Indicators.MovingAverage(Bars.ClosePrices, Bandwidth, MovingAverageType.Exponential);
            logisticMA = Indicators.MovingAverage(Bars.ClosePrices, Bandwidth, MovingAverageType.Weighted);
            waveMA = Indicators.MovingAverage(Bars.ClosePrices, Bandwidth, MovingAverageType.Simple);
            stdDev = Indicators.StandardDeviation(Bars.ClosePrices, SdLookback, MovingAverageType.Simple);

            // Set proper volume based on symbol requirements
            tradeVolume = Symbol.NormalizeVolumeInUnits(100000, RoundingMode.ToNearest); // 1.0 lot = 100,000 units
            Print($"Normalized trade volume: {tradeVolume} units");
        }

        protected override void OnBar()
        {
            // Wait for enough bars to calculate indicators
            if (Bars.Count < Math.Max(Bandwidth, SdLookback) + 1)
                return;

            // Store current values
            double currentAverage = (epanechnikovMA.Result.Last() + logisticMA.Result.Last() + waveMA.Result.Last()) / 3;
            double sd = stdDev.Result.Last();
            double currentUpperBand = currentAverage + (sd * SdMultiplier);
            double currentLowerBand = currentAverage - (sd * SdMultiplier);

            // Check for trading conditions only if we have previous values
            if (prevAverage != 0)
            {
                if (CanTrade())
                {
                    CheckForBuySignal(currentLowerBand, prevLowerBand);
                    CheckForSellSignal(currentUpperBand, prevUpperBand);
                }
            }

            // Update previous values for next bar
            prevAverage = currentAverage;
            prevUpperBand = currentUpperBand;
            prevLowerBand = currentLowerBand;
        }

        private void CheckForBuySignal(double currentLowerBand, double prevLowerBand)
        {
            double currentPrice = Bars.ClosePrices.Last();
            double prevPrice = Bars.ClosePrices.Last(1);

            bool wasOversold = prevPrice <= prevLowerBand;
            bool priceReversal = currentPrice > currentLowerBand;

            if (wasOversold && priceReversal)
            {
                try
                {
                    var result = ExecuteMarketOrder(TradeType.Buy, SymbolName, tradeVolume, "KRO_Buy", StopLossPips, TakeProfitPips);
                    if (result.IsSuccessful)
                        Print($"Buy order executed successfully at {result.Position.EntryPrice}");
                    else
                        Print($"Buy order failed: {result.Error}");
                }
                catch (Exception ex)
                {
                    Print($"Error executing buy order: {ex.Message}");
                }
            }
        }

        private void CheckForSellSignal(double currentUpperBand, double prevUpperBand)
        {
            double currentPrice = Bars.ClosePrices.Last();
            double prevPrice = Bars.ClosePrices.Last(1);

            bool wasOverbought = prevPrice >= prevUpperBand;
            bool priceReversal = currentPrice < currentUpperBand;

            if (wasOverbought && priceReversal)
            {
                try
                {
                    var result = ExecuteMarketOrder(TradeType.Sell, SymbolName, tradeVolume, "KRO_Sell", StopLossPips, TakeProfitPips);
                    if (result.IsSuccessful)
                        Print($"Sell order executed successfully at {result.Position.EntryPrice}");
                    else
                        Print($"Sell order failed: {result.Error}");
                }
                catch (Exception ex)
                {
                    Print($"Error executing sell order: {ex.Message}");
                }
            }
        }

        private bool CanTrade()
        {
            var buyPositions = Positions.FindAll("KRO_Buy", SymbolName);
            var sellPositions = Positions.FindAll("KRO_Sell", SymbolName);
            return buyPositions.Length == 0 && sellPositions.Length == 0;
        }

        protected override void OnStop()
        {
            // Cleanup if needed
        }
    }
}