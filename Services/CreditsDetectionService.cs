using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using EmbyCredits.Services;

namespace EmbyCredits.Services
{
    public static class CreditsDetectionService
    {
        private static ILogger? _logger;
        private static IApplicationPaths? _appPaths;
        private static PluginConfiguration? _configuration;
        private static ILibraryManager? _libraryManager;
        private static IItemRepository? _itemRepository;
        private static IFfmpegManager? _ffmpegManager;
        private static bool _isRunning;
        private static ConcurrentDictionary<string, DateTime> _processedEpisodes = new ConcurrentDictionary<string, DateTime>();
        private static ConcurrentQueue<Episode> _processingQueue = new ConcurrentQueue<Episode>();
        private static SemaphoreSlim _processingSemaphore = new SemaphoreSlim(1, 1);
        private static bool _isProcessing = false;
        private static bool _cancellationRequested = false;
        private static CancellationTokenSource? _cancellationTokenSource = null;
        private static bool _isDryRun = false;

        private const int MaxQueueSize = 1000;
        private const int MaxProcessedEpisodesCache = 10000;
        private const int MaxBatchDetectionCacheSize = 5000;
        private const int MaxEpisodeStatusMessagesCache = 1000;

        private static DetectionCoordinator? _detectionCoordinator;
        private static DebugLogger? _debugLogger;
        private static ChapterMarkerService? _chapterMarkerService;
        private static EpisodeProcessor? _episodeProcessor;
        private static PluginCoordinationService? _pluginCoordination;

        private static ConcurrentDictionary<string, List<(string method, double timestamp)>> _batchDetectionCache = new ConcurrentDictionary<string, List<(string method, double timestamp)>>();
        private static bool _isBatchMode = false;
        
        private static ConcurrentDictionary<string, List<string>> _episodeStatusMessages = new ConcurrentDictionary<string, List<string>>();

        private static void LogInfo(string message)
        {
            _debugLogger?.LogInfo(message);
        }

        private static void LogDebug(string message)
        {
            _debugLogger?.LogDebug(message);
        }

        private static void LogWarn(string message)
        {
            _debugLogger?.LogWarn(message);
        }

        private static void LogError(string message, Exception? ex = null)
        {
            _debugLogger?.LogError(message, ex);
        }

        public static void LogToDebug(string level, string message)
        {
            _debugLogger?.LogToDebug(level, message);
        }
        
        public static void AddEpisodeStatusMessage(string episodeId, string message)
        {
            var messages = _episodeStatusMessages.GetOrAdd(episodeId, _ => new List<string>());
            lock (messages)
            {
                messages.Add(message);
            }
            
            CleanupEpisodeStatusMessages();
        }
        
        private static List<string> GetAndClearEpisodeStatusMessages(string episodeId)
        {
            if (_episodeStatusMessages.TryRemove(episodeId, out var messages))
            {
                return messages;
            }
            return new List<string>();
        }

        public static bool IsDebugMode => _debugLogger?.IsDebugMode ?? false;

        public static void Start(ILogger logger, IApplicationPaths appPaths, PluginConfiguration configuration)
        {
            _logger = logger;
            _appPaths = appPaths;
            _configuration = configuration;
            _isRunning = true;

            _detectionCoordinator?.Dispose();
            _detectionCoordinator = new DetectionCoordinator(_logger, _configuration);

            _debugLogger?.Dispose();
            _debugLogger = new DebugLogger(_logger, configuration);
            _pluginCoordination?.Dispose();
            _pluginCoordination = new PluginCoordinationService(_logger, configuration);
            
            if (_itemRepository != null)
            {
                _chapterMarkerService = new ChapterMarkerService(_logger, _itemRepository);
                _episodeProcessor = new EpisodeProcessor(_logger, _libraryManager, _detectionCoordinator, 
                    _chapterMarkerService, _debugLogger, configuration);
            }

            _logger.Info("Credits Detection Service started");

            if (_libraryManager != null && configuration.EnableAutoDetection)
            {
                _libraryManager.ItemAdded += OnItemAdded;
                _logger.Info("Auto-detection enabled: ItemAdded event handler registered");
            }
        }

        public static void UpdateConfiguration(PluginConfiguration configuration)
        {
            var previousAutoDetectionState = _configuration?.EnableAutoDetection ?? false;
            _configuration = configuration;
            _logger?.Info("Credits Detection Service configuration updated");
            LogInfo($"Text Detection Enabled: {configuration.EnableTextDetection}");
            LogInfo($"Text Detection Threshold: {configuration.TextDetectionThreshold}");
            LogInfo($"Text Detection MinLines: {configuration.TextDetectionMinLines}");
            LogInfo($"Text Detection SearchStart: {configuration.TextDetectionSearchStart}");

            if (_logger != null && _appPaths != null)
            {
                _detectionCoordinator?.Dispose();
                _detectionCoordinator = new DetectionCoordinator(_logger, configuration);

                _debugLogger?.Dispose();
                _debugLogger = new DebugLogger(_logger, configuration);
                _pluginCoordination?.Dispose();
                _pluginCoordination = new PluginCoordinationService(_logger, configuration);
                
                if (_itemRepository != null)
                {
                    _chapterMarkerService = new ChapterMarkerService(_logger, _itemRepository);
                    _episodeProcessor = new EpisodeProcessor(_logger, _libraryManager, _detectionCoordinator, 
                        _chapterMarkerService, _debugLogger, configuration);
                }
            }

            if (_libraryManager != null && _isRunning)
            {
                if (configuration.EnableAutoDetection != previousAutoDetectionState)
                {
                    _libraryManager.ItemAdded -= OnItemAdded;
                    
                    if (configuration.EnableAutoDetection)
                    {
                        _libraryManager.ItemAdded += OnItemAdded;
                        _logger?.Info("Auto-detection enabled: ItemAdded event handler registered");
                    }
                    else
                    {
                        _logger?.Info("Auto-detection disabled: ItemAdded event handler unregistered");
                    }
                }
            }
        }

        public static void ClearCache()
        {
            try
            {
                _detectionCoordinator?.ClearCache();
                
                if (_batchDetectionCache.Count > MaxBatchDetectionCacheSize)
                {
                    System.Threading.Interlocked.Exchange(ref _batchDetectionCache, new ConcurrentDictionary<string, List<(string method, double timestamp)>>());
                }
                else
                {
                    _batchDetectionCache.Clear();
                }
                
                CleanupOldProcessedEpisodes();
                
                while (_processingQueue.TryDequeue(out _)) { }
                
                _episodeStatusMessages.Clear();
                System.Threading.Interlocked.Exchange(ref _episodeStatusMessages, new ConcurrentDictionary<string, List<string>>());
                
                _logger?.Info("Cleared in-memory batch detection cache and processing queue for fresh detection");
            }
            catch (Exception ex)
            {
                LogError("Error clearing cache", ex);
            }
        }

        private static void CleanupOldProcessedEpisodes()
        {
            if (_processedEpisodes.Count <= MaxProcessedEpisodesCache)
                return;

            var cutoffTime = DateTime.UtcNow.AddDays(-7);
            var keysToRemove = _processedEpisodes
                .Where(kvp => kvp.Value < cutoffTime)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var key in keysToRemove)
            {
                _processedEpisodes.TryRemove(key, out _);
            }

            if (_processedEpisodes.Count > MaxProcessedEpisodesCache)
            {
                var oldestEntries = _processedEpisodes
                    .OrderBy(kvp => kvp.Value)
                    .Take(_processedEpisodes.Count - MaxProcessedEpisodesCache)
                    .Select(kvp => kvp.Key)
                    .ToList();

                foreach (var key in oldestEntries)
                {
                    _processedEpisodes.TryRemove(key, out _);
                }
            }
        }

        private static void CleanupBatchDetectionCache()
        {
            if (_batchDetectionCache.Count <= MaxBatchDetectionCacheSize)
                return;

            var entriesToRemove = _batchDetectionCache.Count - MaxBatchDetectionCacheSize;
            var removed = 0;
            
            foreach (var key in _batchDetectionCache.Keys)
            {
                if (removed >= entriesToRemove)
                    break;
                    
                if (_batchDetectionCache.TryRemove(key, out _))
                    removed++;
            }
            
            LogDebug($"Cleaned up batch detection cache: removed {removed} entries");
        }
        
        private static void CleanupEpisodeStatusMessages()
        {
            if (_episodeStatusMessages.Count <= MaxEpisodeStatusMessagesCache)
                return;

            var entriesToRemove = _episodeStatusMessages.Count - MaxEpisodeStatusMessagesCache;
            var removed = 0;
            
            foreach (var key in _episodeStatusMessages.Keys)
            {
                if (removed >= entriesToRemove)
                    break;
                    
                if (_episodeStatusMessages.TryRemove(key, out _))
                    removed++;
            }
            
            LogDebug($"Cleaned up episode status messages cache: removed {removed} entries");
        }

        public static void Stop()
        {
            _isRunning = false;
            _isProcessing = false;

            if (_libraryManager != null)
            {
                try
                {
                    _libraryManager.ItemAdded -= OnItemAdded;
                }
                catch (Exception ex)
                {
                    LogError("Error unregistering ItemAdded event", ex);
                }
            }

            _detectionCoordinator?.Dispose();
            _detectionCoordinator = null;

            try
            {
                _processingSemaphore?.Dispose();
                _processingSemaphore = new SemaphoreSlim(1, 1);
            }
            catch (Exception ex)
            {
                LogError("Error disposing semaphore", ex);
            }
            
            _debugLogger?.Dispose();
            
            _pluginCoordination?.Dispose();
            
            while (_processingQueue.TryDequeue(out _)) { }
            
            // Recreate dictionaries to release capacity
            _batchDetectionCache.Clear();
            System.Threading.Interlocked.Exchange(ref _batchDetectionCache, new ConcurrentDictionary<string, List<(string method, double timestamp)>>());
            
            _processedEpisodes.Clear();
            System.Threading.Interlocked.Exchange(ref _processedEpisodes, new ConcurrentDictionary<string, DateTime>());
            
            _episodeStatusMessages.Clear();
            System.Threading.Interlocked.Exchange(ref _episodeStatusMessages, new ConcurrentDictionary<string, List<string>>());
            
            GC.Collect(2, GCCollectionMode.Forced, true);

            LogInfo("Credits Detection Service stopped");
        }

        private static void OnItemAdded(object? sender, ItemChangeEventArgs e)
        {
            if (!_isRunning || _configuration == null || !_configuration.EnableAutoDetection)
                return;

            try
            {
                if (e.Item is Episode episode)
                {
                    if (episode.IsVirtualItem)
                    {
                        LogDebug($"Skipping virtual item: {episode.SeriesName} - {episode.Name}");
                        return;
                    }

                    if (episode.ParentIndexNumber == null || episode.ParentIndexNumber == 0)
                    {
                        LogDebug($"Skipping TV special: {episode.SeriesName} - {episode.Name} (Season {episode.ParentIndexNumber})");
                        return;
                    }

                    var libraryIds = _configuration.LibraryIds ?? Array.Empty<string>();
                    if (libraryIds.Length > 0)
                    {
                        var topParent = episode.GetTopParent();
                        var internalId = topParent?.InternalId.ToString();
                        
                        LogDebug($"Episode {episode.Name} - TopParent: {topParent?.Name} (InternalId: {internalId}), Type: {topParent?.GetType().Name}, Configured libraries: [{string.Join(", ", libraryIds)}]");
                        
                        bool isInConfiguredLibrary = false;
                        if (!string.IsNullOrEmpty(internalId))
                        {
                            if (libraryIds.Contains(internalId))
                            {
                                isInConfiguredLibrary = true;
                            }
                            else if (long.TryParse(internalId, out var id) && id > 0)
                            {
                                var collectionId = (id - 1).ToString();
                                if (libraryIds.Contains(collectionId))
                                {
                                    isInConfiguredLibrary = true;
                                }
                            }
                        }
                        
                        if (!isInConfiguredLibrary)
                        {
                            LogDebug($"Skipping episode {episode.Name} - not in configured libraries");
                            return;
                        }
                    }

                    LogInfo($"New episode detected: {episode.SeriesName} - {episode.Name}");

                    var episodeId = episode.Id;
                    var episodeName = episode.Name;
                    var episodePath = episode.Path;

                    Task.Run(async () =>
                    {
                        try
                        {
                            await Task.Delay(15000).ConfigureAwait(false);
                            
                            if (_libraryManager == null)
                            {
                                LogWarn($"LibraryManager not available for delayed processing of {episodeName}");
                                return;
                            }

                            var refreshedEpisode = _libraryManager.GetItemById(episodeId) as Episode;
                            if (refreshedEpisode == null)
                            {
                                LogWarn($"Episode {episodeName} no longer exists after delay");
                                return;
                            }

                            if (string.IsNullOrEmpty(refreshedEpisode.Path))
                            {
                                LogDebug($"Episode {refreshedEpisode.Name} has no path after delay, skipping");
                                return;
                            }

                            if (!File.Exists(refreshedEpisode.Path))
                            {
                                LogDebug($"File does not exist for {refreshedEpisode.Name}: {refreshedEpisode.Path}");
                                return;
                            }

                            var fileInfo = new FileInfo(refreshedEpisode.Path);
                            if ((DateTime.UtcNow - fileInfo.LastWriteTimeUtc).TotalSeconds < 5)
                            {
                                LogDebug($"File still being modified: {refreshedEpisode.Name}");
                                return;
                            }

                            LogInfo($"Processing episode after delay: {refreshedEpisode.SeriesName} - {refreshedEpisode.Name}");
                            QueueEpisode(refreshedEpisode);
                        }
                        catch (Exception delayEx)
                        {
                            LogError($"Error in delayed episode processing: {episodeName}", delayEx);
                        }
                    }).ContinueWith(t =>
                    {
                        if (t.IsFaulted && t.Exception != null)
                        {
                            LogError($"Delayed episode processing failed: {episodeName}", t.Exception.GetBaseException());
                        }
                    }, TaskScheduler.Default);
                }
            }
            catch (Exception ex)
            {
                LogError($"Error handling ItemAdded event for {e.Item?.Name}", ex);
            }
        }

        public static void SetLibraryManager(ILibraryManager libraryManager)
        {
            _libraryManager = libraryManager;
        }

        public static void SetItemRepository(IItemRepository itemRepository)
        {
            _itemRepository = itemRepository;
        }

        public static void SetFfmpegManager(IFfmpegManager ffmpegManager)
        {
            _ffmpegManager = ffmpegManager;
        }

        public static void QueueEpisode(Episode episode, bool isManualDetection = false)
        {
            _cancellationRequested = false;

            var episodeId = episode.Id.ToString();
            LogDebug($"QueueEpisode called for: {episode.Name} (ID: {episodeId}), IsManual: {isManualDetection}");
            LogDebug($"Already processed: {_processedEpisodes.ContainsKey(episodeId)}, IsDryRun: {_isDryRun}, IsProcessing: {_isProcessing}");

            if (episode.ParentIndexNumber == null || episode.ParentIndexNumber == 0)
            {
                LogDebug($"Skipping TV special: {episode.SeriesName} - {episode.Name} (Season {episode.ParentIndexNumber})");
                return;
            }

            // Check if hash detection is enabled via rules or default config
            if (_configuration != null && _logger != null && episode.Series != null)
            {
                var ruleMatchingService = new RuleMatchingService(_logger, _configuration);
                var effectiveConfig = ruleMatchingService.GetEffectiveConfiguration(episode.Series);
                
                bool usesHashDetection = effectiveConfig.DetectionMode == DetectionMode.HashOnly ||
                                       effectiveConfig.DetectionMode == DetectionMode.HashWithOcrFallback ||
                                       effectiveConfig.DetectionMode == DetectionMode.OcrWithHashFallback;
                
                if (usesHashDetection && !_isBatchMode)
                {
                    LogInfo($"Hash detection enabled for '{episode.Series.Name}' - queueing entire season instead of single episode");
                    QueueSeasonForEpisode(episode, isManualDetection);
                    return;
                }
            }

            if (!isManualDetection)
            {
                if (_configuration != null && _itemRepository != null)
                {
                    var chapters = _itemRepository.GetChapters(episode);
                    var hasCreditsMarker = chapters.Any(c => 
                    {
                        var markerType = GetMarkerType(c);
                        return markerType == "CreditsStart";
                    });

                    LogDebug($"Episode has existing credits marker: {hasCreditsMarker}, ScheduledTaskOnlyProcessMissing: {_configuration.ScheduledTaskOnlyProcessMissing}");

                    if (hasCreditsMarker && _configuration.ScheduledTaskOnlyProcessMissing)
                    {
                        LogInfo($"Skipping episode {episode.Name} - already has credits marker (ScheduledTaskOnlyProcessMissing is enabled)");
                        
                        if (Plugin.Instance != null)
                        {
                            var series = episode.Series;
                            var episodeKey = series != null
                                ? $"{series.Name} S{episode.ParentIndexNumber:00}E{episode.IndexNumber:00}"
                                : episode.Name;
                            Plugin.Progress.SuccessDetails[episodeKey] = "(already exists)";
                            Plugin.Progress.SuccessfulItems++;
                        }
                        
                        return;
                    }
                }
            }
            else
            {
                if (_processedEpisodes.ContainsKey(episodeId))
                {
                    _processedEpisodes.TryRemove(episodeId, out _);
                    LogDebug($"Manual detection: Cleared {episode.Name} from processed episodes cache");
                }
            }

            if (true)
            {

                if (_processingQueue.Count >= MaxQueueSize)
                {
                    LogWarn($"Queue is full ({_processingQueue.Count} episodes). Skipping {episode.Name} to prevent memory issues.");
                    return;
                }

                _processingQueue.Enqueue(episode);
                LogInfo($"Queued episode: {episode.Name} (Queue size: {_processingQueue.Count})");

                if (!_isProcessing && Plugin.Instance != null)
                {
                    Plugin.Progress.Reset();
                    Plugin.Progress.IsRunning = true;
                    Plugin.Progress.TotalItems = 1;
                    Plugin.Progress.StartTime = DateTime.Now;

                    LogInfo("Starting ProcessQueue task");
                    _ = Task.Run(ProcessQueue).ContinueWith(t =>
                    {
                        if (t.IsFaulted && t.Exception != null)
                        {
                            LogError("ProcessQueue task failed", t.Exception.GetBaseException());
                        }
                    }, TaskScheduler.Default);
                }
                else if (_isProcessing && Plugin.Instance != null)
                {
                    Plugin.Progress.TotalItems++;
                    LogInfo($"Added to existing processing queue (total: {Plugin.Progress.TotalItems})");
                }
                else
                {
                    LogWarn($"Episode queued but not starting processing: isProcessing={_isProcessing}, PluginInstance={Plugin.Instance != null}");
                }
            }
        }

        private static void QueueSeasonForEpisode(Episode episode, bool isManualDetection)
        {
            if (_libraryManager == null || episode.Series == null || !episode.ParentIndexNumber.HasValue)
            {
                LogWarn($"Cannot queue season for episode - missing required data");
                QueueEpisode(episode, isManualDetection);
                return;
            }

            var series = episode.Series;
            var seasonNumber = episode.ParentIndexNumber.Value;
            
            LogInfo($"Fetching all episodes from {series.Name} Season {seasonNumber} for hash detection");
            
            var seriesInternalId = series.InternalId;
            
            var allEpisodes = _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { "Episode" },
                IsVirtualItem = false,
                HasPath = true,
                AncestorIds = new[] { seriesInternalId }
            }).OfType<Episode>().ToList();

            var seasonEpisodes = allEpisodes
                .Where(e => e.ParentIndexNumber == seasonNumber)
                .OrderBy(e => e.IndexNumber)
                .ToList();

            if (seasonEpisodes.Count == 0)
            {
                LogWarn($"No episodes found in season - falling back to single episode queue");
                QueueEpisode(episode, isManualDetection);
                return;
            }

            LogInfo($"Found {seasonEpisodes.Count} episodes in {series.Name} Season {seasonNumber} - queueing all for hash detection");
            
            if (isManualDetection)
            {
                QueueSeriesManual(seasonEpisodes, skipExistingMarkers: false);
            }
            else
            {
                QueueSeries(seasonEpisodes);
            }
        }

        public static void QueueSeries(List<Episode> episodes)
        {
            ClearCache();

            // Ensure batch cache is fully reset
            _batchDetectionCache.Clear();
            System.Threading.Interlocked.Exchange(ref _batchDetectionCache, new ConcurrentDictionary<string, List<(string method, double timestamp)>>());

            while (_processingQueue.TryDequeue(out _)) { }
            _cancellationRequested = false;
            _isProcessing = false;

            if (_detectionCoordinator == null && _logger != null && _configuration != null)
            {
                LogInfo("Initializing DetectionCoordinator");
                _detectionCoordinator?.Dispose();
                _detectionCoordinator = new DetectionCoordinator(_logger, _configuration);
            }

            var validEpisodes = episodes.Where(e => e.ParentIndexNumber != null && e.ParentIndexNumber != 0).ToList();
            var specialCount = episodes.Count - validEpisodes.Count;
            
            if (specialCount > 0)
            {
                LogInfo($"Filtered out {specialCount} specials from processing queue");
            }

            if (validEpisodes.Count == 0)
            {
                LogInfo("No valid episodes to process after filtering specials");
                return;
            }

            if (Plugin.Instance != null)
            {
                Plugin.Progress.Reset();
                Plugin.Progress.IsRunning = true;
                Plugin.Progress.TotalItems = validEpisodes.Count;
                Plugin.Progress.StartTime = DateTime.Now;
            }

            var queuedCount = 0;
            foreach (var episode in validEpisodes)
            {
                var episodeId = episode.Id.ToString();
                if (_processedEpisodes.ContainsKey(episodeId))
                {
                    _processedEpisodes.TryRemove(episodeId, out _);
                }
                _processingQueue.Enqueue(episode);
                queuedCount++;
            }

            LogInfo($"Queued {queuedCount} episodes for processing (forced reprocess). Queue size: {_processingQueue.Count}");
            LogInfo($"Service running: {_isRunning}, Already processing: {_isProcessing}");
            {
                _ = Task.Run(ProcessQueue).ContinueWith(t =>
                {
                    if (t.IsFaulted && t.Exception != null)
                    {
                        LogError("ProcessQueue task failed", t.Exception.GetBaseException());
                    }
                }, TaskScheduler.Default);
            }
        }

        public static void CancelProcessing()
        {
            LogInfo("Cancellation requested for credits detection");

            _cancellationRequested = true;
            
            _cancellationTokenSource?.Cancel();

            _detectionCoordinator?.CancelDetection();
            
            var killedProcesses = Utilities.FFmpegHelper.KillHungProcesses(0);
            if (killedProcesses > 0)
            {
                LogInfo($"Killed {killedProcesses} running ffmpeg processes during cancellation");
            }

            var clearedCount = 0;
            while (_processingQueue.TryDequeue(out _)) 
            { 
                clearedCount++;
            }

            _processedEpisodes.Clear();
            System.Threading.Interlocked.Exchange(ref _processedEpisodes, new ConcurrentDictionary<string, DateTime>());
            
            _episodeStatusMessages.Clear();
            System.Threading.Interlocked.Exchange(ref _episodeStatusMessages, new ConcurrentDictionary<string, List<string>>());
            
            EmbyCredits.Services.DetectionMethods.OcrDetection.ClearAllCache();
            EmbyCredits.Services.DetectionMethods.ChromaprintDetection.ClearAllCache();
            EmbyCredits.Services.DetectionMethods.BlackFrameDetection.ClearAllCache();

            LogInfo($"Queue cleared: {clearedCount} items removed, processed cache cleared");
            ResetProgressToCancelling();
        }

        public static int ClearQueue()
        {
            LogInfo("Clearing processing queue");

            var clearedCount = 0;
            while (_processingQueue.TryDequeue(out _)) 
            { 
                clearedCount++;
            }

            _isProcessing = false;
            _cancellationRequested = false;

            LogInfo($"Queue cleared: {clearedCount} items removed, flags reset");

            return clearedCount;
        }

        private static void ResetProgressToCancelling()
        {
            if (Plugin.Instance != null)
            {
                Plugin.Progress.CurrentItem = "Cancelling...";
            }
        }

        public static void QueueEpisodeManual(Episode episode, bool skipExistingMarkers = false)
        {
            var series = episode.Series;
            if (series != null && episode.ParentIndexNumber.HasValue)
            {
                var seriesId = series.Id.ToString();
                var seasonNumber = episode.ParentIndexNumber.Value;
                EmbyCredits.Services.DetectionMethods.ChromaprintDetection.ClearSeriesCache(seriesId, seasonNumber);
                EmbyCredits.Services.DetectionMethods.OcrDetection.ClearSeriesCache(seriesId, seasonNumber);
                LogDebug($"Cleared episode comparison cache for {series.Name} Season {seasonNumber} (manual detection)");
            }

            if (skipExistingMarkers && _itemRepository != null)
            {
                var chapters = _itemRepository.GetChapters(episode);
                var hasCreditsMarker = chapters.Any(c => 
                {
                    var markerType = GetMarkerType(c);
                    return markerType == "CreditsStart";
                });

                if (hasCreditsMarker)
                {
                    LogInfo($"Skipping episode {episode.Name} - already has credits marker (manual skip enabled)");
                    
                    if (Plugin.Instance != null)
                    {
                        var episodeKey = series != null
                            ? $"{series.Name} S{episode.ParentIndexNumber:00}E{episode.IndexNumber:00}"
                            : episode.Name;
                        Plugin.Progress.SuccessDetails[episodeKey] = "(already exists)";
                        Plugin.Progress.SuccessfulItems++;
                    }
                    
                    return;
                }
            }

            QueueEpisode(episode, isManualDetection: true);
        }

        public static void QueueSeriesManual(List<Episode> episodes, bool skipExistingMarkers = false)
        {
            if (episodes.Count > 0)
            {
                var firstEpisode = episodes.First();
                var series = firstEpisode.Series;
                if (series != null && firstEpisode.ParentIndexNumber.HasValue)
                {
                    var seriesId = series.Id.ToString();
                    var seasonNumber = firstEpisode.ParentIndexNumber.Value;
                    EmbyCredits.Services.DetectionMethods.ChromaprintDetection.ClearSeriesCache(seriesId, seasonNumber);
                    EmbyCredits.Services.DetectionMethods.OcrDetection.ClearSeriesCache(seriesId, seasonNumber);
                    LogDebug($"Cleared episode comparison cache for {series.Name} Season {seasonNumber} (manual series detection)");
                }
            }

            if (skipExistingMarkers && _itemRepository != null)
            {
                var episodesToQueue = episodes.Where(episode =>
                {
                    var chapters = _itemRepository.GetChapters(episode);
                    var hasCreditsMarker = chapters.Any(c => 
                    {
                        var markerType = GetMarkerType(c);
                        return markerType == "CreditsStart";
                    });

                    if (hasCreditsMarker)
                    {
                        LogInfo($"Skipping episode {episode.Name} - already has credits marker (manual skip enabled)");
                        
                        if (Plugin.Instance != null)
                        {
                            var series = episode.Series;
                            var episodeKey = series != null
                                ? $"{series.Name} S{episode.ParentIndexNumber:00}E{episode.IndexNumber:00}"
                                : episode.Name;
                            Plugin.Progress.SuccessDetails[episodeKey] = "(already exists)";
                            Plugin.Progress.SuccessfulItems++;
                        }
                        
                        return false;
                    }
                    return true;
                }).ToList();

                if (episodesToQueue.Count == 0)
                {
                    LogInfo("No episodes to process - all have existing credit markers");
                    return;
                }

                QueueSeries(episodesToQueue);
            }
            else
            {

                QueueSeries(episodes);
            }
        }

        public static void QueueEpisodeDryRun(Episode episode, bool skipExistingMarkers = false)
        {
            _isDryRun = true;
            
            var series = episode.Series;
            if (series != null && episode.ParentIndexNumber.HasValue)
            {
                var seriesId = series.Id.ToString();
                var seasonNumber = episode.ParentIndexNumber.Value;
                EmbyCredits.Services.DetectionMethods.ChromaprintDetection.ClearSeriesCache(seriesId, seasonNumber);
                EmbyCredits.Services.DetectionMethods.OcrDetection.ClearSeriesCache(seriesId, seasonNumber);
                LogDebug($"Cleared episode comparison cache for {series.Name} Season {seasonNumber} (dry run)");
            }
            
            if (skipExistingMarkers)
            {
                QueueEpisodeManual(episode, skipExistingMarkers: true);
            }
            else
            {
                QueueEpisode(episode, isManualDetection: true);
            }
        }

        public static void QueueSeriesDryRun(List<Episode> episodes, bool skipExistingMarkers = false)
        {
            _isDryRun = true;
            
            if (episodes.Count > 0)
            {
                var firstEpisode = episodes.First();
                var series = firstEpisode.Series;
                if (series != null && firstEpisode.ParentIndexNumber.HasValue)
                {
                    var seriesId = series.Id.ToString();
                    var seasonNumber = firstEpisode.ParentIndexNumber.Value;
                    EmbyCredits.Services.DetectionMethods.ChromaprintDetection.ClearSeriesCache(seriesId, seasonNumber);
                    EmbyCredits.Services.DetectionMethods.OcrDetection.ClearSeriesCache(seriesId, seasonNumber);
                    LogDebug($"Cleared episode comparison cache for {series.Name} Season {seasonNumber} (dry run series)");
                }
            }
            
            if (skipExistingMarkers)
            {
                QueueSeriesManual(episodes, skipExistingMarkers: true);
            }
            else
            {
                QueueSeries(episodes);
            }
        }

        public static void QueueEpisodeDryRunDebug(Episode episode, bool skipExistingMarkers = false)
        {
            _isDryRun = true;
            StartDebugMode();
            
            var series = episode.Series;
            if (series != null && episode.ParentIndexNumber.HasValue)
            {
                var seriesId = series.Id.ToString();
                var seasonNumber = episode.ParentIndexNumber.Value;
                EmbyCredits.Services.DetectionMethods.ChromaprintDetection.ClearSeriesCache(seriesId, seasonNumber);
                EmbyCredits.Services.DetectionMethods.OcrDetection.ClearSeriesCache(seriesId, seasonNumber);
                LogDebug($"Cleared episode comparison cache for {series.Name} Season {seasonNumber} (dry run debug)");
            }
            
            if (skipExistingMarkers)
            {
                QueueEpisodeManual(episode, skipExistingMarkers: true);
            }
            else
            {
                QueueEpisode(episode, isManualDetection: true);
            }
        }

        public static void QueueSeriesDryRunDebug(List<Episode> episodes, bool skipExistingMarkers = false)
        {
            _isDryRun = true;
            StartDebugMode();
            
            if (episodes.Count > 0)
            {
                var firstEpisode = episodes.First();
                var series = firstEpisode.Series;
                if (series != null && firstEpisode.ParentIndexNumber.HasValue)
                {
                    var seriesId = series.Id.ToString();
                    var seasonNumber = firstEpisode.ParentIndexNumber.Value;
                    EmbyCredits.Services.DetectionMethods.ChromaprintDetection.ClearSeriesCache(seriesId, seasonNumber);
                    EmbyCredits.Services.DetectionMethods.OcrDetection.ClearSeriesCache(seriesId, seasonNumber);
                    LogDebug($"Cleared episode comparison cache for {series.Name} Season {seasonNumber} (dry run debug series)");
                }
            }
            
            if (skipExistingMarkers)
            {
                QueueSeriesManual(episodes, skipExistingMarkers: true);
            }
            else
            {
                QueueSeries(episodes);
            }
        }

        private static void StartDebugMode()
        {
            _debugLogger?.StartDebugMode();
        }

        public static string GetDebugLog()
        {
            return _debugLogger?.GetDebugLog() ?? "No debug log available. Debug mode was not enabled.";
        }

        public static void CleanupDebugLog()
        {
            _debugLogger?.Cleanup();
        }

        private static void ScheduleDebugLogCleanup()
        {
            _debugLogger?.ScheduleDebugLogCleanup();
        }

        public static System.Collections.Generic.List<object> GetSeriesMarkers(System.Collections.Generic.List<Episode> episodes)
        {
            return _chapterMarkerService?.GetSeriesMarkers(episodes) ?? new System.Collections.Generic.List<object>();
        }

        public static ChapterMarkerService? GetChapterMarkerService()
        {
            return _chapterMarkerService;
        }

        private static async Task ProcessQueue()
        {
            LogInfo($"ProcessQueue started. Queue count: {_processingQueue.Count}");

            if (!await _processingSemaphore.WaitAsync(0))
            {
                LogInfo("ProcessQueue: already processing, skipping");
                return;
            }

            _isProcessing = true;
            LogInfo("ProcessQueue: acquired semaphore, starting processing");

            // Create a new cancellation token source for this processing session
            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = new CancellationTokenSource();

            if (Plugin.Instance != null)
            {
                Plugin.Progress.FailureReasons?.Clear();
            }

            var processedCount = 0;
            try
            {
                // Collect all episodes from queue
                var allEpisodes = new List<Episode>();
                while (_processingQueue.TryDequeue(out var episode))
                {
                    allEpisodes.Add(episode);
                }

                LogInfo($"Collected {allEpisodes.Count} episodes from queue");

                // Group episodes by series and season for batch processing
                var episodesBySeason = allEpisodes
                    .Where(e => e.Series != null && e.ParentIndexNumber.HasValue)
                    .GroupBy(e => new { SeriesId = e.Series!.Id.ToString(), SeasonNumber = e.ParentIndexNumber!.Value })
                    .ToList();

                LogInfo($"Grouped {allEpisodes.Count} episodes into {episodesBySeason.Count} seasons for batch processing");

                foreach (var seasonGroup in episodesBySeason)
                {
                    if (!_isRunning || _cancellationRequested)
                    {
                        LogInfo("Processing cancelled");
                        break;
                    }

                    if (_pluginCoordination != null)
                    {
                        try
                        {
                            await _pluginCoordination.WaitForOtherPlugins();
                        }
                        catch (Exception coordEx)
                        {
                            LogDebug($"Plugin coordination check failed: {coordEx.Message}");
                        }
                    }

                    if (!_isRunning || _cancellationRequested)
                    {
                        LogInfo("Processing cancelled during coordination wait");
                        break;
                    }

                    var seasonEpisodes = seasonGroup.OrderBy(e => e.IndexNumber).ToList();
                    var firstEpisode = seasonEpisodes.First();
                    var seriesName = firstEpisode.Series?.Name ?? "Unknown Series";
                    
                    LogInfo($"Processing season batch: {seriesName} Season {seasonGroup.Key.SeasonNumber} ({seasonEpisodes.Count} episodes)");

                    _isBatchMode = true;
                    try
                    {
                        await ProcessSeasonBatch(seasonEpisodes, _cancellationTokenSource?.Token ?? CancellationToken.None);
                    }
                    finally
                    {
                        _isBatchMode = false;
                    }

                    processedCount += seasonEpisodes.Count;
                    
                    if (processedCount % 20 == 0)
                    {
                        CleanupBatchDetectionCache();
                        CleanupOldProcessedEpisodes();
                        
                        var killedCount = Utilities.FFmpegHelper.KillHungProcesses(450);
                        if (killedCount > 0)
                        {
                            LogWarn($"Killed {killedCount} hung ffmpeg processes (older than 450 seconds)");
                        }
                    }

                    await Task.Delay(1000);
                }

                if (Plugin.Instance != null)
                {
                    if (_cancellationRequested)
                    {
                        Plugin.Progress.IsRunning = false;
                        Plugin.Progress.EndTime = DateTime.Now;
                        Plugin.Progress.CurrentItem = "Cancelled";

                        if (IsDebugMode)
                        {
                            ScheduleDebugLogCleanup();
                        }
                    }
                    else if (_processingQueue.IsEmpty)
                    {
                        Plugin.Progress.IsRunning = false;
                        Plugin.Progress.EndTime = DateTime.Now;
                        Plugin.Progress.CurrentItem = _isDryRun ? "Dry Run Complete" : "Complete";
                        Plugin.Progress.CurrentItemProgress = 100;
                        LogInfo($"Processing complete: {Plugin.Progress.SuccessfulItems} succeeded, {Plugin.Progress.FailedItems} failed");
                        
                        EmbyCredits.Services.DetectionMethods.OcrDetection.ClearAllCache();
                        EmbyCredits.Services.DetectionMethods.ChromaprintDetection.ClearAllCache();
                        EmbyCredits.Services.DetectionMethods.BlackFrameDetection.ClearAllCache();

                        if (_configuration != null && _configuration.EnableAutoDetection && !_isDryRun && Plugin.NotificationManager != null)
                        {
                            try
                            {
                                var duration = Plugin.Progress.EndTime.HasValue && Plugin.Progress.StartTime.HasValue
                                    ? Plugin.Progress.EndTime.Value - Plugin.Progress.StartTime.Value
                                    : TimeSpan.Zero;

                                var failedEpisodes = Plugin.Progress.FailureReasons?.Select(kvp => $"{kvp.Key}: {kvp.Value}").ToList() ?? new List<string>();
                                var successfulSeries = Plugin.Progress.SuccessDetails.Keys
                                    .Select(k => k.Split(new[] { " S" }, StringSplitOptions.None)[0])
                                    .Distinct()
                                    .ToList();

                                if (_logger == null) return;
                                var notificationService = new NotificationService(_logger, Plugin.NotificationManager, _configuration);
                                notificationService.SendAutoDetectionNotification(
                                    Plugin.Progress.SuccessfulItems,
                                    Plugin.Progress.FailedItems,
                                    Plugin.Progress.ProcessedItems,
                                    failedEpisodes,
                                    successfulSeries,
                                    duration
                                );
                            }
                            catch (Exception ex)
                            {
                                LogError("Failed to send auto-detection notification", ex);
                            }
                        }

                        _isDryRun = false;
                        if (IsDebugMode)
                        {
                            ScheduleDebugLogCleanup();
                        }
                    }
                }
            }
            finally
            {
                _isProcessing = false;
                _processingSemaphore.Release();
            }
        }

        public static async Task ProcessSeasonBatch(List<Episode> seasonEpisodes, CancellationToken cancellationToken)
        {
            if (_episodeProcessor == null || _configuration == null || seasonEpisodes.Count == 0)
                return;

            var firstEpisode = seasonEpisodes.First();
            var seriesId = firstEpisode.Series?.Id.ToString() ?? string.Empty;
            var seasonNumber = firstEpisode.ParentIndexNumber ?? 0;
            var seriesName = firstEpisode.Series?.Name ?? "Unknown Series";

            _logger?.Info($"Starting batch processing for {seriesName} Season {seasonNumber} ({seasonEpisodes.Count} episodes)");

            var ruleMatchingService = _logger != null ? new RuleMatchingService(_logger, _configuration) : null;
            var effectiveConfig = firstEpisode.Series != null && ruleMatchingService != null
                ? ruleMatchingService.GetEffectiveConfiguration(firstEpisode.Series) 
                : _configuration;

            if (effectiveConfig != _configuration)
            {
                _logger?.Info($"Using rule-based configuration for series '{seriesName}'");
            }

            bool isAnime = false;
            if (effectiveConfig.EnableAnimeDetection && !string.IsNullOrEmpty(seriesId))
            {
                _logger?.Debug($"Checking if {seriesName} is anime (seriesId: {seriesId})");
                isAnime = CheckIfAnime(seriesId);
                if (isAnime)
                {
                    _logger?.Info($"Series '{seriesName}' identified as ANIME - batch processing will use anime-specific methods");
                }
                else
                {
                    _logger?.Debug($"Series '{seriesName}' is not anime");
                }
            }

            // Get chromaprint detection method if available
            // If using rule-based config, create temporary coordinator with effective config
            DetectionMethods.ChromaprintDetection? chromaprintMethod = null;
            DetectionCoordinator? tempCoordinator = null;
            
            if (effectiveConfig != _configuration && _logger != null)
            {
                _logger.Debug($"Creating temporary DetectionCoordinator with rule-based DetectionMode: {effectiveConfig.DetectionMode}");
                tempCoordinator = new DetectionCoordinator(_logger, effectiveConfig);
                chromaprintMethod = tempCoordinator.GetAllDetectionMethods()
                    .OfType<DetectionMethods.ChromaprintDetection>()
                    .FirstOrDefault();
                _logger.Debug($"Chromaprint method from rule-based config - IsEnabled: {chromaprintMethod?.IsEnabled ?? false}");
            }
            else if (effectiveConfig == _configuration)
            {
                chromaprintMethod = _episodeProcessor.GetDetectionMethods()
                    .OfType<DetectionMethods.ChromaprintDetection>()
                    .FirstOrDefault();
                _logger?.Debug($"Chromaprint method from default config - IsEnabled: {chromaprintMethod?.IsEnabled ?? false}");
            }

            Dictionary<string, double>? batchResults = null;
            
            // Only use batch chromaprint when it's the PRIMARY method (not fallback)
            if (!isAnime && 
                effectiveConfig != null &&
                chromaprintMethod != null && 
                chromaprintMethod.IsEnabled && 
                !string.IsNullOrEmpty(seriesId) &&
                (effectiveConfig.DetectionMode == DetectionMode.HashOnly || 
                 effectiveConfig.DetectionMode == DetectionMode.HashWithOcrFallback))
            {
                // Process all episodes together using batch detection (non-anime only, Hash as primary method)
                var episodeData = seasonEpisodes.Select(ep => (
                    EpisodeId: ep.Id.ToString(),
                    VideoPath: ep.Path,
                    Duration: ep.RunTimeTicks.HasValue ? ep.RunTimeTicks.Value / 10000000.0 : 0
                )).ToList();

                _logger?.Info($"Using batch chromaprint detection for {episodeData.Count} episodes (DetectionMode: {effectiveConfig.DetectionMode})");
                batchResults = await chromaprintMethod.DetectCreditsForSeason(episodeData, seriesId, seasonNumber, cancellationToken);
                _logger?.Info($"Batch detection found credits for {batchResults.Count} episodes");
            }
            else if (isAnime)
            {
                _logger?.Info($"Anime detected - skipping chromaprint batch processing, will use individual episode detection with BlackFrameDetection");
            }
            else if (effectiveConfig != null && (effectiveConfig.DetectionMode == DetectionMode.OcrWithHashFallback || effectiveConfig.DetectionMode == DetectionMode.OcrOnly))
            {
                _logger?.Info($"OCR as primary method (DetectionMode: {effectiveConfig.DetectionMode}) - processing episodes individually to try OCR first");
            }

            // Process each episode with the batch results
            foreach (var episode in seasonEpisodes)
            {
                if (cancellationToken.IsCancellationRequested || _cancellationRequested)
                    break;

                var episodeId = episode.Id.ToString();
                
                try
                {
                    if (Plugin.Instance != null)
                    {
                        Plugin.Progress.CurrentItem = $"{seriesName} - S{episode.ParentIndexNumber:D2}E{episode.IndexNumber:D2} - {episode.Name}";
                    }

                    _logger?.Debug($"Processing episode {episode.Name}");

                    double? batchCreditsStart = null;
                    if (batchResults != null && batchResults.ContainsKey(episodeId))
                    {
                        batchCreditsStart = batchResults[episodeId];
                    }

                    var (success, creditsStart, failureReason, confidence, methodName, detectionReason) = await _episodeProcessor.ProcessEpisodeWithBatchResult(
                        episode, _isDryRun, batchCreditsStart, chromaprintMethod, tempCoordinator);

                    if (success && creditsStart > 0)
                    {
                        if (Plugin.Instance != null)
                        {
                            Plugin.Progress.SuccessfulItems++;

                            var episodeKey = $"{seriesName} S{episode.ParentIndexNumber:00}E{episode.IndexNumber:00}";
                            var statusMessages = GetAndClearEpisodeStatusMessages(episodeId);
                            var duration = episode.RunTimeTicks.HasValue ? episode.RunTimeTicks.Value / (double)TimeSpan.TicksPerSecond : 0;
                            var successDetail = $"{FormatTime(creditsStart)} / {FormatTime(duration)}";
                            
                            if (!string.IsNullOrEmpty(methodName))
                            {
                                successDetail += $" [{methodName}]";
                            }
                            
                            if (!string.IsNullOrEmpty(detectionReason))
                            {
                                successDetail += $" - {detectionReason}";
                            }
                            
                            if (statusMessages.Count > 0)
                            {
                                successDetail += " (" + string.Join(", ", statusMessages) + ")";
                            }
                            
                            Plugin.Progress.SuccessDetails[episodeKey] = successDetail;
                            Plugin.Progress.ConfidenceScores[episodeKey] = confidence;
                            Plugin.Progress.EpisodeIds[episodeKey] = episodeId;

                            // Generate thumbnail if enabled
                            if (Plugin.Instance.Configuration.EnableThumbnailGeneration)
                            {
                                try
                                {
                                    var thumbnailPath = await GenerateThumbnail(episode, creditsStart, episodeKey);
                                    if (!string.IsNullOrEmpty(thumbnailPath))
                                    {
                                        Plugin.Progress.ThumbnailPaths[episodeKey] = thumbnailPath;
                                    }
                                }
                                catch (Exception thumbEx)
                                {
                                    _logger?.Debug($"Failed to generate thumbnail for {episodeKey}: {thumbEx.Message}");
                                }
                            }
                        }

                        if (!_isDryRun)
                        {
                            _processedEpisodes.TryAdd(episodeId, DateTime.UtcNow);
                        }
                        
                        _logger?.Debug($"Successfully detected credits at {FormatTime(creditsStart)} for {episode.Name}");
                    }
                    else
                    {
                        if (Plugin.Instance != null)
                        {
                            Plugin.Progress.FailedItems++;

                            var episodeKey = $"{seriesName} S{episode.ParentIndexNumber:00}E{episode.IndexNumber:00}";
                            Plugin.Progress.FailureReasons[episodeKey] = failureReason;
                        }
                        
                        GetAndClearEpisodeStatusMessages(episodeId);
                        _logger?.Warn($"Failed to detect credits for {episode.Name}: {failureReason}");
                    }
                }
                catch (Exception ex)
                {
                    _logger?.ErrorException($"Error processing episode {episode.Name}", ex);
                    if (Plugin.Instance != null)
                    {
                        Plugin.Progress.FailedItems++;
                        var episodeKey = $"{seriesName} S{episode.ParentIndexNumber:00}E{episode.IndexNumber:00}";
                        Plugin.Progress.FailureReasons[episodeKey] = ex.Message;
                    }
                }

                // Update progress tracking
                if (Plugin.Instance != null)
                {
                    Plugin.Progress.ProcessedItems++;
                    Plugin.Progress.CurrentItemProgress = 100;
                    Plugin.Progress.CheckAndLimitDictionarySize();

                    if (Plugin.Progress.ProcessedItems >= Plugin.Progress.TotalItems)
                    {
                        Plugin.Progress.IsRunning = false;
                        Plugin.Progress.EndTime = DateTime.Now;
                        Plugin.Progress.CurrentItem = "Complete";
                        
                        EmbyCredits.Services.DetectionMethods.OcrDetection.ClearAllCache();
                        EmbyCredits.Services.DetectionMethods.ChromaprintDetection.ClearAllCache();
                        EmbyCredits.Services.DetectionMethods.BlackFrameDetection.ClearAllCache();
                    }
                }
            }

            tempCoordinator?.Dispose();
            
            _logger?.Info($"Batch processing complete for {seriesName} Season {seasonNumber}");
        }



        public static async Task ProcessEpisode(Episode episode)
        {
            if (_episodeProcessor == null || _configuration == null)
                return;

            var episodeId = episode.Id.ToString();

            try
            {
                if (Plugin.Instance != null)
                {
                    Plugin.Progress.CurrentItem = $"{episode.Series?.Name} - S{episode.ParentIndexNumber:D2}E{episode.IndexNumber:D2} - {episode.Name}";
                    Plugin.Progress.CurrentItemProgress = 0;
                }

                if (Plugin.Instance != null)
                {
                    Plugin.Progress.CurrentItemProgress = 10;
                }

                var (success, creditsStart, failureReason, confidence, methodName, detectionReason) = await _episodeProcessor.ProcessEpisode(
                    episode, _isDryRun, _isBatchMode, _batchDetectionCache);

                if (Plugin.Instance != null)
                {
                    Plugin.Progress.CurrentItemProgress = 95;
                }

                if (success && creditsStart > 0)
                {
                    if (Plugin.Instance != null)
                    {
                        Plugin.Progress.SuccessfulItems++;

                        var series = episode.Series;
                        var episodeKey = series != null
                            ? $"{series.Name} S{episode.ParentIndexNumber:00}E{episode.IndexNumber:00}"
                            : episode.Name;
                        
                        var statusMessages = GetAndClearEpisodeStatusMessages(episodeId);
                        var duration = episode.RunTimeTicks.HasValue ? episode.RunTimeTicks.Value / (double)TimeSpan.TicksPerSecond : 0;
                        var successDetail = $"{FormatTime(creditsStart)} / {FormatTime(duration)}";
                        
                        if (!string.IsNullOrEmpty(methodName))
                        {
                            successDetail += $" [{methodName}]";
                        }
                        
                        if (!string.IsNullOrEmpty(detectionReason))
                        {
                            successDetail += $" - {detectionReason}";
                        }
                        
                        if (statusMessages.Count > 0)
                        {
                            successDetail += " (" + string.Join(", ", statusMessages) + ")";
                        }
                        
                        Plugin.Progress.SuccessDetails[episodeKey] = successDetail;
                        Plugin.Progress.ConfidenceScores[episodeKey] = confidence;
                        Plugin.Progress.EpisodeIds[episodeKey] = episode.Id.ToString();

                        if (Plugin.Instance.Configuration.EnableThumbnailGeneration)
                        {
                            try
                            {
                                var thumbnailPath = await GenerateThumbnail(episode, creditsStart, episodeKey);
                                if (!string.IsNullOrEmpty(thumbnailPath))
                                {
                                    Plugin.Progress.ThumbnailPaths[episodeKey] = thumbnailPath;
                                }
                            }
                            catch (Exception thumbEx)
                            {
                                _logger?.Debug($"Failed to generate thumbnail for {episodeKey}: {thumbEx.Message}");
                            }
                        }
                    }

                    if (!_isDryRun)
                    {
                        _processedEpisodes.TryAdd(episodeId, DateTime.UtcNow);
                    }
                }
                else
                {
                    if (Plugin.Instance != null)
                    {
                        Plugin.Progress.FailedItems++;

                        var series = episode.Series;
                        var episodeKey = series != null
                            ? $"{series.Name} S{episode.ParentIndexNumber:00}E{episode.IndexNumber:00}"
                            : episode.Name;
                        Plugin.Progress.FailureReasons[episodeKey] = failureReason;
                    }
                    
                    GetAndClearEpisodeStatusMessages(episodeId);

                    if (!_isDryRun)
                    {
                        _processedEpisodes.TryAdd(episodeId, DateTime.UtcNow);
                    }
                }

                if (Plugin.Instance != null)
                {
                    Plugin.Progress.ProcessedItems++;
                    Plugin.Progress.CurrentItemProgress = 100;
                    Plugin.Progress.CheckAndLimitDictionarySize();

                    if (Plugin.Progress.ProcessedItems >= Plugin.Progress.TotalItems)
                    {
                        Plugin.Progress.IsRunning = false;
                        Plugin.Progress.EndTime = DateTime.Now;
                        Plugin.Progress.CurrentItem = "Complete";
                        
                        EmbyCredits.Services.DetectionMethods.OcrDetection.ClearAllCache();
                        EmbyCredits.Services.DetectionMethods.ChromaprintDetection.ClearAllCache();
                        EmbyCredits.Services.DetectionMethods.BlackFrameDetection.ClearAllCache();
                    }
                }
                
                CleanupBatchDetectionCache();
                CleanupOldProcessedEpisodes();
            }
            catch (Exception ex)
            {
                _logger?.ErrorException($"Error processing episode {episode.Name}", ex);

                if (Plugin.Instance != null)
                {
                    Plugin.Progress.FailedItems++;
                    Plugin.Progress.ProcessedItems++;
                }
            }
        }

        private static string FormatTime(double seconds)
        {
            var time = TimeSpan.FromSeconds(seconds);
            return $"{(int)time.TotalHours:D2}:{time.Minutes:D2}:{time.Seconds:D2}";
        }

        private static async Task<string> GenerateThumbnail(Episode episode, double timestamp, string episodeKey)
        {
            try
            {
                LogDebug($"Starting thumbnail generation for {episodeKey} at {timestamp}s");
                
                var videoPath = episode.Path;
                if (string.IsNullOrEmpty(videoPath) || !File.Exists(videoPath))
                {
                    LogDebug($"Video path not found for {episodeKey}");
                    return string.Empty;
                }

                var config = Plugin.Instance?.Configuration;
                if (config == null)
                {
                    LogDebug("Plugin configuration not available");
                    return string.Empty;
                }

                // Create thumbnails directory in plugin data folder
                var pluginDataPath = Plugin.Instance?.AppPaths?.PluginConfigurationsPath;
                if (string.IsNullOrEmpty(pluginDataPath))
                {
                    LogDebug("Plugin data path not available");
                    return string.Empty;
                }

                var thumbnailDir = Path.Combine(pluginDataPath, "EmbyCredits", "Thumbnails");
                Directory.CreateDirectory(thumbnailDir);
                LogDebug($"Thumbnail directory: {thumbnailDir}");

                // Generate unique filename based on episode key and timestamp
                var safeFileName = string.Join("_", episodeKey.Split(Path.GetInvalidFileNameChars()));
                var thumbnailFileName = $"{safeFileName}_{timestamp:F2}.jpg";
                var thumbnailPath = Path.Combine(thumbnailDir, thumbnailFileName);

                // Extract frame using FFmpeg
                var ffmpegPath = Utilities.FFmpegHelper.GetFfmpegPath();
                var width = config.ThumbnailWidth > 0 ? config.ThumbnailWidth : 320;
                var quality = config.ThumbnailQuality > 0 ? config.ThumbnailQuality : 85;
                
                var ffmpegQuality = Math.Max(2, Math.Min(31, 31 - ((quality - 50) * 29 / 50)));
                
                var arguments = $"-ss {timestamp.ToString(CultureInfo.InvariantCulture)} -i \"{videoPath}\" " +
                               $"-vframes 1 -vf scale={width}:-1 -q:v {ffmpegQuality} " +
                               $"\"{thumbnailPath}\" -y";

                LogDebug($"FFmpeg command: {ffmpegPath} {arguments}");

                using (var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = ffmpegPath,
                        Arguments = arguments,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    }
                })
                {
                    var errorOutput = new System.Text.StringBuilder();
                    DataReceivedEventHandler errorHandler = (sender, e) =>
                    {
                        if (!string.IsNullOrEmpty(e.Data))
                        {
                            errorOutput.AppendLine(e.Data);
                        }
                    };
                    
                    try
                    {
                        process.ErrorDataReceived += errorHandler;

                        process.Start();
                        process.BeginErrorReadLine();
                        
                        await Task.Run(() => process.WaitForExit());
                    }
                    finally
                    {
                        try
                        {
                            process.ErrorDataReceived -= errorHandler;
                            process.CancelErrorRead();
                        }
                        catch { }
                    }

                    if (process.ExitCode == 0 && File.Exists(thumbnailPath))
                    {
                        var fileInfo = new FileInfo(thumbnailPath);
                        LogDebug($"✓ Generated thumbnail for {episodeKey}: {thumbnailFileName} ({fileInfo.Length} bytes)");
                        return thumbnailFileName;
                    }
                    else
                    {
                        LogDebug($"✗ Thumbnail generation failed for {episodeKey}. Exit code: {process.ExitCode}");
                        if (errorOutput.Length > 0)
                        {
                            LogDebug($"FFmpeg error: {errorOutput.ToString().Substring(0, Math.Min(500, errorOutput.Length))}");
                        }
                    }
                }

                return string.Empty;
            }
            catch (Exception ex)
            {
                LogDebug($"✗ Error generating thumbnail for {episodeKey}: {ex.Message}");
                _logger?.ErrorException($"Thumbnail generation error for {episodeKey}", ex);
                return string.Empty;
            }
            finally
            {
                GC.Collect(1, GCCollectionMode.Optimized, false);
            }
        }

        private static double GetMethodConfidence(string method)
        {
            return method switch
            {
                "Video Pattern" => 1.0,
                "Audio Pattern" => 0.9,
                "Text Detection" => 0.85,
                "Scene Change" => 0.80,
                "Black Screen" => 0.75,
                "Audio Silence" => 0.7,
                _ => 0.5
            };
        }

        private static int GetMethodPriority(string method)
        {
            if (_configuration == null) return 5;

            return method switch
            {
                "Video Pattern" => _configuration.VideoPatternPriority,
                "Audio Pattern" => _configuration.AudioPatternPriority,
                "Text Detection" => _configuration.TextDetectionPriority,
                "Scene Change" => _configuration.SceneChangePriority,
                "Audio Silence" => _configuration.AudioSilencePriority,
                "Black Screen" => _configuration.BlackScreenPriority,
                _ => 5
            };
        }

        private static string? GetMarkerType(ChapterInfo chapter)
        {
            try
            {
                if (chapter == null) return null;
                var chapterType = chapter.GetType();
                if (chapterType == null) return null;
                var markerTypeProp = chapterType.GetProperty("MarkerType");
                if (markerTypeProp != null && markerTypeProp.CanRead)
                {
                    var value = markerTypeProp.GetValue(chapter);
                    return value?.ToString();
                }
            }
            catch { }
            return null;
        }

        private static bool CheckIfAnime(string seriesId)
        {
            try
            {
                _logger?.Debug($"CheckIfAnime called for seriesId: {seriesId}");
                
                if (_libraryManager == null)
                {
                    _logger?.Warn($"CheckIfAnime: _libraryManager is null");
                    return false;
                }

                if (!Guid.TryParse(seriesId, out var seriesGuid))
                {
                    _logger?.Warn($"CheckIfAnime: Failed to parse seriesId as Guid: {seriesId}");
                    return false;
                }

                var item = _libraryManager.GetItemById(seriesGuid);
                if (item == null)
                {
                    _logger?.Warn($"CheckIfAnime: GetItemById returned null for Guid: {seriesGuid}");
                    return false;
                }

                _logger?.Debug($"CheckIfAnime: Found item type: {item.GetType().Name}, Name: {item.Name}");

                if (item is MediaBrowser.Controller.Entities.TV.Series series)
                {
                    _logger?.Debug($"CheckIfAnime: Series found - Name: {series.Name}");

                    if (series.Tags != null && series.Tags.Length > 0)
                    {
                        if (_configuration?.EnableDetailedLogging == true)
                            _logger?.Debug($"CheckIfAnime: Tags count: {series.Tags.Length}");
                        
                        for (int i = 0; i < series.Tags.Length; i++)
                        {
                            if (series.Tags[i].Equals("anime", StringComparison.OrdinalIgnoreCase))
                            {
                                _logger?.Info($"CheckIfAnime: Series '{series.Name}' identified as ANIME via Tags");
                                return true;
                            }
                        }
                    }

                    if (series.Genres != null && series.Genres.Length > 0)
                    {
                        if (_configuration?.EnableDetailedLogging == true)
                            _logger?.Debug($"CheckIfAnime: Genres count: {series.Genres.Length}");
                        
                        for (int i = 0; i < series.Genres.Length; i++)
                        {
                            if (series.Genres[i].Equals("anime", StringComparison.OrdinalIgnoreCase))
                            {
                                _logger?.Info($"CheckIfAnime: Series '{series.Name}' identified as ANIME via Genres");
                                return true;
                            }
                        }
                    }

                    _logger?.Debug($"CheckIfAnime: Series '{series.Name}' is NOT anime");
                }
                else
                {
                    _logger?.Warn($"CheckIfAnime: Item is not a Series, it's: {item.GetType().Name}");
                }

                return false;
            }
            catch (Exception ex)
            {
                _logger?.ErrorException($"CheckIfAnime exception for seriesId: {seriesId}", ex);
                return false;
            }
        }
    }
}

