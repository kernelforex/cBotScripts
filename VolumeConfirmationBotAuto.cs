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

        [Parameter("Trade Volume (units)", DefaultValue = 100000)]
        public int BaseTradeVolume { get; set; }

        [Parameter("Min Stop Loss (pips)", DefaultValue = 5)]
        public int MinStopLossPips { get; set; }

        [Parameter("Max Stop Loss (pips)", DefaultValue = 50)]
        public int MaxStopLossPips { get; set; }

        [Parameter("Cooldown After Loss (minutes)", DefaultValue = 60)]
        public int CooldownMinutes { get; set; }

        [Parameter("Max Consecutive Losses", DefaultValue = 2)]
        public int MaxConsecutiveLosses { get; set; }

        private MovingAverage epanechnikovMA;
        private MovingAverage logisticMA;
        private MovingAverage waveMA;
        private StandardDeviation stdDev;
        private double prevAverage;
        private double prevUpperBand;
        private double prevLowerBand;
        private double tradeVolume;

        private double[] volumeArray;
        private double[] cnvArray;
        private double[] cnvTbArray;
        private bool prevInBullishZone = false;
        private bool prevInBearishZone = false;
        private bool inBullishZone = false;
        private bool inBearishZone = false;
        private bool wasOversold = false;
        private bool wasOverbought = false;

        // Loss prevention tracking
        private int consecutiveLosses = 0;
        private DateTime lastLossTime = DateTime.MinValue;
        private bool isInCooldown = false;

        // Zone tracking
        private DateTime zoneStartTime;
        private double zoneStartPrice;
        private double zoneLowPrice;
        private double zoneHighPrice;

        protected override void OnStart()
        {
            epanechnikovMA = Indicators.MovingAverage(Bars.ClosePrices, Bandwidth, MovingAverageType.Exponential);
            logisticMA = Indicators.MovingAverage(Bars.ClosePrices, Bandwidth, MovingAverageType.Weighted);
            waveMA = Indicators.MovingAverage(Bars.ClosePrices, Bandwidth, MovingAverageType.Simple);
            stdDev = Indicators.StandardDeviation(Bars.ClosePrices, SdLookback, MovingAverageType.Simple);

            InitializeArrays();
            InitializeZone();
            
            tradeVolume = Symbol.NormalizeVolumeInUnits(BaseTradeVolume, RoundingMode.ToNearest);
            Print($"Normalized trade volume: {tradeVolume} units");

            // Subscribe to position events for loss tracking
            Positions.Closed += OnPositionClosed;
        }

        private void OnPositionClosed(PositionClosedEventArgs args)
        {
            if (args.Position.Label.StartsWith("Combined_"))
            {
                if (args.Position.NetProfit < 0)
                {
                    consecutiveLosses++;
                    lastLossTime = Server.Time;
                    isInCooldown = true;
                    Print($"Position closed with loss. Consecutive losses: {consecutiveLosses}. Entering cooldown period.");
                }
                else
                {
                    // Reset consecutive losses on profitable trade
                    consecutiveLosses = 0;
                    isInCooldown = false;
                    Print("Profitable trade. Resetting consecutive loss counter.");
                }
            }
        }

        private void InitializeArrays()
        {
            int size = Math.Max(Bars.Count, VolumeLength * 2);
            volumeArray = new double[size];
            cnvArray = new double[size];
            cnvTbArray = new double[size];
        }

        private void InitializeZone()
        {
            zoneStartTime = Server.Time;
            zoneStartPrice = Bars.ClosePrices.Last();
            zoneLowPrice = zoneStartPrice;
            zoneHighPrice = zoneStartPrice;
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
                UpdateZonePrices();
                CalculateVolumes();
                UpdateTripleConfirmation();
                CheckForSignals();
                
                prevInBullishZone = inBullishZone;
                prevInBearishZone = inBearishZone;
            }
            catch (Exception ex)
            {
                Print($"Error in OnTick: {ex.Message}");
            }
        }

        private void UpdateZonePrices()
        {
            double currentHigh = Bars.HighPrices.Last();
            double currentLow = Bars.LowPrices.Last();
            
            zoneHighPrice = Math.Max(zoneHighPrice, currentHigh);
            zoneLowPrice = Math.Min(zoneLowPrice, currentLow);
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

                bool newBullishZone = cnvTbArray[currentIndex] > 0;
                bool newBearishZone = cnvTbArray[currentIndex] <= 0;

                if (newBullishZone != inBullishZone || newBearishZone != inBearishZone)
                {
                    Print($"Zone change after {(Server.Time - zoneStartTime).TotalMinutes:F1} minutes. " +
                          $"High: {zoneHighPrice:F5}, Low: {zoneLowPrice:F5}, Range: {(zoneHighPrice - zoneLowPrice) / Symbol.PipSize:F1} pips");
                    InitializeZone();
                }

                inBullishZone = newBullishZone;
                inBearishZone = newBearishZone;
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

            if (wasOversold && prevInBearishZone && inBullishZone)
            {
                ExecuteBuy();
            }
            else if (wasOverbought && prevInBullishZone && inBearishZone)
            {
                ExecuteSell();
            }
        }

        private bool CanTrade()
        {
            // Check existing positions
            var buyPositions = Positions.FindAll("Combined_Buy", SymbolName);
            var sellPositions = Positions.FindAll("Combined_Sell", SymbolName);
            
            // Check cooldown period
            if (isInCooldown)
            {
                TimeSpan timeSinceLastLoss = Server.Time - lastLossTime;
                if (timeSinceLastLoss.TotalMinutes >= CooldownMinutes)
                {
                    isInCooldown = false;
                    Print("Cooldown period ended. Trading enabled.");
                }
                else
                {
                    Print($"In cooldown. {(CooldownMinutes - timeSinceLastLoss.TotalMinutes):F1} minutes remaining.");
                }
            }

            // Check consecutive losses
            if (consecutiveLosses >= MaxConsecutiveLosses)
            {
                Print($"Maximum consecutive losses ({MaxConsecutiveLosses}) reached. Trading disabled until profitable trade.");
                return false;
            }

            return buyPositions.Length == 0 && 
                   sellPositions.Length == 0 && 
                   !isInCooldown;
        }

private void ExecuteBuy()
        {
            try
            {
                Print("Buy signal detected - Executing buy order...");
                
                double entryPrice = Symbol.Ask;
                double stopLossPrice = zoneLowPrice;
                double stopLossPips = Math.Abs(entryPrice - stopLossPrice) / Symbol.PipSize;
                
                // Validate stop loss
                if (stopLossPips < MinStopLossPips)
                {
                    stopLossPips = MinStopLossPips;
                    stopLossPrice = entryPrice - (MinStopLossPips * Symbol.PipSize);
                }
                else if (stopLossPips > MaxStopLossPips)
                {
                    stopLossPips = MaxStopLossPips;
                    stopLossPrice = entryPrice - (MaxStopLossPips * Symbol.PipSize);
                }
                
                double takeProfitPips = stopLossPips * 2;

                Print($"Entry: {entryPrice:F5}, Stop Loss: {stopLossPrice:F5}, Zone Low: {zoneLowPrice:F5}");
                Print($"SL: {stopLossPips:F1} pips, TP: {takeProfitPips:F1} pips");
                
                var result = ExecuteMarketOrder(TradeType.Buy, SymbolName, tradeVolume, "Combined_Buy", 
                    (int)Math.Ceiling(stopLossPips), (int)Math.Ceiling(takeProfitPips));
                
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
                
                double entryPrice = Symbol.Bid;
                double stopLossPrice = zoneHighPrice;
                double stopLossPips = Math.Abs(stopLossPrice - entryPrice) / Symbol.PipSize;
                
                // Validate stop loss
                if (stopLossPips < MinStopLossPips)
                {
                    stopLossPips = MinStopLossPips;
                    stopLossPrice = entryPrice + (MinStopLossPips * Symbol.PipSize);
                }
                else if (stopLossPips > MaxStopLossPips)
                {
                    stopLossPips = MaxStopLossPips;
                    stopLossPrice = entryPrice + (MaxStopLossPips * Symbol.PipSize);
                }
                
                double takeProfitPips = stopLossPips * 2;

                Print($"Entry: {entryPrice:F5}, Stop Loss: {stopLossPrice:F5}, Zone High: {zoneHighPrice:F5}");
                Print($"SL: {stopLossPips:F1} pips, TP: {takeProfitPips:F1} pips");
                
                var result = ExecuteMarketOrder(TradeType.Sell, SymbolName, tradeVolume, "Combined_Sell", 
                    (int)Math.Ceiling(stopLossPips), (int)Math.Ceiling(takeProfitPips));
                
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

        // private bool CanTrade()
        // {
        //     var buyPositions = Positions.FindAll("Combined_Buy", SymbolName);
        //     var sellPositions = Positions.FindAll("Combined_Sell", SymbolName);
        //     return buyPositions.Length == 0 && sellPositions.Length == 0;
        // }
    }
}