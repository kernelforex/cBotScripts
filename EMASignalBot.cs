using System;
using cAlgo.API;
using cAlgo.API.Indicators;

namespace cAlgo.Robots
{
    [Robot(TimeZone = TimeZones.UTC, AccessRights = AccessRights.None)]
    public class EMASignalBot : Robot
    {
        [Parameter("1st EMA Period", DefaultValue = 5, MinValue = 5, MaxValue = 250)]
        public int FirstEmaPeriod { get; set; }

        [Parameter("2nd EMA Period", DefaultValue = 21, MinValue = 10, MaxValue = 250)]
        public int SecondEmaPeriod { get; set; }

        [Parameter("Position Volume (Lots)", DefaultValue = 1.0, MinValue = 0.1)]
        public double Volume { get; set; }

        private ExponentialMovingAverage ema1;
        private ExponentialMovingAverage ema2;
        private int lastBarIndex = -1;

        protected override void OnStart()
        {
            ema1 = Indicators.ExponentialMovingAverage(Bars.ClosePrices, FirstEmaPeriod);
            ema2 = Indicators.ExponentialMovingAverage(Bars.ClosePrices, SecondEmaPeriod);
        }

        protected override void OnBar()
        {
            if (lastBarIndex == Bars.ClosePrices.Count - 1)
                return;

            lastBarIndex = Bars.ClosePrices.Count - 1;

            double avgPrice = (Bars.OpenPrices.Last(1) + Bars.HighPrices.Last(1) + 
                             Bars.LowPrices.Last(1) + Bars.ClosePrices.Last(1)) / 4;

            double ema1Value = ema1.Result.Last(1);
            double ema2Value = ema2.Result.Last(1);
            double ema1ValuePrev = ema1.Result.Last(2);
            double ema2ValuePrev = ema2.Result.Last(2);

            bool buySignal = ema1Value < avgPrice && ema2Value < avgPrice && 
                           ema2Value < ema1Value && 
                           ema2ValuePrev > ema1ValuePrev;

            bool sellSignal = ema1Value > avgPrice && ema2Value > avgPrice && 
                            ema2Value > ema1Value && 
                            ema2ValuePrev < ema1ValuePrev;

            // Close existing positions if opposite signal appears
            foreach (var position in Positions)
            {
                if ((position.TradeType == TradeType.Buy && sellSignal) ||
                    (position.TradeType == TradeType.Sell && buySignal))
                {
                    ClosePosition(position);
                }
            }

            // Open new position if no positions exist
            if (Positions.Count == 0)
            {
                if (buySignal)
                {
                    ExecuteMarketOrder(TradeType.Buy, SymbolName, Volume * 100000, "Buy Signal");
                }
                else if (sellSignal)
                {
                    ExecuteMarketOrder(TradeType.Sell, SymbolName, Volume * 100000, "Sell Signal");
                }
            }
        }

        protected override void OnStop()
        {
            foreach (var position in Positions)
            {
                ClosePosition(position);
            }
        }
    }
}