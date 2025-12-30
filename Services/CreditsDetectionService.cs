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
    /// <summary>
    /// Credits Detection Service - Main service for detecting end credits in video files.
    /// 
    /// DESIGN NOTE: This service uses static state for compatibility with Emby's plugin architecture.
    /// While this makes testing more difficult and requires careful thread synchronization,
    /// it allows the service to integrate seamlessly with Emby's lifecycle management.
    /// All mutable static fields are protected with appropriate locks or thread-safe collections.
    /// </summary>
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
        private static Timer? _processingTimer;
        private static ConcurrentQueue<Episode> _processingQueue = new ConcurrentQueue<Episode>();
        private static SemaphoreSlim _processingSemaphore = new SemaphoreSlim(1, 1);
        private static bool _isProcessing = false;
        private static bool _cancellationRequested = false;
        private static bool _isDryRun = false;
        private static readonly object _timerLock = new object();
        
        private const int MaxQueueSize = 1000;

        private static DetectionCoordinator? _detectionCoordinator;
        private static ProcessedFilesTracker? _processedFilesTracker;
        private static DebugLogger? _debugLogger;
        private static ChapterMarkerService? _chapterMarkerService;
        private static EpisodeProcessor? _episodeProcessor;
        private static SeriesAveragingService? _seriesAveragingService;

        private static readonly ConcurrentDictionary<string, List<(string method, double timestamp)>> _batchDetectionCache = new ConcurrentDictionary<string, List<(string method, double timestamp)>>();
        private static bool _isBatchMode = false;

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

        // Public method for DetectionCoordinator and other classes to use debug logging
        public static void LogToDebug(string level, string message)
        {
            _debugLogger?.LogToDebug(level, message);
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
            var trackerPath = string.IsNullOrWhiteSpace(configuration.TempFolderPath) 
                ? appPaths.PluginConfigurationsPath 
                : configuration.TempFolderPath;
            _processedFilesTracker = new ProcessedFilesTracker(_logger, trackerPath);
            
            // Initialize new modules
            _debugLogger = new DebugLogger(_logger, configuration);
            _seriesAveragingService = new SeriesAveragingService(_logger, configuration);
            if (_itemRepository != null)
            {
                _chapterMarkerService = new ChapterMarkerService(_logger, _itemRepository);
                _episodeProcessor = new EpisodeProcessor(_logger, _libraryManager, _detectionCoordinator, 
                    _processedFilesTracker, _chapterMarkerService, _debugLogger, configuration, _seriesAveragingService);
            }

            _logger.Info("Credits Detection Service started");

            if (_libraryManager != null && configuration.EnableAutoDetection)
            {
                _libraryManager.ItemAdded += OnItemAdded;
                _logger.Info("Auto-detection enabled: Library event handlers registered");
            }

            _processingTimer = new Timer(CheckForNewEpisodes, null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5));
        }

        public static void UpdateConfiguration(PluginConfiguration configuration)
        {
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
                var trackerPath = string.IsNullOrWhiteSpace(configuration.TempFolderPath) 
                    ? _appPaths.PluginConfigurationsPath 
                    : configuration.TempFolderPath;
                _processedFilesTracker = new ProcessedFilesTracker(_logger, trackerPath);
                
                // Reinitialize new modules
                _debugLogger = new DebugLogger(_logger, configuration);
                _seriesAveragingService?.Dispose();
                _seriesAveragingService = new SeriesAveragingService(_logger, configuration);
                if (_itemRepository != null)
                {
                    _chapterMarkerService = new ChapterMarkerService(_logger, _itemRepository);
                    _episodeProcessor = new EpisodeProcessor(_logger, _libraryManager, _detectionCoordinator, 
                        _processedFilesTracker, _chapterMarkerService, _debugLogger, configuration, _seriesAveragingService);
                }
            }
        }

        public static void ClearCache()
        {
            try
            {

                _detectionCoordinator?.ClearCache();
                _logger?.Info("Cleared in-memory batch detection cache for fresh detection");
            }
            catch (Exception ex)
            {
                LogError("Error clearing cache", ex);
            }
        }

        public static void ClearProcessedFiles()
        {
            try
            {
                _processedFilesTracker?.Clear();
                _logger?.Info("Cleared processed files tracking list");
            }
            catch (Exception ex)
            {
                LogError("Error clearing processed files list", ex);
                throw;
            }
        }

        public static void Stop()
        {
            _isRunning = false;
            
            // Thread-safe timer disposal
            Timer? timerToDispose = null;
            lock (_timerLock)
            {
                timerToDispose = _processingTimer;
                _processingTimer = null;
            }
            timerToDispose?.Dispose();
            
            _isProcessing = false;

            if (_libraryManager != null)
            {
                _libraryManager.ItemAdded -= OnItemAdded;
            }

            _detectionCoordinator?.Dispose();
            _detectionCoordinator = null;
            
            _seriesAveragingService?.Dispose();
            _seriesAveragingService = null;
            
            // Dispose semaphore
            try
            {
                _processingSemaphore?.Dispose();
                _processingSemaphore = new SemaphoreSlim(1, 1);
            }
            catch (Exception ex)
            {
                LogError("Error disposing semaphore", ex);
            }

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

                    var libraryIds = _configuration.LibraryIds ?? Array.Empty<string>();
                    if (libraryIds.Length > 0)
                    {

                        var libraryId = episode.GetTopParent()?.Id.ToString();
                        if (string.IsNullOrEmpty(libraryId) || !libraryIds.Contains(libraryId))
                        {
                            LogDebug($"Skipping episode {episode.Name} - not in configured libraries");
                            return;
                        }
                    }

                    LogInfo($"New episode detected: {episode.SeriesName} - {episode.Name}");
                    QueueEpisode(episode);
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
            Utilities.FFmpegHelper.Initialize(ffmpegManager);
        }

        public static void QueueEpisode(Episode episode)
        {
            _cancellationRequested = false;
            
            var episodeId = episode.Id.ToString();
            LogDebug($"QueueEpisode called for: {episode.Name} (ID: {episodeId})");
            LogDebug($"Already processed: {_processedEpisodes.ContainsKey(episodeId)}, IsDryRun: {_isDryRun}, IsProcessing: {_isProcessing}");
            
            // Check if episode already has credits marker (if ScheduledTaskOnlyProcessMissing is enabled)
            if (_configuration != null && _itemRepository != null)
            {
                var chapters = _itemRepository.GetChapters(episode);
                var hasCreditsMarker = chapters.Any(c => 
                {
                    var markerType = GetMarkerType(c);
                    return markerType != null && markerType.Contains("Credits");
                });
                
                LogDebug($"Episode has existing credits marker: {hasCreditsMarker}, ScheduledTaskOnlyProcessMissing: {_configuration.ScheduledTaskOnlyProcessMissing}");
                
                if (hasCreditsMarker && _configuration.ScheduledTaskOnlyProcessMissing)
                {
                    LogInfo($"Skipping episode {episode.Name} - already has credits marker (ScheduledTaskOnlyProcessMissing is enabled)");
                    return;
                }
            }
            
            if (_configuration != null && _configuration.SkipPreviouslyProcessedFiles && _processedFilesTracker != null)
            {
                if (_processedFilesTracker.ShouldSkipFile(episodeId, _configuration.SkipOnlySuccessfulFiles))
                {
                    LogInfo($"Skipping episode {episode.Name} - already processed (SkipOnlySuccessful: {_configuration.SkipOnlySuccessfulFiles})");
                    return;
                }
            }
            
            // Allow reprocessing in dry run mode or if not yet processed
            if (_isDryRun || !_processedEpisodes.ContainsKey(episodeId))
            {
                // Enforce maximum queue size
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
                    Task.Run(ProcessQueue);
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
            else
            {
                LogInfo($"Skipping episode {episode.Name} - already processed");
            }
        }

        public static void QueueSeries(List<Episode> episodes)
        {
            ClearCache();

            _batchDetectionCache.Clear();

            while (_processingQueue.TryDequeue(out _)) { }
            _cancellationRequested = false;
            _isProcessing = false;

            // Ensure detection coordinator is initialized
            if (_detectionCoordinator == null && _logger != null && _configuration != null)
            {
                LogInfo("Initializing DetectionCoordinator");
                _detectionCoordinator = new DetectionCoordinator(_logger, _configuration);
            }

            if (Plugin.Instance != null)
            {
                Plugin.Progress.Reset();
                Plugin.Progress.IsRunning = true;
                Plugin.Progress.TotalItems = episodes.Count;
                Plugin.Progress.StartTime = DateTime.Now;
            }

            var queuedCount = 0;
            foreach (var episode in episodes)
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

            _isBatchMode = queuedCount >= 3 && _configuration?.UseCorrelationScoring == true;

            if (_isBatchMode)
            {
                LogInfo($"Batch mode enabled: Pre-computing detections for {queuedCount} episodes");
                Task.Run(() => PreComputeBatchDetections(episodes.ToList()));
            }
            else
            {
                Task.Run(ProcessQueue);
            }
        }

        public static void CancelProcessing()
        {
            LogInfo("Cancellation requested for credits detection");
            
            _cancellationRequested = true;
            
            // Cancel ongoing detection operations (this recreates the CancellationTokenSource)
            _detectionCoordinator?.CancelDetection();

            var clearedCount = 0;
            while (_processingQueue.TryDequeue(out _)) 
            { 
                clearedCount++;
            }
            
            // Clear processed episodes cache so cancelled episodes can be reprocessed
            _processedEpisodes.Clear();
            
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

        public static void ClearSeriesAveragingData()
        {
            LogInfo("Clearing series averaging data");
            _seriesAveragingService?.Clear();
            LogInfo("Series averaging data cleared");
        }

        private static void ResetProgressToCancelling()
        {
            if (Plugin.Instance != null)
            {
                Plugin.Progress.CurrentItem = "Cancelling...";
            }
        }

        public static void QueueEpisodeDryRun(Episode episode)
        {
            _isDryRun = true;
            QueueEpisode(episode);
        }

        public static void QueueSeriesDryRun(List<Episode> episodes)
        {
            _isDryRun = true;
            QueueSeries(episodes);
        }

        public static void QueueEpisodeDryRunDebug(Episode episode)
        {
            _isDryRun = true;
            StartDebugMode();
            QueueEpisode(episode);
        }

        public static void QueueSeriesDryRunDebug(List<Episode> episodes)
        {
            _isDryRun = true;
            StartDebugMode();
            QueueSeries(episodes);
        }

        private static void StartDebugMode()
        {
            _debugLogger?.StartDebugMode();
        }

        public static string GetDebugLog()
        {
            return _debugLogger?.GetDebugLog() ?? "No debug log available. Debug mode was not enabled.";
        }

        private static void ScheduleDebugLogCleanup()
        {
            _debugLogger?.ScheduleDebugLogCleanup();
        }

        private static async Task PreComputeBatchDetections(List<Episode> episodes)
        {
            if (_configuration == null || _detectionCoordinator == null) return;

            try
            {
                LogInfo("=== Starting batch detection pre-computation ===");
                var totalEpisodes = episodes.Count;

                await _detectionCoordinator.PreComputeBatchDetections(episodes, async (progress) =>
                {
                    if (_cancellationRequested) return;

                    if (Plugin.Instance != null)
                    {
                        var processedCount = (int)(progress * totalEpisodes);
                        var currentEpisode = processedCount > 0 && processedCount <= episodes.Count 
                            ? episodes[processedCount - 1] 
                            : null;

                        Plugin.Progress.CurrentItem = currentEpisode != null 
                            ? $"Pre-analyzing: {currentEpisode.Name}" 
                            : "Pre-analyzing episodes...";
                        Plugin.Progress.ProcessedItems = processedCount;
                        Plugin.Progress.CurrentItemProgress = (int)(progress * 50); 
                    }

                    await Task.CompletedTask;
                });

                LogInfo("=== Batch pre-computation complete ===");

                if (!_isProcessing && !_cancellationRequested)
                {
                    await ProcessQueue();
                }
            }
            catch (Exception ex)
            {
                LogError("Error in batch pre-computation", ex);
                _isBatchMode = false;
                _batchDetectionCache.Clear();

                if (!_isProcessing)
                {
                    await ProcessQueue();
                }
            }
        }

        public static System.Collections.Generic.List<object> GetSeriesMarkers(System.Collections.Generic.List<Episode> episodes)
        {
            return _chapterMarkerService?.GetSeriesMarkers(episodes) ?? new System.Collections.Generic.List<object>();
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
            
            // Clear previous failure reasons to prevent unbounded growth
            if (Plugin.Instance != null)
            {
                Plugin.Progress.FailureReasons?.Clear();
            }

            try
            {
                while (_processingQueue.TryDequeue(out var episode))
                {
                    if (!_isRunning || _cancellationRequested)
                    {
                        LogInfo("Processing cancelled");
                        break;
                    }

                    LogInfo($"Processing episode from queue: {episode.Name}");
                    await ProcessEpisode(episode);

                    await Task.Delay(1000);
                }

                if (Plugin.Instance != null)
                {
                    if (_cancellationRequested)
                    {
                        Plugin.Progress.IsRunning = false;
                        Plugin.Progress.EndTime = DateTime.Now;
                        Plugin.Progress.CurrentItem = "Cancelled";
                        // Keep debug log available even when cancelled
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

        private static void CheckForNewEpisodes(object? state)
        {
            // Check if service is still running under lock to prevent race with disposal
            lock (_timerLock)
            {
                if (_processingTimer == null)
                    return;
            }
            
            if (!_isRunning || _libraryManager == null || _configuration == null || !_configuration.EnableAutoDetection)
                return;

            try
            {
                LogDebug("Checking for new episodes to analyze...");

                var episodes = _libraryManager.GetItemList(new InternalItemsQuery
                {
                    IncludeItemTypes = new[] { "Episode" },
                    IsVirtualItem = false,
                    HasPath = true
                }).OfType<Episode>().ToList();

                var libraryIds = _configuration.LibraryIds ?? Array.Empty<string>();

                foreach (var episode in episodes)
                {
                    if (!_processedEpisodes.ContainsKey(episode.Id.ToString()))
                    {
                        if (libraryIds.Length > 0)
                        {
                            var libraryId = episode.GetTopParent()?.Id.ToString();
                            if (string.IsNullOrEmpty(libraryId) || !libraryIds.Contains(libraryId))
                            {
                                continue;
                            }
                        }

                        QueueEpisode(episode);
                    }
                }
            }
            catch (Exception ex)
            {
                LogError("Error checking for new episodes", ex);
            }
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

                var (success, creditsStart, failureReason) = await _episodeProcessor.ProcessEpisode(
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
                        Plugin.Progress.SuccessDetails[episodeKey] = FormatTime(creditsStart);
                    }

                    // Only add to processed episodes if not a dry run
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

                    // Only add to processed episodes if not a dry run
                    if (!_isDryRun)
                    {
                        _processedEpisodes.TryAdd(episodeId, DateTime.UtcNow);
                    }
                }

                if (Plugin.Instance != null)
                {
                    Plugin.Progress.ProcessedItems++;
                    Plugin.Progress.CurrentItemProgress = 100;

                    if (Plugin.Progress.ProcessedItems >= Plugin.Progress.TotalItems)
                    {
                        Plugin.Progress.IsRunning = false;
                        Plugin.Progress.EndTime = DateTime.Now;
                        Plugin.Progress.CurrentItem = "Complete";
                    }
                }
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
    }
}

