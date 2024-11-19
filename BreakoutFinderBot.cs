using System;
using System.Collections.Generic;
using cAlgo.API;
using System.Linq;

namespace cAlgo.Robots
{
    [Robot(TimeZone = TimeZones.UTC, AccessRights = AccessRights.None)]
    public class BreakoutFinderBot : Robot
    {
        [Parameter("Period", DefaultValue = 5, MinValue = 2)]
        public int Period { get; set; }

        [Parameter("Max Breakout Length", DefaultValue = 200, MinValue = 30, MaxValue = 300)]
        public int MaxBreakoutLength { get; set; }

        [Parameter("Threshold Rate %", DefaultValue = 3.0, MinValue = 1.0, MaxValue = 10.0)]
        public double ThresholdRate { get; set; }

        [Parameter("Minimum Tests", DefaultValue = 2, MinValue = 1)]
        public int MinimumTests { get; set; }

        [Parameter("Stop Loss (Pips)", DefaultValue = 20, MinValue = 5)]
        public double StopLossPips { get; set; }

        [Parameter("Take Profit (Pips)", DefaultValue = 40, MinValue = 10)]
        public double TakeProfitPips { get; set; }

        private List<PivotPoint> HighPivots;
        private List<PivotPoint> LowPivots;
        private const int StandardLotSize = 100000;
        private bool isNewBar = false;
        private int lastProcessedBar = -1;

        protected override void OnStart()
        {
            HighPivots = new List<PivotPoint>();
            LowPivots = new List<PivotPoint>();
            Print("Bot started. Waiting for signals...");
        }

        protected override void OnBar()
        {
            int currentBar = Bars.Count - 1;
            if (currentBar != lastProcessedBar)
            {
                isNewBar = true;
                lastProcessedBar = currentBar;
            }
        }

        protected override void OnTick()
        {
            if (!isNewBar) return;
            isNewBar = false;

            UpdatePivots();
            CheckForBreakoutSignals();
            CleanupOldPivots();
        }

        private void UpdatePivots()
        {
            int currentIndex = Bars.Count - 1;

            if (IsPivotHigh(currentIndex - Period))
            {
                double pivotPrice = Bars.HighPrices[currentIndex - Period];
                HighPivots.Insert(0, new PivotPoint 
                { 
                    Price = pivotPrice,
                    Index = currentIndex - Period
                });
                Print($"New High Pivot found at price: {pivotPrice}");
            }

            if (IsPivotLow(currentIndex - Period))
            {
                double pivotPrice = Bars.LowPrices[currentIndex - Period];
                LowPivots.Insert(0, new PivotPoint 
                { 
                    Price = pivotPrice,
                    Index = currentIndex - Period
                });
                Print($"New Low Pivot found at price: {pivotPrice}");
            }
        }

        private bool IsPivotHigh(int index)
        {
            if (index < Period || index >= Bars.Count - Period)
                return false;

            double centerPrice = Bars.HighPrices[index];

            for (int i = 1; i <= Period; i++)
            {
                if (Bars.HighPrices[index - i] > centerPrice || 
                    Bars.HighPrices[index + i] > centerPrice)
                    return false;
            }

            return true;
        }

        private bool IsPivotLow(int index)
        {
            if (index < Period || index >= Bars.Count - Period)
                return false;

            double centerPrice = Bars.LowPrices[index];

            for (int i = 1; i <= Period; i++)
            {
                if (Bars.LowPrices[index - i] < centerPrice || 
                    Bars.LowPrices[index + i] < centerPrice)
                    return false;
            }

            return true;
        }

        private void CheckForBreakoutSignals()
        {
            if (Positions.Count > 0)
                return;

            int currentIndex = Bars.Count - 1;
            double channelWidth = CalculateChannelWidth();
            double currentClose = Bars.ClosePrices[currentIndex];
            double currentOpen = Bars.OpenPrices[currentIndex];
            double currentHigh = Bars.HighPrices[currentIndex];
            double currentLow = Bars.LowPrices[currentIndex];
            
            // Check for bullish breakout
            if (HighPivots.Count >= MinimumTests)
            {
                double resistanceLevel = GetResistanceLevel();
                int touchCount = CountResistanceTouches(resistanceLevel, channelWidth);

                Print($"Bullish Check - Resistance: {resistanceLevel}, Current High: {currentHigh}, Touches: {touchCount}");

                if (touchCount >= MinimumTests && currentHigh > resistanceLevel)
                {
                    Print($"Bullish breakout detected! Resistance: {resistanceLevel}, Touches: {touchCount}");
                    ExecuteBuyOrder(resistanceLevel);
                }
            }

            // Check for bearish breakout
            if (LowPivots.Count >= MinimumTests)
            {
                double supportLevel = GetSupportLevel();
                int touchCount = CountSupportTouches(supportLevel, channelWidth);

                Print($"Bearish Check - Support: {supportLevel}, Current Low: {currentLow}, Touches: {touchCount}");

                if (touchCount >= MinimumTests && currentLow < supportLevel)
                {
                    Print($"Bearish breakout detected! Support: {supportLevel}, Touches: {touchCount}");
                    ExecuteSellOrder(supportLevel);
                }
            }
        }

        private double GetResistanceLevel()
        {
            if (HighPivots.Count == 0) return 0;
            
            // Find the most recent significant resistance level
            var recentPivots = HighPivots.Take(10).ToList();
            double maxPivot = recentPivots.Max(p => p.Price);
            return maxPivot;
        }

        private double GetSupportLevel()
        {
            if (LowPivots.Count == 0) return double.MaxValue;
            
            // Find the most recent significant support level
            var recentPivots = LowPivots.Take(10).ToList();
            double minPivot = recentPivots.Min(p => p.Price);
            return minPivot;
        }

        private int CountResistanceTouches(double level, double channelWidth)
        {
            int touches = 0;
            double upperBound = level + (channelWidth / 2);
            double lowerBound = level - (channelWidth / 2);

            foreach (var pivot in HighPivots.Take(20))
            {
                if (pivot.Price >= lowerBound && pivot.Price <= upperBound)
                    touches++;
            }

            return touches;
        }

        private int CountSupportTouches(double level, double channelWidth)
        {
            int touches = 0;
            double upperBound = level + (channelWidth / 2);
            double lowerBound = level - (channelWidth / 2);

            foreach (var pivot in LowPivots.Take(20))
            {
                if (pivot.Price >= lowerBound && pivot.Price <= upperBound)
                    touches++;
            }

            return touches;
        }

        private double CalculateChannelWidth()
        {
            int barsToCheck = Math.Min(Math.Min(Bars.Count, 300), MaxBreakoutLength);
            double highest = Bars.HighPrices.Take(barsToCheck).Max();
            double lowest = Bars.LowPrices.Take(barsToCheck).Min();
            return (highest - lowest) * (ThresholdRate / 100);
        }

        private void ExecuteBuyOrder(double breakoutLevel)
        {
            try
            {
                var result = ExecuteMarketOrder(TradeType.Buy, Symbol.Name, StandardLotSize, "Breakout Long", StopLossPips, TakeProfitPips);
                if (result.IsSuccessful)
                {
                    Print($"Buy order executed successfully at {result.Position.EntryPrice}");
                }
                else
                {
                    Print($"Buy order failed: {result.Error}");
                }
            }
            catch (Exception ex)
            {
                Print($"Error executing buy order: {ex.Message}");
            }
        }

        private void ExecuteSellOrder(double breakoutLevel)
        {
            try
            {
                var result = ExecuteMarketOrder(TradeType.Sell, Symbol.Name, StandardLotSize, "Breakout Short", StopLossPips, TakeProfitPips);
                if (result.IsSuccessful)
                {
                    Print($"Sell order executed successfully at {result.Position.EntryPrice}");
                }
                else
                {
                    Print($"Sell order failed: {result.Error}");
                }
            }
            catch (Exception ex)
            {
                Print($"Error executing sell order: {ex.Message}");
            }
        }

        private void CleanupOldPivots()
        {
            int currentIndex = Bars.Count - 1;
            HighPivots.RemoveAll(p => currentIndex - p.Index > MaxBreakoutLength);
            LowPivots.RemoveAll(p => currentIndex - p.Index > MaxBreakoutLength);
        }

        private class PivotPoint
        {
            public double Price { get; set; }
            public int Index { get; set; }
        }
    }
}