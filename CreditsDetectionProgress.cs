using System;
using System.Collections.Generic;
using System.Linq;

namespace EmbyCredits
{
    public class CreditsDetectionProgress
    {
        private const int MaxDictionarySize = 1000;
        
        public bool IsRunning { get; set; }
        public int TotalItems { get; set; }
        public int ProcessedItems { get; set; }
        public int SuccessfulItems { get; set; }
        public int FailedItems { get; set; }
        public string CurrentItem { get; set; } = string.Empty;
        public int CurrentItemProgress { get; set; }
        public DateTime? StartTime { get; set; }
        public DateTime? EndTime { get; set; }
        
        private Dictionary<string, string> _failureReasons = new Dictionary<string, string>();
        private Dictionary<string, string> _successDetails = new Dictionary<string, string>();
        private Dictionary<string, double> _confidenceScores = new Dictionary<string, double>();
        
        public Dictionary<string, string> FailureReasons 
        { 
            get => _failureReasons;
            set => _failureReasons = value;
        }
        
        public Dictionary<string, string> SuccessDetails 
        { 
            get => _successDetails;
            set => _successDetails = value;
        }
        
        public Dictionary<string, double> ConfidenceScores 
        { 
            get => _confidenceScores;
            set => _confidenceScores = value;
        }

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
            CleanupDictionaries();
        }
        
        private void CleanupDictionaries()
        {
            _failureReasons.Clear();
            _successDetails.Clear();
            _confidenceScores.Clear();
        }
        
        internal void CheckAndLimitDictionarySize()
        {
            if (_failureReasons.Count > MaxDictionarySize)
            {
                var toRemove = _failureReasons.Keys.Take(_failureReasons.Count - MaxDictionarySize).ToList();
                foreach (var key in toRemove)
                    _failureReasons.Remove(key);
            }
            
            if (_successDetails.Count > MaxDictionarySize)
            {
                var toRemove = _successDetails.Keys.Take(_successDetails.Count - MaxDictionarySize).ToList();
                foreach (var key in toRemove)
                    _successDetails.Remove(key);
            }
            
            if (_confidenceScores.Count > MaxDictionarySize)
            {
                var toRemove = _confidenceScores.Keys.Take(_confidenceScores.Count - MaxDictionarySize).ToList();
                foreach (var key in toRemove)
                    _confidenceScores.Remove(key);
            }
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
