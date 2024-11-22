using System;
using cAlgo.API;
using cAlgo.API.Indicators;
using cAlgo.API.Internals;

namespace cAlgo.Robots
{
    [Robot(TimeZone = TimeZones.UTC, AccessRights = AccessRights.None)]
    public class CandlestickTrendTradingBot : Robot
    {
        [Parameter("MA Length", DefaultValue = 20)]
        public int maLength { get; set; }

        [Parameter("Order Volume (Lots)", DefaultValue = 0.01, MinValue = 0.01, Step = 0.01)]
        public double volumeInLots { get; set; }

        [Parameter("Bullish Threshold (%)", DefaultValue = 0.1, MinValue = 0.0)]
        public double bullishThreshold { get; set; }

        [Parameter("Bearish Threshold (%)", DefaultValue = 0.1, MinValue = 0.0)]
        public double bearishThreshold { get; set; }

        [Parameter("Reversal Threshold (%)", DefaultValue = 0.05, MinValue = 0.0)]
        public double reversalThreshold { get; set; }

        [Parameter("Take Profit (Pips)", DefaultValue = 1, MinValue = 0.1)]
        public double takeProfitPips { get; set; }

        [Parameter("Stop Loss (Pips)", DefaultValue = 2, MinValue = 0)]
        public double stopLossPips { get; set; }

        private MovingAverage hullMA;
        private double previousMA;
        private Position currentPosition;
        private bool canTrade = true;
        private double entryPrice = 0;
        private DateTime lastTradeTime;

        protected override void OnStart()
        {
            hullMA = Indicators.MovingAverage(Bars.ClosePrices, maLength, MovingAverageType.Hull);
            previousMA = hullMA.Result.Last(1);
            lastTradeTime = DateTime.MinValue;
        }

        protected override void OnTick()
        {
            if (!canTrade) return;

            var currentBar = Bars.LastBar;
            
            if (currentBar.OpenTime <= lastTradeTime) return;
            
            double priceMovement = Math.Abs(currentBar.Close - currentBar.Open) / currentBar.Open * 100;
            
            if (currentPosition != null && stopLossPips > 0)
            {
                double currentMovement = 0;
                if (currentPosition.TradeType == TradeType.Buy)
                {
                    currentMovement = (currentBar.Close - entryPrice) / entryPrice * 100;
                    if (currentMovement < -reversalThreshold)
                    {
                        ClosePosition();
                        canTrade = false;
                        return;
                    }
                }
                else
                {
                    currentMovement = (entryPrice - currentBar.Close) / entryPrice * 100;
                    if (currentMovement < -reversalThreshold)
                    {
                        ClosePosition();
                        canTrade = false;
                        return;
                    }
                }
            }

            if (currentPosition == null && canTrade)
            {
                try
                {
                    double volumeInUnits = Symbol.NormalizeVolumeInUnits(volumeInLots * 100000);
                    
                    if (currentBar.Close > currentBar.Open && priceMovement >= bullishThreshold)
                    {
                        var result = ExecuteMarketOrder(TradeType.Buy, SymbolName, volumeInUnits, "BullishTrade", 
                            stopLossPips, takeProfitPips);
                        if (result.IsSuccessful)
                        {
                            entryPrice = result.Position.EntryPrice;
                            lastTradeTime = currentBar.OpenTime;
                        }
                    }
                    else if (currentBar.Close < currentBar.Open && priceMovement >= bearishThreshold)
                    {
                        var result = ExecuteMarketOrder(TradeType.Sell, SymbolName, volumeInUnits, "BearishTrade", 
                            stopLossPips, takeProfitPips);
                        if (result.IsSuccessful)
                        {
                            entryPrice = result.Position.EntryPrice;
                            lastTradeTime = currentBar.OpenTime;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Print($"Trade execution failed: {ex.Message}");
                }
            }
        }

        private void ClosePosition()
        {
            if (currentPosition != null)
            {
                ClosePosition(currentPosition);
                entryPrice = 0;
            }
            currentPosition = null;
        }

        protected override void OnPositionOpened(Position position)
        {
            currentPosition = position;
            Print($"Position opened at {Time} - Type: {position.TradeType}, Volume: {position.Volume}, Entry: {position.EntryPrice}");
        }

        protected override void OnPositionClosed(Position position)
        {
            if (currentPosition == position)
            {
                currentPosition = null;
                entryPrice = 0;
                Print($"Position closed at {Time} - P/L: {position.GrossProfit}");
            }
        }

        protected override void OnStop()
        {
            ClosePosition();
        }

        protected override void OnBar()
        {
            canTrade = true;
        }
    }
}