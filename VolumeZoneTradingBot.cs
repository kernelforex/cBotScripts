using System;
using cAlgo.API;
using cAlgo.API.Indicators;
using cAlgo.API.Internals;

namespace cAlgo.Robots
{
    [Robot(TimeZone = TimeZones.UTC, AccessRights = AccessRights.None)]
    public class VolumeZoneTradingBot : Robot
    {
        [Parameter("Period Length", DefaultValue = 20)]
        public int Length { get; set; }

        [Parameter("Stop Loss (pips)", DefaultValue = 20)]
        public double StopLossPips { get; set; }

        [Parameter("Take Profit (pips)", DefaultValue = 40)]
        public double TakeProfitPips { get; set; }

        private double[] volumeArray;
        private double[] cnvArray;
        private double[] cnvTbArray;
        private double previousCnvTb = 0;
        private bool inBullishZone = false;
        private bool inBearishZone = false;

        protected override void OnStart()
        {
            volumeArray = new double[Bars.Count];
            cnvArray = new double[Bars.Count];
            cnvTbArray = new double[Bars.Count];
        }

        protected override void OnTick()
        {
            CalculateVolumes();
            CheckForSignals();
        }

        private void CalculateVolumes()
        {
            int currentIndex = Bars.Count - 1;
            
            // Calculate Net Volume
            double priceChange = Bars.ClosePrices[currentIndex] - Bars.ClosePrices[currentIndex - 1];
            double netVolume = priceChange > 0 ? Bars.TickVolumes[currentIndex] : 
                             priceChange < 0 ? -Bars.TickVolumes[currentIndex] : 0;
            
            // Update arrays
            volumeArray[currentIndex] = netVolume;
            
            // Calculate Cumulative Net Volume
            double cumNetVolume = 0;
            for (int i = 0; i <= currentIndex; i++)
            {
                cumNetVolume += volumeArray[i];
                cnvArray[i] = cumNetVolume;
            }
            
            // Calculate CNV - SMA(CNV)
            double sma = 0;
            if (currentIndex >= Length)
            {
                for (int i = currentIndex - Length + 1; i <= currentIndex; i++)
                {
                    sma += cnvArray[i];
                }
                sma /= Length;
            }
            
            cnvTbArray[currentIndex] = cnvArray[currentIndex] - sma;
        }

        private void CheckForSignals()
        {
            int currentIndex = Bars.Count - 1;
            double currentCnvTb = cnvTbArray[currentIndex];

            // Check for zone transitions
            bool wasInBullishZone = inBullishZone;
            bool wasInBearishZone = inBearishZone;
            
            inBullishZone = currentCnvTb > 0;
            inBearishZone = currentCnvTb <= 0;

            // Check for trading signals
            if (wasInBullishZone && inBearishZone)
            {
                ExecuteSell();
            }
            else if (wasInBearishZone && inBullishZone)
            {
                ExecuteBuy();
            }

            previousCnvTb = currentCnvTb;
        }

        private void ExecuteBuy()
        {
            if (Positions.Find("VolumeZoneBot") == null)
            {
                var result = ExecuteMarketOrder(TradeType.Buy, SymbolName, 100000, // 1 lot = 100,000 units
                    "VolumeZoneBot", StopLossPips, TakeProfitPips);
                
                if (result.IsSuccessful)
                {
                    Print("Buy order executed at {0}", result.Position.EntryPrice);
                }
            }
        }

        private void ExecuteSell()
        {
            if (Positions.Find("VolumeZoneBot") == null)
            {
                var result = ExecuteMarketOrder(TradeType.Sell, SymbolName, 100000, // 1 lot = 100,000 units
                    "VolumeZoneBot", StopLossPips, TakeProfitPips);
                
                if (result.IsSuccessful)
                {
                    Print("Sell order executed at {0}", result.Position.EntryPrice);
                }
            }
        }
    }
}