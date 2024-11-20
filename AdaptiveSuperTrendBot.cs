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
        private Queue<double> upperBand;
        private Queue<double> lowerBand;
        private Queue<int> direction;
        private Queue<double> superTrend;
        private List<double> volatilityData;
        private int currentCluster;
        private const int QueueSize = 3; // Keep only recent values

        protected override void OnStart()
        {
            atr = Indicators.AverageTrueRange(AtrLength, MovingAverageType.Simple);
            volatilityData = new List<double>();
            
            upperBand = new Queue<double>(QueueSize);
            lowerBand = new Queue<double>(QueueSize);
            direction = new Queue<int>(QueueSize);
            superTrend = new Queue<double>(QueueSize);

            // Initialize queues
            for (int i = 0; i < QueueSize; i++)
            {
                upperBand.Enqueue(0);
                lowerBand.Enqueue(0);
                direction.Enqueue(1);
                superTrend.Enqueue(0);
            }
        }

        private void MaintainQueueSize(Queue<double> queue)
        {
            while (queue.Count > QueueSize)
                queue.Dequeue();
        }

        private void MaintainQueueSize(Queue<int> queue)
        {
            while (queue.Count > QueueSize)
                queue.Dequeue();
        }

        protected override void OnTick()
        {
            if (Bars.Count < TrainingDataPeriod + 1) return;

            UpdateVolatilityData();
            CalculateSuperTrend();
            HandleTrading();
        }

        private void UpdateVolatilityData()
        {
            volatilityData.Clear();
            for (int i = 0; i < Math.Min(TrainingDataPeriod, Bars.Count); i++)
            {
                if (atr.Result[i] > 0)
                    volatilityData.Add(atr.Result[i]);
            }
        }

        private void CalculateSuperTrend()
        {
            if (volatilityData.Count == 0) return;

            double assignedCentroid = GetVolatilityCluster();
            double src = (MarketSeries.High.Last(0) + MarketSeries.Low.Last(0)) / 2;
            
            double newUpperBand = src + Factor * assignedCentroid;
            double newLowerBand = src - Factor * assignedCentroid;

            // Adjust bands based on previous values
            if (Bars.ClosePrices.Last(1) > upperBand.Last())
                newUpperBand = Math.Min(newUpperBand, upperBand.Last());
            if (Bars.ClosePrices.Last(1) < lowerBand.Last())
                newLowerBand = Math.Max(newLowerBand, lowerBand.Last());

            // Update direction
            int newDirection = direction.Last();
            if (Bars.ClosePrices.Last(0) > upperBand.Last())
                newDirection = -1;
            else if (Bars.ClosePrices.Last(0) < lowerBand.Last())
                newDirection = 1;

            // Update queues
            upperBand.Enqueue(newUpperBand);
            lowerBand.Enqueue(newLowerBand);
            direction.Enqueue(newDirection);
            superTrend.Enqueue(newDirection == -1 ? newLowerBand : newUpperBand);

            // Maintain queue sizes
            MaintainQueueSize(upperBand);
            MaintainQueueSize(lowerBand);
            MaintainQueueSize(direction);
            MaintainQueueSize(superTrend);
        }

        private double GetVolatilityCluster()
        {
            if (volatilityData.Count == 0) return 0;

            var sortedVol = volatilityData.OrderBy(x => x).ToList();
            int highIndex = Math.Min((int)(sortedVol.Count * HighVolPercentile), sortedVol.Count - 1);
            int medIndex = Math.Min((int)(sortedVol.Count * MedVolPercentile), sortedVol.Count - 1);
            int lowIndex = Math.Min((int)(sortedVol.Count * LowVolPercentile), sortedVol.Count - 1);

            double highVol = sortedVol[highIndex];
            double medVol = sortedVol[medIndex];
            double lowVol = sortedVol[lowIndex];

            double currentVol = atr.Result.Last(0);
            
            var distances = new[]
            {
                Math.Abs(currentVol - highVol),
                Math.Abs(currentVol - medVol),
                Math.Abs(currentVol - lowVol)
            };
            
            currentCluster = Array.IndexOf(distances, distances.Min());
            return new[] { highVol, medVol, lowVol }[currentCluster];
        }

        private void HandleTrading()
        {
            if (direction.Count < 2) return;

            var dirArray = direction.ToArray();
            bool bullishCross = dirArray[dirArray.Length - 1] < dirArray[dirArray.Length - 2];
            bool bearishCross = dirArray[dirArray.Length - 1] > dirArray[dirArray.Length - 2];

            if (bullishCross && !HasOpenPosition())
            {
                ExecuteMarketOrder(TradeType.Buy, SymbolName, 1, "Adaptive SuperTrend Buy", 
                    StopLossPips, TakeProfitPips);
            }
            else if (bearishCross && !HasOpenPosition())
            {
                ExecuteMarketOrder(TradeType.Sell, SymbolName, 1, "Adaptive SuperTrend Sell", 
                    StopLossPips, TakeProfitPips);
            }
        }

        private bool HasOpenPosition()
        {
            return Positions.Count > 0;
        }

        protected override void OnStop()
        {
            // Cleanup if needed
        }
    }
}