using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace EmbyCredits
{
    public class CreditsDetectionProgress
    {
        private const int MaxDictionarySize = 5000;
        
        public bool IsRunning { get; set; }

        private int _totalItems;
        private int _processedItems;
        private int _successfulItems;
        private int _failedItems;
        private int _skippedItems;

        public int TotalItems
        {
            get => Volatile.Read(ref _totalItems);
            set => Volatile.Write(ref _totalItems, value);
        }
        public int ProcessedItems
        {
            get => Volatile.Read(ref _processedItems);
            set => Volatile.Write(ref _processedItems, value);
        }
        public int SuccessfulItems
        {
            get => Volatile.Read(ref _successfulItems);
            set => Volatile.Write(ref _successfulItems, value);
        }
        public int FailedItems
        {
            get => Volatile.Read(ref _failedItems);
            set => Volatile.Write(ref _failedItems, value);
        }
        public int SkippedItems
        {
            get => Volatile.Read(ref _skippedItems);
            set => Volatile.Write(ref _skippedItems, value);
        }
        public string CurrentItem { get; set; } = string.Empty;
        public int CurrentItemProgress { get; set; }
        public DateTime? StartTime { get; set; }
        public DateTime? EndTime { get; set; }
        public string CurrentMethod { get; set; } = string.Empty;
        public double AverageProcessingTimeSeconds { get; set; }
        
        private readonly Queue<double> _processingTimes = new Queue<double>();
        private double _processingTimesSum = 0.0;
        private readonly object _processingTimesLock = new object();
        private readonly object _dictionariesLock = new object();
        private DateTime? _currentItemStartTime;
        
        private Dictionary<string, string> _failureReasons = new Dictionary<string, string>();
        private Dictionary<string, string> _successDetails = new Dictionary<string, string>();
        private Dictionary<string, string> _skipReasons = new Dictionary<string, string>();
        private Dictionary<string, double> _confidenceScores = new Dictionary<string, double>();
        private Dictionary<string, string> _thumbnailPaths = new Dictionary<string, string>();
        private Dictionary<string, string> _episodeIds = new Dictionary<string, string>();
        private Dictionary<string, string> _appliedRules = new Dictionary<string, string>();
        
        public Dictionary<string, string> FailureReasons 
        { 
            get => _failureReasons;
            set { lock (_dictionariesLock) { _failureReasons = value; } }
        }
        
        public Dictionary<string, string> SuccessDetails 
        { 
            get => _successDetails;
            set { lock (_dictionariesLock) { _successDetails = value; } }
        }
        
        public Dictionary<string, string> SkipReasons 
        { 
            get => _skipReasons;
            set { lock (_dictionariesLock) { _skipReasons = value; } }
        }
        
        public Dictionary<string, double> ConfidenceScores 
        { 
            get => _confidenceScores;
            set { lock (_dictionariesLock) { _confidenceScores = value; } }
        }

        public Dictionary<string, string> ThumbnailPaths 
        { 
            get => _thumbnailPaths;
            set { lock (_dictionariesLock) { _thumbnailPaths = value; } }
        }

        public Dictionary<string, string> EpisodeIds 
        { 
            get => _episodeIds;
            set { lock (_dictionariesLock) { _episodeIds = value; } }
        }

        public Dictionary<string, string> AppliedRules 
        { 
            get => _appliedRules;
            set { lock (_dictionariesLock) { _appliedRules = value; } }
        }

        public void Reset()
        {
            IsRunning = false;
            Volatile.Write(ref _totalItems, 0);
            Volatile.Write(ref _processedItems, 0);
            Volatile.Write(ref _successfulItems, 0);
            Volatile.Write(ref _failedItems, 0);
            Volatile.Write(ref _skippedItems, 0);
            CurrentItem = string.Empty;
            CurrentItemProgress = 0;
            StartTime = null;
            EndTime = null;
            CurrentMethod = string.Empty;
            AverageProcessingTimeSeconds = 0;
            lock (_processingTimesLock) 
            { 
                _processingTimes.Clear();
                _processingTimesSum = 0.0;
            }
            _currentItemStartTime = null;
            DeleteOldThumbnails();
            CleanupDictionaries();
        }
        
        public void StartProcessingItem(string itemName, string methodName)
        {
            CurrentItem = itemName;
            CurrentMethod = methodName;
            _currentItemStartTime = DateTime.UtcNow;
        }
        
        public void IncrementSkipped() => Interlocked.Increment(ref _skippedItems);

        public void CompleteSkippedItem()
        {
            Interlocked.Increment(ref _processedItems);
            CheckAndLimitDictionarySize();
        }

        public void CompleteProcessingItem(bool success)
        {
            if (_currentItemStartTime.HasValue)
            {
                var elapsed = (DateTime.UtcNow - _currentItemStartTime.Value).TotalSeconds;
                lock (_processingTimesLock)
                {
                    _processingTimesSum += elapsed;
                    _processingTimes.Enqueue(elapsed);
                    if (_processingTimes.Count > 100)
                        _processingTimesSum -= _processingTimes.Dequeue();
                    AverageProcessingTimeSeconds = _processingTimesSum / _processingTimes.Count;
                }
                _currentItemStartTime = null;
            }
            
            if (success)
                Interlocked.Increment(ref _successfulItems);
            else
                Interlocked.Increment(ref _failedItems);
            Interlocked.Increment(ref _processedItems);
            
            CheckAndLimitDictionarySize();
        }
        
        private void DeleteOldThumbnails()
        {
            try
            {
                var pluginDataPath = Plugin.Instance?.AppPaths?.PluginConfigurationsPath;
                if (string.IsNullOrEmpty(pluginDataPath))
                    return;

                var thumbnailDir = System.IO.Path.Combine(pluginDataPath, "EmbyCredits", "Thumbnails");
                if (!System.IO.Directory.Exists(thumbnailDir))
                    return;

                // Delete every file in the folder — thumbnails are only needed for the
                // active session's UI display, so anything on disk from a prior run
                // (including across server restarts) is stale and should be purged.
                foreach (var file in System.IO.Directory.GetFiles(thumbnailDir, "*.jpg"))
                {
                    try { System.IO.File.Delete(file); } catch { }
                }
            }
            catch
            {
            }
        }
        
        private void CleanupDictionaries()
        {
            lock (_dictionariesLock)
            {
                _failureReasons.Clear();
                _failureReasons.TrimExcess();
                _successDetails.Clear();
                _successDetails.TrimExcess();
                _skipReasons.Clear();
                _skipReasons.TrimExcess();
                _confidenceScores.Clear();
                _confidenceScores.TrimExcess();
                _thumbnailPaths.Clear();
                _thumbnailPaths.TrimExcess();
                _episodeIds.Clear();
                _episodeIds.TrimExcess();
                _appliedRules.Clear();
                _appliedRules.TrimExcess();
            }
        }
        
        internal void CheckAndLimitDictionarySize()
        {
            List<string>? thumbnailFilesToDelete = null;

            lock (_dictionariesLock)
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

                if (_skipReasons.Count > MaxDictionarySize)
                {
                    var toRemove = _skipReasons.Keys.Take(_skipReasons.Count - MaxDictionarySize).ToList();
                    foreach (var key in toRemove)
                        _skipReasons.Remove(key);
                }

                if (_confidenceScores.Count > MaxDictionarySize)
                {
                    var toRemove = _confidenceScores.Keys.Take(_confidenceScores.Count - MaxDictionarySize).ToList();
                    foreach (var key in toRemove)
                        _confidenceScores.Remove(key);
                }

                if (_thumbnailPaths.Count > MaxDictionarySize)
                {
                    var toRemove = _thumbnailPaths.Keys.Take(_thumbnailPaths.Count - MaxDictionarySize).ToList();
                    thumbnailFilesToDelete = new List<string>(toRemove.Count);
                    foreach (var key in toRemove)
                    {
                        if (_thumbnailPaths.TryGetValue(key, out var thumbnailFile))
                            thumbnailFilesToDelete.Add(thumbnailFile);
                        _thumbnailPaths.Remove(key);
                    }
                }

                if (_episodeIds.Count > MaxDictionarySize)
                {
                    var toRemove = _episodeIds.Keys.Take(_episodeIds.Count - MaxDictionarySize).ToList();
                    foreach (var key in toRemove)
                        _episodeIds.Remove(key);
                }

                if (_appliedRules.Count > MaxDictionarySize)
                {
                    var toRemove = _appliedRules.Keys.Take(_appliedRules.Count - MaxDictionarySize).ToList();
                    foreach (var key in toRemove)
                        _appliedRules.Remove(key);
                }
            }

            if (thumbnailFilesToDelete != null && thumbnailFilesToDelete.Count > 0)
            {
                try
                {
                    var pluginDataPath = Plugin.Instance?.AppPaths?.PluginConfigurationsPath;
                    if (!string.IsNullOrEmpty(pluginDataPath))
                    {
                        var thumbnailDir = System.IO.Path.Combine(pluginDataPath, "EmbyCredits", "Thumbnails");
                        foreach (var thumbnailFile in thumbnailFilesToDelete)
                        {
                            try
                            {
                                var fullPath = System.IO.Path.Combine(thumbnailDir, thumbnailFile);
                                if (System.IO.File.Exists(fullPath))
                                    System.IO.File.Delete(fullPath);
                            }
                            catch
                            {
                            }
                        }
                    }
                }
                catch
                {
                }
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
