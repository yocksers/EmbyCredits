using System;
using System.Collections.Generic;
using System.Linq;

namespace EmbyCredits
{
    public class CreditsDetectionProgress
    {
        private const int MaxDictionarySize = 5000;
        
        public bool IsRunning { get; set; }
        public int TotalItems { get; set; }
        public int ProcessedItems { get; set; }
        public int SuccessfulItems { get; set; }
        public int FailedItems { get; set; }
        public int SkippedItems { get; set; }
        public string CurrentItem { get; set; } = string.Empty;
        public int CurrentItemProgress { get; set; }
        public DateTime? StartTime { get; set; }
        public DateTime? EndTime { get; set; }
        public string CurrentMethod { get; set; } = string.Empty;
        public double AverageProcessingTimeSeconds { get; set; }
        
        private readonly List<double> _processingTimes = new List<double>();
        private DateTime? _currentItemStartTime;
        
        private Dictionary<string, string> _failureReasons = new Dictionary<string, string>();
        private Dictionary<string, string> _successDetails = new Dictionary<string, string>();
        private Dictionary<string, string> _skipReasons = new Dictionary<string, string>();
        private Dictionary<string, double> _confidenceScores = new Dictionary<string, double>();
        private Dictionary<string, string> _thumbnailPaths = new Dictionary<string, string>();
        private Dictionary<string, string> _episodeIds = new Dictionary<string, string>();
        
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
        
        public Dictionary<string, string> SkipReasons 
        { 
            get => _skipReasons;
            set => _skipReasons = value;
        }
        
        public Dictionary<string, double> ConfidenceScores 
        { 
            get => _confidenceScores;
            set => _confidenceScores = value;
        }

        public Dictionary<string, string> ThumbnailPaths 
        { 
            get => _thumbnailPaths;
            set => _thumbnailPaths = value;
        }

        public Dictionary<string, string> EpisodeIds 
        { 
            get => _episodeIds;
            set => _episodeIds = value;
        }

        public void Reset()
        {
            IsRunning = false;
            TotalItems = 0;
            ProcessedItems = 0;
            SuccessfulItems = 0;
            FailedItems = 0;
            SkippedItems = 0;
            CurrentItem = string.Empty;
            CurrentItemProgress = 0;
            StartTime = null;
            EndTime = null;
            CurrentMethod = string.Empty;
            AverageProcessingTimeSeconds = 0;
            _processingTimes.Clear();
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
        
        public void CompleteProcessingItem(bool success)
        {
            if (_currentItemStartTime.HasValue)
            {
                var elapsed = (DateTime.UtcNow - _currentItemStartTime.Value).TotalSeconds;
                _processingTimes.Add(elapsed);
                
                if (_processingTimes.Count > 100)
                {
                    _processingTimes.RemoveAt(0);
                }
                
                AverageProcessingTimeSeconds = _processingTimes.Average();
                _currentItemStartTime = null;
            }
            
            if (success)
            {
                SuccessfulItems++;
            }
            else
            {
                FailedItems++;
            }
            ProcessedItems++;
        }
        
        private void DeleteOldThumbnails()
        {
            try
            {
                if (_thumbnailPaths.Count == 0)
                    return;

                var pluginDataPath = Plugin.Instance?.AppPaths?.PluginConfigurationsPath;
                if (string.IsNullOrEmpty(pluginDataPath))
                    return;

                var thumbnailDir = System.IO.Path.Combine(pluginDataPath, "EmbyCredits", "Thumbnails");
                if (!System.IO.Directory.Exists(thumbnailDir))
                    return;

                foreach (var thumbnailFile in _thumbnailPaths.Values)
                {
                    try
                    {
                        var fullPath = System.IO.Path.Combine(thumbnailDir, thumbnailFile);
                        if (System.IO.File.Exists(fullPath))
                        {
                            System.IO.File.Delete(fullPath);
                        }
                    }
                    catch
                    {
                    }
                }
            }
            catch
            {
            }
        }
        
        private void CleanupDictionaries()
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
                
                try
                {
                    var pluginDataPath = Plugin.Instance?.AppPaths?.PluginConfigurationsPath;
                    if (!string.IsNullOrEmpty(pluginDataPath))
                    {
                        var thumbnailDir = System.IO.Path.Combine(pluginDataPath, "EmbyCredits", "Thumbnails");
                        foreach (var key in toRemove)
                        {
                            if (_thumbnailPaths.TryGetValue(key, out var thumbnailFile))
                            {
                                try
                                {
                                    var fullPath = System.IO.Path.Combine(thumbnailDir, thumbnailFile);
                                    if (System.IO.File.Exists(fullPath))
                                    {
                                        System.IO.File.Delete(fullPath);
                                    }
                                }
                                catch
                                {
                                }
                            }
                        }
                    }
                }
                catch
                {
                }
                
                foreach (var key in toRemove)
                    _thumbnailPaths.Remove(key);
            }
            
            if (_episodeIds.Count > MaxDictionarySize)
            {
                var toRemove = _episodeIds.Keys.Take(_episodeIds.Count - MaxDictionarySize).ToList();
                foreach (var key in toRemove)
                    _episodeIds.Remove(key);
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
