using System;
using cAlgo.API;
using cAlgo.API.Indicators;
using cAlgo.API.Internals;

namespace cAlgo.Robots
{
    [Robot(TimeZone = TimeZones.UTC, AccessRights = AccessRights.None)]
    public class SqueezeMomentumBot : Robot
    {
        [Parameter("BB Length", DefaultValue = 20)]
        public int Length { get; set; }

        [Parameter("BB MultFactor", DefaultValue = 2.0)]
        public double MultBB { get; set; }

        [Parameter("KC Length", DefaultValue = 20)]
        public int LengthKC { get; set; }

        [Parameter("KC MultFactor", DefaultValue = 1.5)]
        public double MultKC { get; set; }

        [Parameter("Use True Range", DefaultValue = true)]
        public bool UseTrueRange { get; set; }

        [Parameter("Volume (Lots)", DefaultValue = 0.1)]
        public double Volume { get; set; }

        [Parameter("Stop Loss (Pips)", DefaultValue = 0, MinValue = 0)]
        public double StopLossPips { get; set; }

        [Parameter("Take Profit (Pips)", DefaultValue = 0, MinValue = 0)]
        public double TakeProfitPips { get; set; }

        private MovingAverage bbBasis;
        private StandardDeviation bbDev;
        private MovingAverage kcMA;
        private double[] values;

        protected override void OnStart()
        {
            bbBasis = Indicators.MovingAverage(MarketSeries.Close, Length, MovingAverageType.Simple);
            bbDev = Indicators.StandardDeviation(MarketSeries.Close, Length, MovingAverageType.Simple);
            kcMA = Indicators.MovingAverage(MarketSeries.Close, LengthKC, MovingAverageType.Simple);
            values = new double[MarketSeries.Close.Count];
            CalculateValues();
        }

        private bool IsValidValue(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

        private double GetRange(int index)
        {
            try
            {
                if (index < 0 || index >= MarketSeries.Close.Count - 1)
                    return 0;

                if (UseTrueRange)
                {
                    double highLow = MarketSeries.High[index] - MarketSeries.Low[index];
                    double highClose = Math.Abs(MarketSeries.High[index] - MarketSeries.Close[index]);
                    double lowClose = Math.Abs(MarketSeries.Low[index] - MarketSeries.Close[index]);
                    
                    return Math.Max(highLow, Math.Max(highClose, lowClose));
                }
                
                return MarketSeries.High[index] - MarketSeries.Low[index];
            }
            catch
            {
                return 0;
            }
        }

        private double CalculateKCValue(int index)
        {
            if (index < LengthKC) return 0;

            double sumRange = 0;
            int count = 0;

            for (int i = 0; i < LengthKC; i++)
            {
                if (index - i >= 0)
                {
                    sumRange += GetRange(index - i);
                    count++;
                }
            }

            return count > 0 ? sumRange / count : 0;
        }

        private void CalculateValues()
        {
            try
            {
                if (values.Length < MarketSeries.Close.Count)
                {
                    Array.Resize(ref values, MarketSeries.Close.Count);
                }

                int startIndex = Math.Max(LengthKC, values.Length - 100);
                int endIndex = MarketSeries.Close.Count - 1;

                for (int i = startIndex; i <= endIndex; i++)
                {
                    if (i < LengthKC) continue;

                    double highest = MarketSeries.High[i];
                    double lowest = MarketSeries.Low[i];

                    for (int j = 1; j < LengthKC && (i - j) >= 0; j++)
                    {
                        highest = Math.Max(highest, MarketSeries.High[i - j]);
                        lowest = Math.Min(lowest, MarketSeries.Low[i - j]);
                    }

                    double avgHL = (highest + lowest) / 2;
                    double avgClose = kcMA.Result[i];
                    double meanPrice = (avgHL + avgClose) / 2;
                    
                    double sumX = 0, sumY = 0, sumXY = 0, sumX2 = 0;
                    int validPoints = 0;

                    for (int j = 0; j < LengthKC && (i - j) >= 0; j++)
                    {
                        double x = j;
                        double y = MarketSeries.Close[i - j] - meanPrice;
                        sumX += x;
                        sumY += y;
                        sumXY += x * y;
                        sumX2 += x * x;
                        validPoints++;
                    }
                    
                    if (validPoints > 0)
                    {
                        double slope = (validPoints * sumXY - sumX * sumY) / (validPoints * sumX2 - sumX * sumX);
                        values[i] = slope;
                    }
                }
            }
            catch (Exception e)
            {
                Print("Error in CalculateValues: " + e.Message);
            }
        }

        private void ExecuteTradeWithSLTP(TradeType tradeType, string label)
        {
            try
            {
                double? stopLossPrice = null;
                double? takeProfitPrice = null;
                double pipSize = Symbol.PipSize;

                if (StopLossPips > 0)
                {
                    stopLossPrice = tradeType == TradeType.Buy ? 
                        Symbol.Bid - (StopLossPips * pipSize) : 
                        Symbol.Ask + (StopLossPips * pipSize);
                }

                if (TakeProfitPips > 0)
                {
                    takeProfitPrice = tradeType == TradeType.Buy ? 
                        Symbol.Bid + (TakeProfitPips * pipSize) : 
                        Symbol.Ask - (TakeProfitPips * pipSize);
                }

                var volume = Volume * 100000; // Convert to units
                ExecuteMarketOrder(tradeType, SymbolName, volume, label, stopLossPrice, takeProfitPrice);
            }
            catch (Exception e)
            {
                Print("Error executing trade: " + e.Message);
            }
        }

        protected override void OnTick()
        {
            try
            {
                int index = MarketSeries.Close.Count - 1;
                if (index < LengthKC || index < 1) return;

                double rangeMA = CalculateKCValue(index);

                double basisValue = bbBasis.Result[index];
                double devValue = bbDev.Result[index];
                double maValue = kcMA.Result[index];

                if (!IsValidValue(basisValue) || !IsValidValue(devValue) || !IsValidValue(maValue))
                {
                    Print("Invalid indicator values detected");
                    return;
                }

                double upperBB = basisValue + (MultBB * devValue);
                double lowerBB = basisValue - (MultBB * devValue);
                
                double upperKC = maValue + (MultKC * rangeMA);
                double lowerKC = maValue - (MultKC * rangeMA);

                bool sqzOn = (lowerBB > lowerKC) && (upperBB < upperKC);
                bool sqzOff = (lowerBB < lowerKC) && (upperBB > upperKC);
                
                if (index >= values.Length || (index - 1) >= values.Length)
                {
                    CalculateValues();
                    return;
                }

                double currentValue = values[index];
                double previousValue = values[index - 1];

                string barColor;
                if (currentValue > 0)
                {
                    barColor = currentValue > previousValue ? "lime" : "green";
                }
                else
                {
                    barColor = currentValue < previousValue ? "red" : "maroon";
                }

                var positions = Positions.FindAll("SqueezeMomentum");
                var currentPosition = positions.Length > 0 ? positions[0] : null;

                if (currentPosition == null)
                {
                    if (barColor == "lime" || barColor == "maroon")
                    {
                        ExecuteTradeWithSLTP(TradeType.Buy, "SqueezeMomentum");
                    }
                    else if (barColor == "green" || barColor == "red")
                    {
                        ExecuteTradeWithSLTP(TradeType.Sell, "SqueezeMomentum");
                    }
                }
                else
                {
                    if (currentPosition.TradeType == TradeType.Buy && (barColor == "green" || barColor == "red"))
                    {
                        ClosePosition(currentPosition);
                    }
                    else if (currentPosition.TradeType == TradeType.Sell && (barColor == "lime" || barColor == "maroon"))
                    {
                        ClosePosition(currentPosition);
                    }
                }
            }
            catch (Exception e)
            {
                Print("Error in OnTick: " + e.Message);
            }
        }

        protected override void OnBar()
        {
            try
            {
                CalculateValues();
            }
            catch (Exception e)
            {
                Print("Error in OnBar: " + e.Message);
            }
        }
    }
}