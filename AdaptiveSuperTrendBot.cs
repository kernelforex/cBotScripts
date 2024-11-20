using System;
using cAlgo.API;
using cAlgo.API.Indicators;
using System.Collections.Generic;
using System.Linq;

namespace cAlgo.Robots
{
    [Robot(TimeZone = TimeZones.UTC, AccessRights = AccessRights.None)]
    public class AdaptiveSuperTrendBot : Robot
    {
        [Parameter("ATR Length", DefaultValue = 10, Group = "SuperTrend Settings")]
        public int AtrLength { get; set; }

        [Parameter("SuperTrend Factor", DefaultValue = 3.0, Group = "SuperTrend Settings")]
        public double Factor { get; set; }

        [Parameter("Training Data Length", DefaultValue = 100, Group = "K-Means Settings")]
        public int TrainingDataPeriod { get; set; }

        [Parameter("High Volatility Percentile", DefaultValue = 0.75, Group = "K-Means Settings")]
        public double HighVolPercentile { get; set; }

        [Parameter("Medium Volatility Percentile", DefaultValue = 0.5, Group = "K-Means Settings")]
        public double MedVolPercentile { get; set; }

        [Parameter("Low Volatility Percentile", DefaultValue = 0.25, Group = "K-Means Settings")]
        public double LowVolPercentile { get; set; }

        [Parameter("Stop Loss (pips)", DefaultValue = 50)]
        public double StopLossPips { get; set; }

        [Parameter("Take Profit (pips)", DefaultValue = 100)]
        public double TakeProfitPips { get; set; }

        private AverageTrueRange atr;
        private double[] upperBand;
        private double[] lowerBand;
        private int[] direction;
        private double[] superTrend;
        private List<double> volatilityData;
        private int currentCluster;

        protected override void OnStart()
        {
            atr = Indicators.AverageTrueRange(AtrLength, MovingAverageType.Simple);
            volatilityData = new List<double>();
            
            upperBand = new double[Bars.Count];
            lowerBand = new double[Bars.Count];
            direction = new int[Bars.Count];
            superTrend = new double[Bars.Count];
        }

        protected override void OnTick()
        {
            int index = Bars.Count - 1;
            if (index < TrainingDataPeriod) return;

            UpdateVolatilityData();
            CalculateSuperTrend(index);
            HandleTrading(index);
        }

        private void UpdateVolatilityData()
        {
            volatilityData.Clear();
            for (int i = 0; i < TrainingDataPeriod; i++)
            {
                volatilityData.Add(atr.Result[i]);
            }
        }

        private void CalculateSuperTrend(int index)
        {
            double assignedCentroid = GetVolatilityCluster();
            double src = (Bars.HighPrices[index] + Bars.LowPrices[index]) / 2;
            
            upperBand[index] = src + Factor * assignedCentroid;
            lowerBand[index] = src - Factor * assignedCentroid;

            if (index > 0)
            {
                upperBand[index] = Bars.ClosePrices[index - 1] > upperBand[index - 1] ? 
                    Math.Min(upperBand[index], upperBand[index - 1]) : upperBand[index];
                    
                lowerBand[index] = Bars.ClosePrices[index - 1] < lowerBand[index - 1] ? 
                    Math.Max(lowerBand[index], lowerBand[index - 1]) : lowerBand[index];

                direction[index] = direction[index - 1];
                
                if (Bars.ClosePrices[index] > upperBand[index])
                    direction[index] = -1;
                else if (Bars.ClosePrices[index] < lowerBand[index])
                    direction[index] = 1;

                superTrend[index] = direction[index] == -1 ? lowerBand[index] : upperBand[index];
            }
        }

        private double GetVolatilityCluster()
        {
            var sortedVol = volatilityData.OrderBy(x => x).ToList();
            double highVol = sortedVol[(int)(sortedVol.Count * HighVolPercentile)];
            double medVol = sortedVol[(int)(sortedVol.Count * MedVolPercentile)];
            double lowVol = sortedVol[(int)(sortedVol.Count * LowVolPercentile)];

            double currentVol = atr.Result.Last();
            
            var distances = new[]
            {
                Math.Abs(currentVol - highVol),
                Math.Abs(currentVol - medVol),
                Math.Abs(currentVol - lowVol)
            };
            
            currentCluster = Array.IndexOf(distances, distances.Min());
            return new[] { highVol, medVol, lowVol }[currentCluster];
        }

        private void HandleTrading(int index)
        {
            if (index < 1) return;

            bool bullishCross = direction[index] < direction[index - 1];
            bool bearishCross = direction[index] > direction[index - 1];

            if (bullishCross && Positions.Count == 0)
            {
                ExecuteMarketOrder(TradeType.Buy, SymbolName, 1, "Adaptive SuperTrend Buy", 
                    StopLossPips, TakeProfitPips);
            }
            else if (bearishCross && Positions.Count == 0)
            {
                ExecuteMarketOrder(TradeType.Sell, SymbolName, 1, "Adaptive SuperTrend Sell", 
                    StopLossPips, TakeProfitPips);
            }
        }

        protected override void OnStop()
        {
            // Clean up resources if needed
        }
    }
}