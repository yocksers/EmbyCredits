using System;
using System.Collections.Generic;

namespace EmbyCredits
{
    public class CreditsDetectionProgress
    {
        public bool IsRunning { get; set; }
        public int TotalItems { get; set; }
        public int ProcessedItems { get; set; }
        public int SuccessfulItems { get; set; }
        public int FailedItems { get; set; }
        public string CurrentItem { get; set; } = string.Empty;
        public int CurrentItemProgress { get; set; }
        public DateTime? StartTime { get; set; }
        public DateTime? EndTime { get; set; }
        public Dictionary<string, string> FailureReasons { get; set; } = new Dictionary<string, string>();
        public Dictionary<string, string> SuccessDetails { get; set; } = new Dictionary<string, string>();

        public void Reset()
        {
            IsRunning = false;
            TotalItems = 0;
            ProcessedItems = 0;
            SuccessfulItems = 0;
            FailedItems = 0;
            CurrentItem = string.Empty;
            CurrentItemProgress = 0;
            StartTime = null;
            EndTime = null;
            FailureReasons.Clear();
            SuccessDetails.Clear();
        }

        public int PercentComplete => TotalItems > 0 ? (int)((ProcessedItems / (double)TotalItems) * 100) : 0;

        public TimeSpan? EstimatedTimeRemaining
        {
            get
            {
                if (!StartTime.HasValue || ProcessedItems == 0 || TotalItems == 0)
                    return null;

                var elapsed = DateTime.Now - StartTime.Value;
                var avgTimePerItem = elapsed.TotalSeconds / ProcessedItems;
                var remainingItems = TotalItems - ProcessedItems;
                return TimeSpan.FromSeconds(avgTimePerItem * remainingItems);
            }
        }
    }
}
