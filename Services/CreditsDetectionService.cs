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
        private static volatile bool _isRunning;
        private static ConcurrentDictionary<string, DateTime> _processedEpisodes = new ConcurrentDictionary<string, DateTime>();
        private static EpisodeQueue _processingQueue = new EpisodeQueue(MaxQueueSize);
        private static SemaphoreSlim? _processingSemaphore = new SemaphoreSlim(1, 1);
        private static volatile bool _isProcessing = false;
        private static volatile bool _cancellationRequested = false;
        private static CancellationTokenSource? _cancellationTokenSource = null;

        /// <summary>Indicates whether cancellation has been requested via <see cref="CancelProcessing"/>.</summary>
        public static bool IsCancellationRequested => _cancellationRequested;
        private static bool _isDryRun = false;
        private static volatile bool _isManualDetectionRun = false;
        private static bool _previousRestoreAfterScanState = false;
        private static bool _previousItemAddedHandlerState = false;

        private const int MaxQueueSize = 1000;
        private const int MaxProcessedEpisodesCache = 10000;
        private const int MaxBatchDetectionCacheSize = 5000;
        private const int MaxEpisodeStatusMessagesCache = 1000;

        private static volatile DetectionCoordinator? _detectionCoordinator;
        private static DebugLogger? _debugLogger;
        private static ChapterMarkerService? _chapterMarkerService;
        private static EpisodeProcessor? _episodeProcessor;
        private static PluginCoordinationService? _pluginCoordination;
        private static TheIntroDbService? _theIntroDbService;

        private static ConcurrentDictionary<string, DateTime> _recentlyRestoredEpisodes = new ConcurrentDictionary<string, DateTime>();
        private const int RestoreGuardSeconds = 120;
        private static ConcurrentDictionary<string, bool> _pendingDeferredRestoreChecks = new ConcurrentDictionary<string, bool>();
        private const int DeferredRestoreDelaySeconds = 45;

        private static ConcurrentDictionary<string, List<(string method, double timestamp)>> _batchDetectionCache = new ConcurrentDictionary<string, List<(string method, double timestamp)>>();
        private static ConcurrentDictionary<string, DateTime> _batchDetectionCacheInsertTime = new ConcurrentDictionary<string, DateTime>();
        private static bool _isBatchMode = false;

        // When a single episode triggers a season-wide Chromaprint scan, only save markers
        // for the originally requested episode IDs. Empty = no restriction (save all).
        private static ConcurrentDictionary<string, bool> _singleEpisodeTargets = new ConcurrentDictionary<string, bool>();
        
        private static ConcurrentDictionary<string, ConcurrentQueue<string>> _episodeStatusMessages = new ConcurrentDictionary<string, ConcurrentQueue<string>>();

        private static RuleMatchingService? _cachedRuleMatchingService;
        private static Timer? _cacheCleanupTimer;
        private static readonly ConcurrentDictionary<string, SeasonDispatchState> _pendingSeasonDispatches =
            new ConcurrentDictionary<string, SeasonDispatchState>(StringComparer.Ordinal);

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
            if (ex is OperationCanceledException || ex is TaskCanceledException)
            {
                _debugLogger?.LogInfo($"{message} (Task was canceled)");
                return;
            }
            _debugLogger?.LogError(message, ex);
        }

        public static void LogToDebug(string level, string message)
        {
            _debugLogger?.LogToDebug(level, message);
        }
        
        public static void AddEpisodeStatusMessage(string episodeId, string message)
        {
            var messages = _episodeStatusMessages.GetOrAdd(episodeId, _ => new ConcurrentQueue<string>());

            messages.Enqueue(message);
            while (messages.Count > 50)
                messages.TryDequeue(out _);

            if (_episodeStatusMessages.Count > MaxEpisodeStatusMessagesCache)
                CleanupEpisodeStatusMessages();
        }
        
        private static List<string> GetAndClearEpisodeStatusMessages(string episodeId)
        {
            if (_episodeStatusMessages.TryRemove(episodeId, out var messages))
            {
                return messages.ToList();
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
            _cachedRuleMatchingService = new RuleMatchingService(logger, configuration);
            
            if (_itemRepository != null)
            {
                _chapterMarkerService = new ChapterMarkerService(_logger, _itemRepository);
                _theIntroDbService?.Dispose();
                _theIntroDbService = configuration.EnableTheIntroDB ? new TheIntroDbService(_logger, configuration) : null;
                _episodeProcessor = new EpisodeProcessor(_logger, _libraryManager, _detectionCoordinator, 
                    _chapterMarkerService, _debugLogger, configuration, _cachedRuleMatchingService!, _theIntroDbService);
            }

            _logger.Info("Credits Detection Service started");

            _previousItemAddedHandlerState = configuration.EnableAutoDetection || configuration.EnableTracerMode || configuration.OnlyProcessNewEpisodes;
            if (_libraryManager != null)
            {
                _libraryManager.ItemAdded -= OnItemAdded;
                if (_previousItemAddedHandlerState)
                {
                    _libraryManager.ItemAdded += OnItemAdded;
                    _logger.Info("ItemAdded event handler registered (auto-detection, tracer, or OnlyProcessNewEpisodes enabled)");
                }

                _libraryManager.ItemUpdated -= OnItemUpdated;
                _previousRestoreAfterScanState = configuration.EnableAutoRestoreAfterScan;
                if (configuration.EnableAutoRestoreAfterScan)
                {
                    _libraryManager.ItemUpdated += OnItemUpdated;
                    _logger.Info("Auto-restore enabled: ItemUpdated event handler registered");
                }
            }

            _cacheCleanupTimer?.Dispose();
            _cacheCleanupTimer = new Timer(_ =>
            {
                if (!_isProcessing) { CleanupBatchDetectionCache(); CleanupOldProcessedEpisodes(); }
            }, null, TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(10));
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
                var oldDetectionCoordinator = _detectionCoordinator;
                _detectionCoordinator = new DetectionCoordinator(_logger, configuration);
                
                var oldDebugLogger = _debugLogger;
                _debugLogger = new DebugLogger(_logger, configuration);
                
                var oldPluginCoordination = _pluginCoordination;
                _pluginCoordination = new PluginCoordinationService(_logger, configuration);
                _cachedRuleMatchingService = new RuleMatchingService(_logger, configuration);

                var oldChapterMarkerService = _chapterMarkerService;
                var oldEpisodeProcessor = _episodeProcessor;
                var oldTheIntroDbService = _theIntroDbService;
                
                if (_itemRepository != null)
                {
                    _chapterMarkerService = new ChapterMarkerService(_logger, _itemRepository);
                    _theIntroDbService = configuration.EnableTheIntroDB ? new TheIntroDbService(_logger, configuration) : null;
                    _episodeProcessor = new EpisodeProcessor(_logger, _libraryManager, _detectionCoordinator, 
                        _chapterMarkerService, _debugLogger, configuration, _cachedRuleMatchingService!, _theIntroDbService);
                }

                // Delay disposal of old services to allow in-flight background tasks to finish using them
                var cts = _cancellationTokenSource;
                Task.Delay(10000, cts?.Token ?? CancellationToken.None).ContinueWith(_ =>
                {
                    try
                    {
                        oldDetectionCoordinator?.Dispose();
                        oldDebugLogger?.Dispose();
                        oldPluginCoordination?.Dispose();
                        oldTheIntroDbService?.Dispose();
                    }
                    catch { }
                }, TaskScheduler.Default);
            }

            if (_libraryManager != null && _isRunning)
            {
                var needsItemAddedHandler = configuration.EnableAutoDetection || configuration.EnableTracerMode || configuration.OnlyProcessNewEpisodes;
                if (needsItemAddedHandler != _previousItemAddedHandlerState)
                {
                    _libraryManager.ItemAdded -= OnItemAdded;
                    _previousItemAddedHandlerState = needsItemAddedHandler;
                    if (needsItemAddedHandler)
                    {
                        _libraryManager.ItemAdded += OnItemAdded;
                        _logger?.Info("ItemAdded event handler registered (auto-detection, tracer, or OnlyProcessNewEpisodes enabled)");
                    }
                    else
                    {
                        _logger?.Info("ItemAdded event handler unregistered (auto-detection, tracer, and OnlyProcessNewEpisodes all disabled)");
                    }
                }

                var previousRestoreState = _previousRestoreAfterScanState;
                _previousRestoreAfterScanState = configuration.EnableAutoRestoreAfterScan;
                if (configuration.EnableAutoRestoreAfterScan != previousRestoreState)
                {
                    _libraryManager.ItemUpdated -= OnItemUpdated;

                    if (configuration.EnableAutoRestoreAfterScan)
                    {
                        _libraryManager.ItemUpdated += OnItemUpdated;
                        _logger?.Info("Auto-restore enabled: ItemUpdated event handler registered");
                    }
                    else
                    {
                        _logger?.Info("Auto-restore disabled: ItemUpdated event handler unregistered");
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
                    System.Threading.Interlocked.Exchange(ref _batchDetectionCacheInsertTime, new ConcurrentDictionary<string, DateTime>());
                }
                else
                {
                    _batchDetectionCache.Clear();
                    _batchDetectionCacheInsertTime.Clear();
                }
                
                CleanupOldProcessedEpisodes();
                
                while (_processingQueue.TryDequeue(out _)) { }
                
                _episodeStatusMessages.Clear();
                System.Threading.Interlocked.Exchange(ref _episodeStatusMessages, new ConcurrentDictionary<string, ConcurrentQueue<string>>());
                
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
            
            foreach (var kvp in _processedEpisodes)
            {
                if (kvp.Value < cutoffTime)
                {
                    _processedEpisodes.TryRemove(kvp.Key, out _);
                }
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

            var oldest = _batchDetectionCacheInsertTime
                .OrderBy(kvp => kvp.Value)
                .Take(entriesToRemove)
                .Select(kvp => kvp.Key)
                .ToList();

            var removed = 0;
            foreach (var key in oldest)
            {
                if (_batchDetectionCache.TryRemove(key, out _))
                {
                    _batchDetectionCacheInsertTime.TryRemove(key, out _);
                    removed++;
                }
            }

            if (removed < entriesToRemove)
            {
                // Fallback: remove any remaining entries not tracked in the insert-time dict
                foreach (var kvp in _batchDetectionCache)
                {
                    if (removed >= entriesToRemove) break;
                    if (!_batchDetectionCacheInsertTime.ContainsKey(kvp.Key) && _batchDetectionCache.TryRemove(kvp.Key, out _))
                        removed++;
                }
            }

            LogDebug($"Cleaned up batch detection cache: removed {removed} entries");
        }
        
        private static void CleanupEpisodeStatusMessages()
        {
            if (_episodeStatusMessages.Count <= MaxEpisodeStatusMessagesCache)
                return;

            var removed = 0;
            var entriesToRemove = _episodeStatusMessages.Count - MaxEpisodeStatusMessagesCache;

            // Prefer removing already-drained queues (episode finished, messages consumed)
            foreach (var kvp in _episodeStatusMessages)
            {
                if (removed >= entriesToRemove) break;
                if (kvp.Value.IsEmpty && _episodeStatusMessages.TryRemove(kvp.Key, out _))
                    removed++;
            }

            // Fall back to arbitrary removal if still over limit
            foreach (var kvp in _episodeStatusMessages)
            {
                if (removed >= entriesToRemove) break;
                if (_episodeStatusMessages.TryRemove(kvp.Key, out _))
                    removed++;
            }

            LogDebug($"Cleaned up episode status messages cache: removed {removed} entries");
        }

        public static void Stop()
        {
            _isRunning = false;
            _isProcessing = false;

            _cacheCleanupTimer?.Dispose();
            _cacheCleanupTimer = null;

            if (_libraryManager != null)
            {
                try
                {
                    _libraryManager.ItemAdded -= OnItemAdded;
                    _libraryManager.ItemUpdated -= OnItemUpdated;
                }
                catch (Exception ex)
                {
                    LogError("Error unregistering library event handlers", ex);
                }
            }

            _detectionCoordinator?.Dispose();
            _detectionCoordinator = null;

            try
            {
                var cts = Interlocked.Exchange(ref _cancellationTokenSource, null);
                if (cts != null)
                {
                    cts.Cancel();
                    cts.Dispose();
                }
                var oldSemaphore = Interlocked.Exchange(ref _processingSemaphore, new SemaphoreSlim(1, 1));
                oldSemaphore?.Dispose();
            }
            catch (Exception ex)
            {
                LogError("Error disposing semaphore", ex);
            }
            
            _debugLogger?.Dispose();
            _debugLogger = null;

            _pluginCoordination?.Dispose();
            _pluginCoordination = null;

            _episodeProcessor = null;
            _chapterMarkerService = null;
            _cachedRuleMatchingService = null;

            _theIntroDbService?.Dispose();
            _theIntroDbService = null;

            while (_processingQueue.TryDequeue(out _)) { }
            _processingQueue.Dispose();
            _processingQueue = new EpisodeQueue(MaxQueueSize);

            // Recreate dictionaries to release capacity
            _batchDetectionCache.Clear();
            System.Threading.Interlocked.Exchange(ref _batchDetectionCache, new ConcurrentDictionary<string, List<(string method, double timestamp)>>());
            _batchDetectionCacheInsertTime.Clear();
            System.Threading.Interlocked.Exchange(ref _batchDetectionCacheInsertTime, new ConcurrentDictionary<string, DateTime>());
            
            _processedEpisodes.Clear();
            System.Threading.Interlocked.Exchange(ref _processedEpisodes, new ConcurrentDictionary<string, DateTime>());
            
            _episodeStatusMessages.Clear();
            System.Threading.Interlocked.Exchange(ref _episodeStatusMessages, new ConcurrentDictionary<string, ConcurrentQueue<string>>());

            _recentlyRestoredEpisodes.Clear();
            _pendingDeferredRestoreChecks.Clear();
            _singleEpisodeTargets.Clear();

            foreach (var s in _pendingSeasonDispatches.Values)
                s.Dispose();
            _pendingSeasonDispatches.Clear();

            LogInfo("Credits Detection Service stopped");
        }

        private static void OnItemAdded(object? sender, ItemChangeEventArgs e)
        {
            if (!_isRunning || _configuration == null)
                return;

            var autoDetect = _configuration.EnableAutoDetection;
            var tracerEnabled = _configuration.EnableTracerMode;
            var trackPending = _configuration.OnlyProcessNewEpisodes;

            if (!autoDetect && !tracerEnabled && !trackPending)
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

                    // Track in Tracer and pending queue before library filter — these apply to all new episodes
                    if (tracerEnabled && Plugin.TracerService != null)
                        Plugin.TracerService.TrackEpisode(episode);

                    if (trackPending && Plugin.PendingEpisodesService != null)
                        Plugin.PendingEpisodesService.TrackEpisode(episode);

                    // Library filter applies only to auto-detection.
                    // CollectionFolders (virtual library roots) are NOT physical ancestors — they never
                    // appear in GetParent() chains. Use path-based matching via GetVirtualFolders().
                    var libraryIds = _configuration.LibraryIds ?? Array.Empty<string>();
                    if (libraryIds.Length > 0 && _libraryManager != null)
                    {
                        string? matchedLibrary = null;
                        if (!string.IsNullOrEmpty(episode.Path))
                            matchedLibrary = RuleMatchingService.FindLibraryNameForPath(episode.Path, libraryIds, _libraryManager);

                        LogDebug($"Episode {episode.Name} - library match={matchedLibrary != null} (library={matchedLibrary ?? "none"}), Configured: [{string.Join(", ", libraryIds)}]");

                        if (matchedLibrary == null)
                        {
                            LogDebug($"Skipping auto-detection for {episode.Name} - not in configured libraries");
                            return;
                        }
                    }

                    LogInfo($"New episode detected: {episode.SeriesName} - {episode.Name}");

                    // Stop here if auto-detection is off
                    if (!autoDetect)
                        return;

                    var seasonKey = $"{episode.SeriesId}_{episode.ParentIndexNumber!.Value}";
                    var dispatchState = _pendingSeasonDispatches.GetOrAdd(seasonKey, _ => new SeasonDispatchState());
                    dispatchState.EpisodeIds.TryAdd(episode.Id, 0);
                    LogInfo($"Registered '{episode.Name}' in dispatch window for '{episode.SeriesName}' Season {episode.ParentIndexNumber}");
                    dispatchState.ResetTimer(20000, () => DispatchPendingSeason(seasonKey));
                }
            }
            catch (Exception ex)
            {
                LogError($"Error handling ItemAdded event for {e.Item?.Name}", ex);
            }
        }

        private static void DispatchPendingSeason(string seasonKey)
        {
            if (!_pendingSeasonDispatches.TryRemove(seasonKey, out var state))
                return;
            state.Dispose();

            if (!_isRunning || _configuration == null || !_configuration.EnableAutoDetection)
                return;

            _ = Task.Run(() =>
            {
                try
                {
                    var episodeIds = state.EpisodeIds.Keys.ToList();
                    if (episodeIds.Count == 0 || _libraryManager == null)
                        return;

                    var validEpisodes = new List<Episode>();
                    foreach (var id in episodeIds)
                    {
                        var ep = _libraryManager.GetItemById(id) as Episode;
                        if (ep == null || string.IsNullOrEmpty(ep.Path)) continue;

                        if (ep.Path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                            ep.Path.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                            continue;

                        try
                        {
                            if (!File.Exists(ep.Path)) continue;
                            var fi = new FileInfo(ep.Path);
                            using (fi.Open(FileMode.Open, FileAccess.Read, FileShare.Read)) { }
                            if ((DateTime.UtcNow - fi.LastWriteTimeUtc).TotalSeconds < 5)
                            {
                                LogDebug($"File still being modified: {ep.Name}");
                                continue;
                            }
                            validEpisodes.Add(ep);
                        }
                        catch (IOException)
                        {
                            LogDebug($"File is locked and still being written: {ep.Name}");
                        }
                        catch (Exception ex)
                        {
                            LogDebug($"Skipping {ep.Name} due to file access error: {ex.Message}");
                        }
                    }

                    if (validEpisodes.Count == 0)
                    {
                        LogWarn($"No valid files ready for season dispatch '{seasonKey}'");
                        return;
                    }

                    var representative = validEpisodes[0];
                    LogInfo($"Dispatching {validEpisodes.Count} episode(s) for '{representative.SeriesName}' Season {representative.ParentIndexNumber}");

                    if (_configuration != null && _logger != null && representative.Series != null)
                    {
                        var rms = _cachedRuleMatchingService;
                        if (rms != null)
                        {
                            var effectiveCfg = rms.GetEffectiveConfiguration(representative.Series);
                            bool usesHash = effectiveCfg.DetectionMode == DetectionMode.HashOnly ||
                                            effectiveCfg.DetectionMode == DetectionMode.HashWithOcrFallback ||
                                            effectiveCfg.DetectionMode == DetectionMode.OcrWithHashFallback;
                            bool isAnimeBlackFrame = effectiveCfg.EnableAnimeDetection &&
                                                    effectiveCfg.AnimeDetectionMethod == AnimeDetectionMethod.BlackFrame &&
                                                    CheckIfAnime(representative.Series.Id.ToString("N"));
                            bool isBlackFrameOnly = effectiveCfg.DetectionMode == DetectionMode.BlackFrameOnly;

                            if (usesHash && !isAnimeBlackFrame && !isBlackFrameOnly)
                            {
                                LogInfo($"Hash detection for '{representative.SeriesName}' — queuing season with {validEpisodes.Count} target(s)");
                                _singleEpisodeTargets.Clear();
                                foreach (var ep in validEpisodes)
                                    _singleEpisodeTargets[ep.Id.ToString()] = true;
                                QueueSeasonForEpisode(representative, isManualDetection: false);
                                return;
                            }
                        }
                    }

                    foreach (var ep in validEpisodes)
                    {
                        LogInfo($"Processing episode after delay: {ep.SeriesName} - {ep.Name}");
                        QueueEpisode(ep);
                    }
                }
                catch (Exception ex)
                {
                    LogError($"Error dispatching pending season '{seasonKey}'", ex);
                }
            }).ContinueWith(t =>
            {
                if (t.IsFaulted && t.Exception != null)
                    LogError($"Season dispatch task failed for '{seasonKey}'", t.Exception.GetBaseException());
            }, TaskScheduler.Default);
        }

        private static void OnItemUpdated(object? sender, ItemChangeEventArgs e)
        {
            if (!_isRunning || _isProcessing || _configuration == null || !_configuration.EnableAutoRestoreAfterScan)
                return;

            if (string.IsNullOrWhiteSpace(_configuration.BackupFolderPath))
                return;

            if (e.Item is not Episode episode)
                return;

            if (episode.IsVirtualItem || episode.ParentIndexNumber == null || episode.ParentIndexNumber == 0)
                return;

            try
            {
                var episodeId = episode.Id.ToString();

                if (_recentlyRestoredEpisodes.TryGetValue(episodeId, out var restoredAt) &&
                    (DateTime.UtcNow - restoredAt).TotalSeconds < RestoreGuardSeconds)
                {
                    return;
                }

                if (_itemRepository == null)
                    return;

                var chapters = _itemRepository.GetChapters(episode);
                var hasCreditsMarker = chapters?.Any(c => GetMarkerType(c) == "CreditsStart") ?? false;

                if (hasCreditsMarker)
                {
                    ScheduleDeferredRestoreCheck(episode, episodeId);
                    return;
                }

                if (Plugin.CreditsBackupService == null)
                    return;

                var backupService = Plugin.CreditsBackupService;
                var backupFolder = _configuration.BackupFolderPath;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var restored = await backupService.RestoreEpisodeMarkerFromBackup(episode, backupFolder).ConfigureAwait(false);
                        if (restored)
                        {
                            _recentlyRestoredEpisodes[episodeId] = DateTime.UtcNow;
                            var cutoff = DateTime.UtcNow.AddSeconds(-RestoreGuardSeconds * 2);
                            foreach (var key in _recentlyRestoredEpisodes.Where(kvp => kvp.Value < cutoff).Select(kvp => kvp.Key).ToList())
                                _recentlyRestoredEpisodes.TryRemove(key, out _);
                            ScheduleDeferredRestoreCheck(episode, episodeId);
                        }
                    }
                    catch (Exception ex)
                    {
                        LogError($"Error in OnItemUpdated restore handler for '{episode.Name}'", ex);
                    }
                });
            }
            catch (Exception ex)
            {
                LogError($"Error in OnItemUpdated for '{episode.Name}'", ex);
            }
        }

        private static void ScheduleDeferredRestoreCheck(Episode episode, string episodeId)
        {
            if (!_pendingDeferredRestoreChecks.TryAdd(episodeId, true))
                return;

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(DeferredRestoreDelaySeconds)).ConfigureAwait(false);
                    _pendingDeferredRestoreChecks.TryRemove(episodeId, out _);

                    if (!_isRunning || _configuration == null || !_configuration.EnableAutoRestoreAfterScan)
                        return;

                    if (string.IsNullOrWhiteSpace(_configuration.BackupFolderPath))
                        return;

                    if (_itemRepository == null || Plugin.CreditsBackupService == null)
                        return;

                    var chapters = _itemRepository.GetChapters(episode);
                    var stillHasMarker = chapters?.Any(c => GetMarkerType(c) == "CreditsStart") ?? false;

                    if (!stillHasMarker)
                    {
                        LogInfo($"Deferred restore check: credits marker was removed for '{episode.Name}', restoring from backup...");
                        var restored = await Plugin.CreditsBackupService.RestoreEpisodeMarkerFromBackup(episode, _configuration.BackupFolderPath).ConfigureAwait(false);
                        if (restored)
                        {
                            _recentlyRestoredEpisodes[episodeId] = DateTime.UtcNow;
                            var cutoff = DateTime.UtcNow.AddSeconds(-RestoreGuardSeconds * 2);
                            foreach (var key in _recentlyRestoredEpisodes.Where(kvp => kvp.Value < cutoff).Select(kvp => kvp.Key).ToList())
                                _recentlyRestoredEpisodes.TryRemove(key, out _);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _pendingDeferredRestoreChecks.TryRemove(episodeId, out _);
                    LogError($"Error in deferred restore check for '{episode.Name}'", ex);
                }
            });
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
                var ruleMatchingService = _cachedRuleMatchingService ?? new RuleMatchingService(_logger, _configuration);
                var effectiveConfig = ruleMatchingService.GetEffectiveConfiguration(episode.Series);
                
                bool usesHashDetection = effectiveConfig.DetectionMode == DetectionMode.HashOnly ||
                                       effectiveConfig.DetectionMode == DetectionMode.HashWithOcrFallback ||
                                       effectiveConfig.DetectionMode == DetectionMode.OcrWithHashFallback;

                bool isAnimeBlackFrame = effectiveConfig.EnableAnimeDetection &&
                                        effectiveConfig.AnimeDetectionMethod == AnimeDetectionMethod.BlackFrame &&
                                        CheckIfAnime(episode.Series?.Id.ToString("N") ?? string.Empty);

                bool isBlackFrameOnly = effectiveConfig.DetectionMode == DetectionMode.BlackFrameOnly;

                if (usesHashDetection && !isAnimeBlackFrame && !isBlackFrameOnly && !_isBatchMode)
                {
                    LogInfo($"Hash detection enabled for '{episode.Series?.Name}' - queueing entire season instead of single episode");
                    // Record which episode was originally requested so only it gets a marker saved.
                    _singleEpisodeTargets.Clear();
                    _singleEpisodeTargets[episodeId] = true;
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
                            Plugin.Progress.SkipReasons[episodeKey] = "Already has credits marker";
                            Plugin.Progress.SkippedItems++;
                        }

                        return;
                    }

                    if (_configuration.UseEmbeddedChapterMarkersScheduled && _chapterMarkerService != null)
                    {
                        var imported = _chapterMarkerService.TryImportEmbeddedCreditChapter(episode);
                        if (imported)
                        {
                            LogInfo($"Skipping episode {episode.Name} - credits marker imported from embedded chapter");

                            if (Plugin.Instance != null)
                            {
                                var series = episode.Series;
                                var episodeKey = series != null
                                    ? $"{series.Name} S{episode.ParentIndexNumber:00}E{episode.IndexNumber:00}"
                                    : episode.Name;
                                Plugin.Progress.SkipReasons[episodeKey] = "Imported from embedded chapter";
                                Plugin.Progress.SkippedItems++;
                            }

                            return;
                        }
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

            // Check for previously failed episodes (both automatic and manual detection)
            if (_configuration != null && _configuration.SkipPreviouslyFailedEpisodes)
            {
                var hasFailed = episode.ProviderIds?.TryGetValue("EmbyCredits.Fail", out var failValue) == true && failValue == "true";
                
                // For manual detection, only skip if IgnoreFailureMarkers is FALSE
                // For automatic detection, always skip
                bool shouldSkip = hasFailed && (!isManualDetection || !_configuration.IgnoreFailureMarkers);
                
                if (shouldSkip)
                {
                    var skipReason = isManualDetection 
                        ? "previously failed detection - enable 'Allow retry of failed episodes' in Settings to override"
                        : "previously failed detection (SkipPreviouslyFailedEpisodes is enabled)";
                    
                    LogInfo($"Skipping episode {episode.Name} - {skipReason}");
                    
                    if (Plugin.Instance != null)
                    {
                        var series = episode.Series;
                        var episodeKey = series != null
                            ? $"{series.Name} S{episode.ParentIndexNumber:00}E{episode.IndexNumber:00}"
                            : episode.Name;
                        Plugin.Progress.SkipReasons[episodeKey] = "Previously failed detection";
                        Plugin.Progress.SkippedItems++;
                    }
                    
                    return;
                }
                else if (hasFailed && isManualDetection && _configuration.IgnoreFailureMarkers)
                {
                    LogInfo($"Retrying previously failed episode {episode.Name} (IgnoreFailureMarkers is enabled)");
                }
            }

            // Skip if file is unchanged since last detection
            if (!isManualDetection &&
                _configuration != null &&
                _configuration.SkipDetectionIfFileUnchanged &&
                !string.IsNullOrWhiteSpace(_configuration.BackupFolderPath) &&
                Plugin.CreditsBackupService != null)
            {
                if (!Plugin.CreditsBackupService.HasFileChanged(episode, _configuration.BackupFolderPath))
                {
                    LogInfo($"Skipping {episode.Name} — file unchanged since last detection");
                    if (Plugin.Instance != null)
                    {
                        var skipSeries = episode.Series;
                        var skipKey = skipSeries != null
                            ? $"{skipSeries.Name} S{episode.ParentIndexNumber:00}E{episode.IndexNumber:00}"
                            : episode.Name;
                        Plugin.Progress.SkipReasons[skipKey] = "File unchanged since last detection";
                        Plugin.Progress.SkippedItems++;
                    }
                    return;
                }
            }

            if (_processingQueue.Count >= MaxQueueSize)
            {
                LogWarn($"Queue is full ({_processingQueue.Count} episodes). Skipping {episode.Name} to prevent memory issues.");
                return;
            }

            if (!_processingQueue.TryEnqueue(episode))
            {
                LogWarn($"Failed to enqueue episode: {episode.Name}");
                return;
            }
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

            var seasonItem = episode.ParentId != 0 ? _libraryManager.GetItemById(episode.ParentId) : null;
            long ancestorId = seasonItem?.InternalId ?? seriesInternalId;

            var seasonEpisodes = _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { "Episode" },
                IsVirtualItem = false,
                HasPath = true,
                AncestorIds = new[] { ancestorId }
            }).OfType<Episode>()
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
                QueueSeriesManual(seasonEpisodes, skipExistingMarkers: false, preserveTargets: true);
            }
            else
            {
                QueueSeries(seasonEpisodes, clearTargets: false);
            }
        }

        public static void QueueSeries(List<Episode> episodes, bool clearTargets = true)
        {
            ClearCache();

            // Ensure batch cache is fully reset
            _batchDetectionCache.Clear();
            System.Threading.Interlocked.Exchange(ref _batchDetectionCache, new ConcurrentDictionary<string, List<(string method, double timestamp)>>());
            _batchDetectionCacheInsertTime.Clear();
            System.Threading.Interlocked.Exchange(ref _batchDetectionCacheInsertTime, new ConcurrentDictionary<string, DateTime>());

            if (clearTargets)
                _singleEpisodeTargets.Clear();

            _processingQueue.Clear();
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
                if (_processingQueue.TryEnqueue(episode))
                {
                    queuedCount++;
                }
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

            var clearedCount = _processingQueue.Count;
            _processingQueue.Clear();

            _processedEpisodes.Clear();
            System.Threading.Interlocked.Exchange(ref _processedEpisodes, new ConcurrentDictionary<string, DateTime>());
            
            _episodeStatusMessages.Clear();
            System.Threading.Interlocked.Exchange(ref _episodeStatusMessages, new ConcurrentDictionary<string, ConcurrentQueue<string>>());
            
            EmbyCredits.Services.DetectionMethods.OcrDetection.ClearAllCache();
            EmbyCredits.Services.DetectionMethods.ChromaprintDetection.ClearAllCache();
            EmbyCredits.Services.DetectionMethods.BlackFrameDetection.ClearAllCache();

            LogInfo($"Queue cleared: {clearedCount} items removed, processed cache cleared");
            ResetProgressToCancelling();
        }

        public static int ClearQueue()
        {
            LogInfo("Clearing processing queue");

            var clearedCount = _processingQueue.Count;
            _processingQueue.Clear();

            _isProcessing = false;
            _cancellationRequested = false;

            LogInfo($"Queue cleared: {clearedCount} items removed, flags reset");

            return clearedCount;
        }

        /// <summary>
        /// Resets cancellation state and creates a fresh <see cref="CancellationTokenSource"/> so that
        /// a newly-started scheduled task run is not immediately aborted by a stale cancellation flag
        /// left over from a previous run or a UI cancel action.
        /// </summary>
        public static void ResetForScheduledTask()
        {
            _cancellationRequested = false;
            _isDryRun = false;
            var newCts = new CancellationTokenSource();
            Interlocked.Exchange(ref _cancellationTokenSource, newCts)?.Dispose();
            LogInfo("Cancellation state reset for scheduled task run");
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

            _isManualDetectionRun = true;

            if (_configuration?.UseEmbeddedChapterMarkersManual == true && _chapterMarkerService != null)
            {
                var imported = _chapterMarkerService.TryImportEmbeddedCreditChapter(episode);
                if (imported)
                {
                    LogInfo($"Skipping episode {episode.Name} - credits marker imported from embedded chapter");

                    if (Plugin.Instance != null)
                    {
                        var episodeKey = series != null
                            ? $"{series.Name} S{episode.ParentIndexNumber:00}E{episode.IndexNumber:00}"
                            : episode.Name;
                        Plugin.Progress.SuccessDetails[episodeKey] = "(imported from embedded chapter)";
                        Plugin.Progress.SuccessfulItems++;
                    }

                    return;
                }
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

            if (_configuration != null && _configuration.SkipPreviouslyFailedEpisodes && !_configuration.IgnoreFailureMarkers)
            {
                var hasFailed = episode.ProviderIds?.TryGetValue("EmbyCredits.Fail", out var failValue) == true && failValue == "true";
                if (hasFailed)
                {
                    LogInfo($"Skipping episode {episode.Name} - previously failed detection");
                    
                    if (Plugin.Instance != null)
                    {
                        var episodeKey = series != null
                            ? $"{series.Name} S{episode.ParentIndexNumber:00}E{episode.IndexNumber:00}"
                            : episode.Name;
                        Plugin.Progress.SkipReasons[episodeKey] = "Previously failed detection";
                        Plugin.Progress.SkippedItems++;
                    }
                    
                    return;
                }
            }

            QueueEpisode(episode, isManualDetection: true);
        }

        public static void QueueSeriesManual(List<Episode> episodes, bool skipExistingMarkers = false, bool? ignoreFailureMarkers = null, bool preserveTargets = false)
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

            var originalTotalCount = episodes.Count;
            var episodesToQueue = episodes.ToList();
            var skippedEpisodes = new List<string>();
            
            if (Plugin.Instance != null)
            {
                Plugin.Progress.SkipReasons.Clear();
                Plugin.Progress.SkippedItems = 0;
            }

            _isManualDetectionRun = true;

            // Import embedded credit chapters when the setting is enabled
            if (_configuration?.UseEmbeddedChapterMarkersManual == true && _chapterMarkerService != null)
            {
                var remainingAfterImport = new List<Episode>();
                foreach (var episode in episodesToQueue)
                {
                    var imported = _chapterMarkerService.TryImportEmbeddedCreditChapter(episode);
                    if (imported)
                    {
                        var series2 = episode.Series;
                        var episodeKey = series2 != null
                            ? $"{series2.Name} S{episode.ParentIndexNumber:00}E{episode.IndexNumber:00}"
                            : episode.Name;
                        LogInfo($"Skipping episode {episode.Name} - credits marker imported from embedded chapter");
                        skippedEpisodes.Add($"{episodeKey} (imported from embedded chapter)");

                        if (Plugin.Instance != null)
                        {
                            Plugin.Progress.SuccessDetails[episodeKey] = "(imported from embedded chapter)";
                            Plugin.Progress.SuccessfulItems++;
                        }
                    }
                    else
                    {
                        remainingAfterImport.Add(episode);
                    }
                }
                episodesToQueue = remainingAfterImport;
            }

            // Filter episodes with existing markers if requested
            if (skipExistingMarkers && _itemRepository != null)
            {
                episodesToQueue = episodesToQueue.Where(episode =>
                {
                    var chapters = _itemRepository.GetChapters(episode);
                    var hasCreditsMarker = chapters.Any(c => 
                    {
                        var markerType = GetMarkerType(c);
                        return markerType == "CreditsStart";
                    });

                    if (hasCreditsMarker)
                    {
                        var series = episode.Series;
                        var episodeKey = series != null
                            ? $"{series.Name} S{episode.ParentIndexNumber:00}E{episode.IndexNumber:00}"
                            : episode.Name;
                        skippedEpisodes.Add($"{episodeKey} (has marker)");
                        
                        if (Plugin.Instance != null)
                        {
                            Plugin.Progress.SkipReasons[episodeKey] = "Already has credits marker";
                            Plugin.Progress.SkippedItems++;
                        }
                        
                        return false;
                    }
                    return true;
                }).ToList();
            }

            // Filter episodes with failure markers based on configuration
            var effectiveIgnoreFailureMarkers = ignoreFailureMarkers ?? _configuration?.IgnoreFailureMarkers ?? false;
            if (_configuration != null && _configuration.SkipPreviouslyFailedEpisodes && !effectiveIgnoreFailureMarkers)
            {
                var filteredByFailure = new List<Episode>();
                
                foreach (var episode in episodesToQueue)
                {
                    var hasFailed = episode.ProviderIds?.TryGetValue("EmbyCredits.Fail", out var failValue) == true && failValue == "true";
                    
                    if (hasFailed)
                    {
                        var series = episode.Series;
                        var episodeKey = series != null
                            ? $"{series.Name} S{episode.ParentIndexNumber:00}E{episode.IndexNumber:00}"
                            : episode.Name;
                        skippedEpisodes.Add($"{episodeKey} (previously failed)");
                        
                        if (Plugin.Instance != null)
                        {
                            Plugin.Progress.SkipReasons[episodeKey] = "Previously failed detection";
                            Plugin.Progress.SkippedItems++;
                        }
                    }
                    else
                    {
                        filteredByFailure.Add(episode);
                    }
                }
                
                episodesToQueue = filteredByFailure;
            }
            else if (_configuration != null && _configuration.SkipPreviouslyFailedEpisodes && effectiveIgnoreFailureMarkers)
            {
                // Count how many failed episodes we're retrying
                var retryCount = episodesToQueue.Count(e => 
                    e.ProviderIds?.TryGetValue("EmbyCredits.Fail", out var failValue) == true && failValue == "true");
                
                if (retryCount > 0)
                {
                    LogInfo($"Retrying {retryCount} previously failed episodes (IgnoreFailureMarkers is enabled)");
                }
            }

            // Log summary of skipped episodes
            if (skippedEpisodes.Count > 0)
            {
                LogInfo($"Skipped {skippedEpisodes.Count} episode(s):");
                foreach (var skipped in skippedEpisodes)
                {
                    LogInfo($"  - {skipped}");
                }
            }

            if (episodesToQueue.Count == 0)
            {
                LogInfo("No episodes to process - all filtered out by skip settings");
                return;
            }

            // Save skip information before QueueSeries resets it
            Dictionary<string, string>? skipReasonsCopy = null;
            int skippedItemsCount = 0;
            if (Plugin.Instance != null && Plugin.Progress.SkipReasons.Count > 0)
            {
                skipReasonsCopy = new Dictionary<string, string>(Plugin.Progress.SkipReasons);
                skippedItemsCount = Plugin.Progress.SkippedItems;
            }

            QueueSeries(episodesToQueue, clearTargets: !preserveTargets);
            
            // Restore skip information after Reset
            if (Plugin.Instance != null && skipReasonsCopy != null)
            {
                foreach (var kvp in skipReasonsCopy)
                {
                    Plugin.Progress.SkipReasons[kvp.Key] = kvp.Value;
                }
                Plugin.Progress.SkippedItems = skippedItemsCount;
                
                // Set TotalItems to original count (including skipped episodes)
                Plugin.Progress.TotalItems = originalTotalCount;
            }
        }

        public static void QueueEpisodeDryRun(Episode episode, bool skipExistingMarkers = false)
        {
            _isDryRun = true;
            _isManualDetectionRun = true;
            
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
            _isManualDetectionRun = true;
            
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
            _isManualDetectionRun = true;
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
            _isManualDetectionRun = true;
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

        public static List<EpisodeMarkerInfo> GetSeriesMarkers(System.Collections.Generic.List<Episode> episodes)
        {
            return _chapterMarkerService?.GetSeriesMarkers(episodes) ?? new List<EpisodeMarkerInfo>();
        }

        public static ChapterMarkerService? GetChapterMarkerService()
        {
            return _chapterMarkerService;
        }

        private static async Task ProcessQueue()
        {
            LogInfo($"ProcessQueue started. Queue count: {_processingQueue.Count}");

            if (_processingSemaphore == null || !await _processingSemaphore.WaitAsync(0))
            {
                LogInfo("ProcessQueue: already processing, skipping");
                return;
            }

            _isProcessing = true;
            LogInfo("ProcessQueue: acquired semaphore, starting processing");

            var isManualRun = _isManualDetectionRun;
            _isManualDetectionRun = false;

            if (Plugin.Instance != null)
            {
                Plugin.Progress.CurrentItem = "Preparing...";
            }

            // Create a new cancellation token source for this processing session
            var newCts = new CancellationTokenSource();
            Interlocked.Exchange(ref _cancellationTokenSource, newCts)?.Dispose();

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
                    if (episode != null)
                    {
                        allEpisodes.Add(episode);
                    }
                }

                LogInfo($"Collected {allEpisodes.Count} episodes from queue");

                // Group episodes by series and season for batch processing
                var episodesBySeason = allEpisodes
                    .Where(e => e.Series != null && e.ParentIndexNumber.HasValue)
                    .GroupBy(e => new { SeriesId = e.Series!.Id.ToString(), SeasonNumber = e.ParentIndexNumber!.Value })
                    .ToList();

                LogInfo($"Grouped {allEpisodes.Count} episodes into {episodesBySeason.Count} seasons for batch processing");

                // Build one rule-matching service for pre-filtering (reused across all season groups)
                RuleMatchingService? queueRuleService = (_configuration != null && _logger != null && _configuration.DetectionRules?.Count > 0)
                    ? new RuleMatchingService(_logger, _configuration)
                    : null;

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

                    // Pre-filter: evaluate rules before any detection work starts
                    if (queueRuleService != null)
                    {
                        var preCheckConfig = queueRuleService.GetEffectiveConfiguration(firstEpisode);
                        if (preCheckConfig.DisableDetection)
                        {
                            var preCheckRuleName = queueRuleService.GetMatchingRuleName(firstEpisode);
                            _logger?.Info($"[RuleMatching] Detection disabled by rule for '{seriesName}' Season {seasonGroup.Key.SeasonNumber} - skipping {seasonEpisodes.Count} episode(s)");
                            if (Plugin.Instance != null)
                            {
                                foreach (var ep in seasonEpisodes)
                                {
                                    var epKey = $"{seriesName} S{ep.ParentIndexNumber:00}E{ep.IndexNumber:00}";
                                    Plugin.Progress.SkipReasons[epKey] = "Detection disabled by rule";
                                    if (!string.IsNullOrEmpty(preCheckRuleName))
                                        Plugin.Progress.AppliedRules[epKey] = preCheckRuleName;
                                    Plugin.Progress.SkippedItems++;
                                    Plugin.Progress.ProcessedItems++;
                                }
                            }
                            processedCount += seasonEpisodes.Count;
                            continue;
                        }
                    }

                    if (Plugin.Instance != null)
                    {
                        Plugin.Progress.CurrentItem = $"Preparing: {seriesName} Season {seasonGroup.Key.SeasonNumber} ({seasonEpisodes.Count} episodes)";
                    }

                    LogInfo($"Processing season batch: {seriesName} Season {seasonGroup.Key.SeasonNumber} ({seasonEpisodes.Count} episodes)");

                    _isBatchMode = true;
                    try
                    {
                        await ProcessSeasonBatch(seasonEpisodes, _cancellationTokenSource?.Token ?? CancellationToken.None, isManualRun);
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
                    else if (_processingQueue.Count == 0)
                    {
                        if (_configuration != null &&
                            _configuration.EnableAutoBackupAfterDetection &&
                            !string.IsNullOrWhiteSpace(_configuration.BackupFolderPath) &&
                            !_isDryRun &&
                            Plugin.CreditsBackupService != null)
                        {
                            try
                            {
                                var distinctSeriesIds = allEpisodes
                                    .Where(e => e.Series != null)
                                    .GroupBy(e => e.Series!.Id)
                                    .Select(g => g.First().Series!)
                                    .ToList();

                                foreach (var series in distinctSeriesIds)
                                {
                                    var seriesEpisodes = _libraryManager?.GetItemList(new InternalItemsQuery
                                    {
                                        IncludeItemTypes = new[] { "Episode" },
                                        IsVirtualItem = false,
                                        HasPath = true,
                                        AncestorIds = new[] { series.InternalId }
                                    }).OfType<Episode>().ToList() ?? new List<Episode>();

                                    await Plugin.CreditsBackupService.SaveSeriesBackupToFile(
                                        series,
                                        seriesEpisodes,
                                        _configuration.BackupFolderPath,
                                        _configuration.MaxScheduledBackups > 0 ? _configuration.MaxScheduledBackups : 10).ConfigureAwait(false);
                                }
                            }
                            catch (Exception backupEx)
                            {
                                LogError("Auto-backup after detection failed", backupEx);
                            }
                        }

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
                
                var cts = Interlocked.Exchange(ref _cancellationTokenSource, null);
                cts?.Dispose();

                if (_processingQueue.Count > 0 && _isRunning && !_cancellationRequested)
                {
                    LogInfo($"Queue received {_processingQueue.Count} episode(s) during processing — starting follow-up cycle");
                    _ = Task.Run(ProcessQueue).ContinueWith(t =>
                    {
                        if (t.IsFaulted && t.Exception != null)
                        {
                            LogError("ProcessQueue follow-up task failed", t.Exception.GetBaseException());
                        }
                    }, TaskScheduler.Default);
                }
            }
        }

        public static async Task ProcessSeasonBatch(List<Episode> seasonEpisodes, CancellationToken cancellationToken, bool isManualRun = false)
        {
            if (_episodeProcessor == null || _configuration == null || seasonEpisodes.Count == 0)
                return;

            var firstEpisode = seasonEpisodes.First();
            var seriesId = firstEpisode.Series?.Id.ToString() ?? string.Empty;
            var seasonNumber = firstEpisode.ParentIndexNumber ?? 0;
            var seriesName = firstEpisode.Series?.Name ?? "Unknown Series";

            _logger?.Info($"Starting batch processing for {seriesName} Season {seasonNumber} ({seasonEpisodes.Count} episodes)");

            var ruleMatchingService = _logger != null ? new RuleMatchingService(_logger, _configuration) : null;
            var effectiveConfig = ruleMatchingService != null
                ? ruleMatchingService.GetEffectiveConfiguration(firstEpisode)
                : _configuration;

            var matchedRuleName = (effectiveConfig != _configuration && ruleMatchingService != null)
                ? ruleMatchingService.GetMatchingRuleName(firstEpisode)
                : null;

            if (effectiveConfig != _configuration)
            {
                _logger?.Info($"Using rule-based configuration for series '{seriesName}'");
            }

            if (effectiveConfig.DisableDetection)
            {
                _logger?.Info($"Detection disabled by rule for series '{seriesName}', skipping {seasonEpisodes.Count} episode(s)");
                return;
            }

            // Filter out previously failed episodes before any detection work begins.
            // This prevents them from wasting CPU in the Chromaprint batch scan and the
            // per-episode processing loop. Manual detection already filters before calling
            // this method, so this primarily fixes the scheduled task path.
            if (_configuration != null && _configuration.SkipPreviouslyFailedEpisodes && !_configuration.IgnoreFailureMarkers)
            {
                var beforeCount = seasonEpisodes.Count;
                seasonEpisodes = seasonEpisodes.Where(ep =>
                {
                    var hasFailed = ep.ProviderIds?.TryGetValue("EmbyCredits.Fail", out var failValue) == true && failValue == "true";
                    if (hasFailed)
                    {
                        var epKey = $"{seriesName} S{ep.ParentIndexNumber:00}E{ep.IndexNumber:00}";
                        if (Plugin.Instance != null)
                        {
                            Plugin.Progress.SkipReasons[epKey] = "Previously failed detection";
                            Plugin.Progress.IncrementSkipped();
                        }
                    }
                    return !hasFailed;
                }).ToList();

                var skippedCount = beforeCount - seasonEpisodes.Count;
                if (skippedCount > 0)
                    _logger?.Info($"Skipped {skippedCount} previously failed episode(s) in {seriesName} Season {seasonNumber}");

                if (seasonEpisodes.Count == 0)
                {
                    _logger?.Info($"All episodes in {seriesName} Season {seasonNumber} were previously failed — nothing to process");
                    return;
                }
            }

            if (Plugin.Instance != null)
            {
                Plugin.Progress.CurrentItem = $"Preparing: {seriesName} Season {seasonNumber} ({seasonEpisodes.Count} episodes)";
            }

            // Pre-query TheIntroDB for all episodes before any CPU-intensive detection runs.
            // Episodes with a database hit are handled directly here and removed from the list
            // so that Chromaprint / OCR / BlackFrame detection is never started for them.
            if (_configuration != null && _configuration.EnableTheIntroDB && _theIntroDbService != null && Plugin.ChapterMarkerService != null)
            {
                var detectionNeeded = new List<Episode>();
                var introDbHits = 0;

                if (Plugin.Instance != null)
                    Plugin.Progress.CurrentItem = $"Querying TheIntroDB: {seriesName} Season {seasonNumber}";

                foreach (var ep in seasonEpisodes)
                {
                    if (cancellationToken.IsCancellationRequested || _cancellationRequested)
                        break;

                    var epId = ep.Id.ToString();
                    var epKey = $"{seriesName} S{ep.ParentIndexNumber:00}E{ep.IndexNumber:00}";

                    // Honour scheduled-task "only process missing" before querying the external API
                    if (!isManualRun && _configuration.ScheduledTaskOnlyProcessMissing && _itemRepository != null)
                    {
                        var existingChapters = _itemRepository.GetChapters(ep);
                        var alreadyHasMarker = existingChapters?.Any(c => GetMarkerType(c) == "CreditsStart") ?? false;
                        if (alreadyHasMarker)
                        {
                            _logger?.Info($"[TheIntroDB pre-filter] Skipping {ep.Name} - already has credits marker");
                            if (Plugin.Instance != null)
                            {
                                Plugin.Progress.SkipReasons[epKey] = "Already has credits marker";
                                Plugin.Progress.IncrementSkipped();
                            }
                            continue;
                        }
                    }

                    var ts = await _theIntroDbService.GetCreditsTimestamp(ep, cancellationToken).ConfigureAwait(false);
                    if (!ts.HasValue || ts.Value <= 0)
                    {
                        detectionNeeded.Add(ep);
                        continue;
                    }

                    double creditsStart = ts.Value;
                    double epDuration = ep.RunTimeTicks.HasValue && ep.RunTimeTicks.Value > 0
                        ? ep.RunTimeTicks.Value / (double)TimeSpan.TicksPerSecond
                        : 0;
                    double finalTimestamp = creditsStart + _configuration.TimestampOffsetSeconds;

                    if (finalTimestamp < 0 || (epDuration > 0 && finalTimestamp >= epDuration))
                    {
                        _logger?.Warn($"[TheIntroDB pre-filter] Timestamp out of range for {ep.Name} ({FormatTime(finalTimestamp)}), falling back to detection");
                        detectionNeeded.Add(ep);
                        continue;
                    }

                    bool isTargetEp = _singleEpisodeTargets.Count == 0 || _singleEpisodeTargets.ContainsKey(epId);
                    bool effectiveDryRun = _isDryRun || !isTargetEp;

                    if (!effectiveDryRun)
                    {
                        Plugin.ChapterMarkerService.SaveCreditsMarker(ep, finalTimestamp);

                        if (ep.ProviderIds != null && ep.ProviderIds.ContainsKey("EmbyCredits.Fail"))
                        {
                            ep.ProviderIds.Remove("EmbyCredits.Fail");
                            _libraryManager?.UpdateItem(ep, ep.Parent, ItemUpdateType.MetadataEdit, null!);
                        }
                    }

                    _logger?.Info($"✓ [{(effectiveDryRun ? "DRY RUN" : "SAVED")}] [TheIntroDB] Credits at {FormatTime(creditsStart)}, saved at {FormatTime(finalTimestamp)} for {ep.Name}");
                    introDbHits++;

                    if (Plugin.Instance != null)
                    {
                        Plugin.Progress.StartProcessingItem($"{seriesName} - S{ep.ParentIndexNumber:D2}E{ep.IndexNumber:D2} - {ep.Name}", "TheIntroDB");
                        Plugin.Progress.CompleteProcessingItem(true);

                        var offsetSeconds = _configuration.TimestampOffsetSeconds;
                        var successDetail = $"{FormatTime(creditsStart)} / {FormatTime(epDuration)} [TheIntroDB] - Community database timestamp";
                        if (offsetSeconds != 0)
                            successDetail += $" (offset: {offsetSeconds:+0;-0}s, final: {FormatTime(finalTimestamp)})";

                        Plugin.Progress.SuccessDetails[epKey] = successDetail;
                        Plugin.Progress.ConfidenceScores[epKey] = 1.0;
                        Plugin.Progress.EpisodeIds[epKey] = epId;
                        if (!string.IsNullOrEmpty(matchedRuleName))
                            Plugin.Progress.AppliedRules[epKey] = matchedRuleName;

                        if (Plugin.Instance.Configuration.EnableThumbnailGeneration)
                        {
                            try
                            {
                                var thumbnailPath = await GenerateThumbnail(ep, creditsStart, epKey);
                                if (!string.IsNullOrEmpty(thumbnailPath))
                                    Plugin.Progress.ThumbnailPaths[epKey] = thumbnailPath;
                            }
                            catch (Exception thumbEx)
                            {
                                _logger?.Debug($"Failed to generate thumbnail for {epKey}: {thumbEx.Message}");
                            }
                        }
                    }

                    if (!effectiveDryRun)
                    {
                        _processedEpisodes.TryAdd(epId, DateTime.UtcNow);
                        Plugin.TracerService?.MarkDetected(epId);
                        Plugin.PendingEpisodesService?.MarkProcessed(epId);
                    }
                }

                if (introDbHits > 0)
                    _logger?.Info($"[TheIntroDB] Found timestamps for {introDbHits} episode(s) in {seriesName} Season {seasonNumber}, {detectionNeeded.Count} episode(s) need detection");

                seasonEpisodes = detectionNeeded;

                if (seasonEpisodes.Count == 0)
                {
                    _logger?.Info($"[TheIntroDB] All episodes in {seriesName} Season {seasonNumber} resolved from database — skipping detection");
                    return;
                }
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
                if (Plugin.Instance != null)
                {
                    Plugin.Progress.CurrentItem = $"Audio fingerprinting: {seriesName} Season {seasonNumber} ({episodeData.Count} episodes)";
                }
                batchResults = await chromaprintMethod.DetectCreditsForSeason(episodeData, seriesId, seasonNumber, cancellationToken);
                _logger?.Info($"Batch detection found credits for {batchResults.Count} episodes");
            }
            else if (isAnime)
            {
                _logger?.Info($"Anime detected - skipping chromaprint batch processing, will use individual episode detection with BlackFrameDetection");
            }
            else if (effectiveConfig != null && effectiveConfig.DetectionMode == DetectionMode.BlackFrameOnly)
            {
                _logger?.Info($"BlackFrameOnly mode - processing episodes individually using black frame detection");
            }
            else if (effectiveConfig != null && (effectiveConfig.DetectionMode == DetectionMode.OcrWithHashFallback || effectiveConfig.DetectionMode == DetectionMode.OcrOnly))
            {
                _logger?.Info($"OCR as primary method (DetectionMode: {effectiveConfig.DetectionMode}) - processing episodes individually to try OCR first");
            }

            // Process each episode with the batch results
            var blackFrameParallelSessions = _configuration?.BlackFrameParallelSessions ?? 1;
            var shouldRunParallel = isAnime && blackFrameParallelSessions > 1 && seasonEpisodes.Count > 2;

            var isPaddleOcr = effectiveConfig?.OcrEngine == OcrEngine.PaddleOCR;
            var paddleConcurrentFiles = _configuration?.PaddleOcrConcurrentFiles ?? 2;
            var shouldRunPaddleParallel = !isAnime &&
                isPaddleOcr &&
                (_configuration?.PaddleOcrEnableConcurrentFiles ?? false) &&
                paddleConcurrentFiles > 1 &&
                seasonEpisodes.Count > 1 &&
                (effectiveConfig?.DetectionMode == DetectionMode.OcrOnly ||
                 effectiveConfig?.DetectionMode == DetectionMode.OcrWithHashFallback);

            if (shouldRunPaddleParallel)
            {
                const int seedCount = 1;
                var seedEpisodes = seasonEpisodes.Take(seedCount).ToList();
                var remainingEpisodes = seasonEpisodes.Skip(seedCount).ToList();

                _logger?.Info($"[PaddleOCR] Concurrent file mode: processing {seedCount} seed episode sequentially, then {remainingEpisodes.Count} in parallel (max {paddleConcurrentFiles} concurrent)");

                // Sequential seed phase — populates the episode-comparison timestamp cache
                foreach (var episode in seedEpisodes)
                {
                    if (cancellationToken.IsCancellationRequested || _cancellationRequested) break;
                    await ProcessSingleEpisodeInBatch(episode, seriesName, matchedRuleName, batchResults, chromaprintMethod, tempCoordinator, isManualRun, cancellationToken);
                }

                if (!cancellationToken.IsCancellationRequested && !_cancellationRequested)
                {
                    using var paddleSemaphore = new SemaphoreSlim(paddleConcurrentFiles, paddleConcurrentFiles);
                    var parallelTasks = remainingEpisodes.Select(async episode =>
                    {
                        if (cancellationToken.IsCancellationRequested || _cancellationRequested) return;
                        try
                        {
                            await paddleSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) { return; }
                        try
                        {
                            await ProcessSingleEpisodeInBatch(episode, seriesName, matchedRuleName, batchResults, chromaprintMethod, tempCoordinator, isManualRun, cancellationToken);
                        }
                        finally
                        {
                            paddleSemaphore.Release();
                        }
                    }).ToList();
                    await Task.WhenAll(parallelTasks).ConfigureAwait(false);
                }
            }
            else if (shouldRunParallel)
            {
                const int seedCount = 2;
                var seedEpisodes = seasonEpisodes.Take(seedCount).ToList();
                var remainingEpisodes = seasonEpisodes.Skip(seedCount).ToList();

                _logger?.Info($"[BlackFrame] Parallel mode: processing {seedCount} seed episodes sequentially, then {remainingEpisodes.Count} in parallel (max {blackFrameParallelSessions} concurrent)");

                // Sequential seed phase — builds the comparison cache
                foreach (var episode in seedEpisodes)
                {
                    if (cancellationToken.IsCancellationRequested || _cancellationRequested) break;
                    await ProcessSingleEpisodeInBatch(episode, seriesName, matchedRuleName, batchResults, chromaprintMethod, tempCoordinator, isManualRun, cancellationToken);
                }

                // Parallel phase — all remaining episodes benefit from the seeded cache
                if (!cancellationToken.IsCancellationRequested && !_cancellationRequested)
                {
                    using var bfSemaphore = new SemaphoreSlim(blackFrameParallelSessions, blackFrameParallelSessions);
                    var parallelTasks = remainingEpisodes.Select(async episode =>
                    {
                        if (cancellationToken.IsCancellationRequested || _cancellationRequested) return;
                        try
                        {
                            await bfSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) { return; }
                        try
                        {
                            await ProcessSingleEpisodeInBatch(episode, seriesName, matchedRuleName, batchResults, chromaprintMethod, tempCoordinator, isManualRun, cancellationToken);
                        }
                        finally
                        {
                            bfSemaphore.Release();
                        }
                    }).ToList();
                    await Task.WhenAll(parallelTasks).ConfigureAwait(false);
                }
            }
            else
            {
                // Sequential (existing behaviour)
                foreach (var episode in seasonEpisodes)
                {
                    if (cancellationToken.IsCancellationRequested || _cancellationRequested) break;
                    await ProcessSingleEpisodeInBatch(episode, seriesName, matchedRuleName, batchResults, chromaprintMethod, tempCoordinator, isManualRun, cancellationToken);
                }
            }



            tempCoordinator?.Dispose();
            
            _logger?.Info($"Batch processing complete for {seriesName} Season {seasonNumber}");
        }



        private static async Task ProcessSingleEpisodeInBatch(
            Episode episode,
            string seriesName,
            string? matchedRuleName,
            Dictionary<string, double>? batchResults,
            DetectionMethods.ChromaprintDetection? chromaprintMethod,
            DetectionCoordinator? tempCoordinator,
            bool isManualRun,
            CancellationToken cancellationToken)
        {
            var episodeId = episode.Id.ToString();

            if (_configuration != null && _configuration.SkipPreviouslyFailedEpisodes && !_configuration.IgnoreFailureMarkers)
            {
                var hasFailed = episode.ProviderIds?.TryGetValue("EmbyCredits.Fail", out var failValue) == true && failValue == "true";
                if (hasFailed)
                {
                    _logger?.Info($"Skipping {episode.Name} - previously failed detection (SkipPreviouslyFailedEpisodes is enabled)");
                    if (Plugin.Instance != null)
                    {
                        var episodeKey = $"{seriesName} S{episode.ParentIndexNumber:00}E{episode.IndexNumber:00}";
                        Plugin.Progress.SkipReasons[episodeKey] = "Previously failed detection";
                        Plugin.Progress.IncrementSkipped();
                    }
                    return;
                }
            }

            if (_configuration != null && _itemRepository != null)
            {
                var episodeKey = $"{seriesName} S{episode.ParentIndexNumber:00}E{episode.IndexNumber:00}";

                // Existing-marker skip: only applies to scheduled runs, never to manual runs.
                // Manual runs perform this filter at queue-build time in QueueSeriesManual/QueueEpisodeManual.
                if (!isManualRun && _configuration.ScheduledTaskOnlyProcessMissing)
                {
                    var chapters = _itemRepository.GetChapters(episode);
                    var hasCreditsMarker = chapters?.Any(c => GetMarkerType(c) == "CreditsStart") ?? false;
                    if (hasCreditsMarker)
                    {
                        _logger?.Info($"Skipping {episode.Name} - already has credits marker (ScheduledTaskOnlyProcessMissing is enabled)");
                        if (Plugin.Instance != null)
                        {
                            Plugin.Progress.SkipReasons[episodeKey] = "Already has credits marker";
                            Plugin.Progress.IncrementSkipped();
                        }
                        return;
                    }
                }

                // Embedded chapter import: use the setting that matches the run type.
                var useEmbedded = isManualRun
                    ? _configuration.UseEmbeddedChapterMarkersManual
                    : _configuration.UseEmbeddedChapterMarkersScheduled;
                if (useEmbedded && _chapterMarkerService != null)
                {
                    var imported = _chapterMarkerService.TryImportEmbeddedCreditChapter(episode);
                    if (imported)
                    {
                        _logger?.Info($"Skipping detection for {episode.Name} - credits marker imported from embedded chapter");
                        if (Plugin.Instance != null)
                        {
                            Plugin.Progress.SkipReasons[episodeKey] = "Imported from embedded chapter";
                            Plugin.Progress.IncrementSkipped();
                        }
                        return;
                    }
                }
            }

            try
            {
                if (Plugin.Instance != null)
                {
                    var episodeDisplayName = $"{seriesName} - S{episode.ParentIndexNumber:D2}E{episode.IndexNumber:D2} - {episode.Name}";
                    Plugin.Progress.StartProcessingItem(episodeDisplayName, "Detecting");
                }

                _logger?.Debug($"Processing episode {episode.Name}");

                double? batchCreditsStart = null;
                if (batchResults != null && batchResults.ContainsKey(episodeId))
                {
                    batchCreditsStart = batchResults[episodeId];
                }

                if (_episodeProcessor == null) return;

                // If this run was triggered by a single-episode request, only save markers
                // for the originally queued episode; treat all others as dry-run so they
                // contribute to Chromaprint batch analysis but produce no output.
                bool isTargetEpisode = _singleEpisodeTargets.Count == 0 || _singleEpisodeTargets.ContainsKey(episodeId);
                bool effectiveDryRun = _isDryRun || !isTargetEpisode;
                if (!isTargetEpisode)
                {
                    _logger?.Debug($"[SingleEpisodeTarget] Skipping marker save for {episode.Name} - not the originally requested episode");
                }

                var (success, creditsStart, failureReason, confidence, methodName, detectionReason) = await _episodeProcessor.ProcessEpisodeWithBatchResult(
                    episode, effectiveDryRun, batchCreditsStart, chromaprintMethod, tempCoordinator);

                if (success && creditsStart > 0)
                {
                    if (Plugin.Instance != null)
                    {
                        Plugin.Progress.CompleteProcessingItem(true);

                        var episodeKey = $"{seriesName} S{episode.ParentIndexNumber:00}E{episode.IndexNumber:00}";
                        var statusMessages = GetAndClearEpisodeStatusMessages(episodeId);
                        var duration = episode.RunTimeTicks.HasValue ? episode.RunTimeTicks.Value / (double)TimeSpan.TicksPerSecond : 0;
                        var offsetSeconds = _configuration?.TimestampOffsetSeconds ?? 0;
                        var finalTimestamp = creditsStart + offsetSeconds;
                        var successDetail = $"{FormatTime(creditsStart)} / {FormatTime(duration)}";

                        if (!string.IsNullOrEmpty(methodName))
                            successDetail += $" [{methodName}]";

                        if (!string.IsNullOrEmpty(detectionReason))
                            successDetail += $" - {detectionReason}";

                        if (offsetSeconds != 0)
                            successDetail += $" (offset: {offsetSeconds:+0;-0}s, final: {FormatTime(finalTimestamp)})";

                        if (statusMessages.Count > 0)
                            successDetail += " (" + string.Join(", ", statusMessages) + ")";

                        Plugin.Progress.SuccessDetails[episodeKey] = successDetail;
                        Plugin.Progress.ConfidenceScores[episodeKey] = confidence;
                        Plugin.Progress.EpisodeIds[episodeKey] = episodeId;
                        if (!string.IsNullOrEmpty(matchedRuleName))
                            Plugin.Progress.AppliedRules[episodeKey] = matchedRuleName;

                        if (Plugin.Instance.Configuration.EnableThumbnailGeneration)
                        {
                            try
                            {
                                var thumbnailPath = await GenerateThumbnail(episode, creditsStart, episodeKey);
                                if (!string.IsNullOrEmpty(thumbnailPath))
                                    Plugin.Progress.ThumbnailPaths[episodeKey] = thumbnailPath;
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
                        Plugin.TracerService?.MarkDetected(episodeId);
                        Plugin.PendingEpisodesService?.MarkProcessed(episodeId);

                        if (_configuration != null &&
                            _configuration.SkipDetectionIfFileUnchanged &&
                            !string.IsNullOrWhiteSpace(_configuration.BackupFolderPath) &&
                            Plugin.CreditsBackupService != null)
                        {
                            try
                            {
                                var fpTicks = (long)(creditsStart * TimeSpan.TicksPerSecond);
                                await Plugin.CreditsBackupService.UpsertEpisodeInSeriesBackup(
                                    episode, fpTicks, _configuration.BackupFolderPath).ConfigureAwait(false);
                            }
                            catch (Exception fpEx)
                            {
                                _logger?.Debug($"Failed to record file fingerprint for {episode.Name}: {fpEx.Message}");
                            }
                        }
                    }

                    _logger?.Debug($"Successfully detected credits at {FormatTime(creditsStart)} for {episode.Name}");
                }
                else
                {
                    if (Plugin.Instance != null)
                    {
                        Plugin.Progress.CompleteProcessingItem(false);
                        var episodeKey = $"{seriesName} S{episode.ParentIndexNumber:00}E{episode.IndexNumber:00}";
                        Plugin.Progress.FailureReasons[episodeKey] = failureReason;
                        if (!string.IsNullOrEmpty(matchedRuleName))
                            Plugin.Progress.AppliedRules[episodeKey] = matchedRuleName;
                    }

                    GetAndClearEpisodeStatusMessages(episodeId);

                    if (!_isDryRun &&
                        _configuration != null &&
                        _configuration.SkipDetectionIfFileUnchanged &&
                        !string.IsNullOrWhiteSpace(_configuration.BackupFolderPath) &&
                        Plugin.CreditsBackupService != null)
                    {
                        try
                        {
                            await Plugin.CreditsBackupService.UpsertEpisodeInSeriesBackup(
                                episode, 0L, _configuration.BackupFolderPath).ConfigureAwait(false);
                        }
                        catch (Exception fpEx)
                        {
                            _logger?.Debug($"Failed to record file fingerprint for {episode.Name}: {fpEx.Message}");
                        }
                    }

                    if (!_isDryRun)
                    {
                        Plugin.TracerService?.MarkFailed(episodeId, failureReason, episode);
                        Plugin.PendingEpisodesService?.MarkProcessed(episodeId);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.ErrorException($"Error processing episode {episode.Name}", ex);
                if (!_isDryRun)
                    Plugin.TracerService?.MarkFailed(episodeId, ex.Message, episode);
                Plugin.PendingEpisodesService?.MarkProcessed(episodeId);
                if (Plugin.Instance != null)
                {
                    Plugin.Progress.CompleteProcessingItem(false);
                    var episodeKey = $"{seriesName} S{episode.ParentIndexNumber:00}E{episode.IndexNumber:00}";
                    Plugin.Progress.FailureReasons[episodeKey] = ex.Message;
                    if (!string.IsNullOrEmpty(matchedRuleName))
                        Plugin.Progress.AppliedRules[episodeKey] = matchedRuleName;
                }
            }

            if (Plugin.Instance != null)
            {
                Plugin.Progress.CheckAndLimitDictionarySize();
                if (!_isProcessing && Plugin.Progress.ProcessedItems >= Plugin.Progress.TotalItems)
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

        public static async Task ProcessEpisode(Episode episode)
        {
            if (_episodeProcessor == null || _configuration == null)
                return;

            var episodeId = episode.Id.ToString();

            try
            {
                if (Plugin.Instance != null)
                {
                    var episodeDisplayName = $"{episode.Series?.Name} - S{episode.ParentIndexNumber:D2}E{episode.IndexNumber:D2} - {episode.Name}";
                    Plugin.Progress.StartProcessingItem(episodeDisplayName, "Detecting");
                }

                var (success, creditsStart, failureReason, confidence, methodName, detectionReason) = await _episodeProcessor.ProcessEpisode(
                    episode, _isDryRun, _isBatchMode, _batchDetectionCache);

                if (success && creditsStart > 0)
                {
                    if (Plugin.Instance != null)
                    {
                        Plugin.Progress.CompleteProcessingItem(true);

                        var series = episode.Series;
                        var episodeKey = series != null
                            ? $"{series.Name} S{episode.ParentIndexNumber:00}E{episode.IndexNumber:00}"
                            : episode.Name;
                        
                        var statusMessages = GetAndClearEpisodeStatusMessages(episodeId);
                        var duration = episode.RunTimeTicks.HasValue ? episode.RunTimeTicks.Value / (double)TimeSpan.TicksPerSecond : 0;
                        var offsetSeconds = _configuration?.TimestampOffsetSeconds ?? 0;
                        var finalTimestamp = creditsStart + offsetSeconds;
                        var successDetail = $"{FormatTime(creditsStart)} / {FormatTime(duration)}";
                        
                        if (!string.IsNullOrEmpty(methodName))
                        {
                            successDetail += $" [{methodName}]";
                        }
                        
                        if (!string.IsNullOrEmpty(detectionReason))
                        {
                            successDetail += $" - {detectionReason}";
                        }
                        
                        if (offsetSeconds != 0)
                        {
                            successDetail += $" (offset: {offsetSeconds:+0;-0}s, final: {FormatTime(finalTimestamp)})";
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
                        Plugin.Progress.CompleteProcessingItem(false);

                        var series = episode.Series;
                        var episodeKey = series != null
                            ? $"{series.Name} S{episode.ParentIndexNumber:00}E{episode.IndexNumber:00}"
                            : episode.Name;
                        Plugin.Progress.FailureReasons[episodeKey] = failureReason;
                    }
                    
                    GetAndClearEpisodeStatusMessages(episodeId);

                    if (!_isDryRun)
                    {
                        Plugin.TracerService?.MarkFailed(episodeId, failureReason, episode);
                        _processedEpisodes.TryAdd(episodeId, DateTime.UtcNow);
                    }
                }

                if (Plugin.Instance != null)
                {
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

                if (!_isDryRun)
                    Plugin.TracerService?.MarkFailed(episodeId, ex.Message, episode);

                if (Plugin.Instance != null)
                {
                    Plugin.Progress.CompleteProcessingItem(false);
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
                
                var thumbnailStartInfo = new ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                thumbnailStartInfo.ArgumentList.Add("-ss");
                thumbnailStartInfo.ArgumentList.Add(timestamp.ToString(CultureInfo.InvariantCulture));
                thumbnailStartInfo.ArgumentList.Add("-i");
                thumbnailStartInfo.ArgumentList.Add(videoPath);
                thumbnailStartInfo.ArgumentList.Add("-vframes");
                thumbnailStartInfo.ArgumentList.Add("1");
                thumbnailStartInfo.ArgumentList.Add("-vf");
                thumbnailStartInfo.ArgumentList.Add($"scale={width}:-1");
                thumbnailStartInfo.ArgumentList.Add("-q:v");
                thumbnailStartInfo.ArgumentList.Add(ffmpegQuality.ToString(CultureInfo.InvariantCulture));
                thumbnailStartInfo.ArgumentList.Add(thumbnailPath);
                thumbnailStartInfo.ArgumentList.Add("-y");

                LogDebug($"FFmpeg command: {ffmpegPath} -ss {timestamp} -i <path> -vframes 1 -vf scale={width}:-1 -q:v {ffmpegQuality}");

                using (var process = new Process { StartInfo = thumbnailStartInfo })
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

        private sealed class SeasonDispatchState : IDisposable
        {
            public readonly ConcurrentDictionary<Guid, byte> EpisodeIds = new ConcurrentDictionary<Guid, byte>();
            private Timer? _timer;
            private readonly object _lock = new object();
            private bool _disposed;

            public void ResetTimer(int delayMs, Action callback)
            {
                lock (_lock)
                {
                    if (_disposed) return;
                    if (_timer == null)
                        _timer = new Timer(_ => callback(), null, delayMs, Timeout.Infinite);
                    else
                        _timer.Change(delayMs, Timeout.Infinite);
                }
            }

            public void Dispose()
            {
                lock (_lock)
                {
                    _disposed = true;
                    _timer?.Dispose();
                    _timer = null;
                }
            }
        }
    }
}

