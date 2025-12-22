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
        private static Timer? _processingTimer;
        private static ConcurrentQueue<Episode> _processingQueue = new ConcurrentQueue<Episode>();
        private static SemaphoreSlim _processingSemaphore = new SemaphoreSlim(1, 1);
        private static bool _isProcessing = false;
        private static string? _cacheDirectory;
        private static string? _audioCacheDirectory;
        private static bool _cancellationRequested = false;
        
        private static DetectionCoordinator? _detectionCoordinator;
        
        private static readonly ConcurrentDictionary<string, List<(string method, double timestamp)>> _batchDetectionCache = new ConcurrentDictionary<string, List<(string method, double timestamp)>>();
        private static bool _isBatchMode = false;

        private static void LogInfo(string message)
        {
            if (_configuration?.EnableDetailedLogging == true)
                _logger?.Info(message);
        }

        private static void LogDebug(string message)
        {
            if (_configuration?.EnableDetailedLogging == true)
                _logger?.Debug(message);
        }

        private static void LogWarn(string message)
        {
            if (_configuration?.EnableDetailedLogging == true)
                _logger?.Warn(message);
        }

        private static void LogError(string message, Exception? ex = null)
        {
            if (ex != null)
                _logger?.ErrorException(message, ex);
            else
                _logger?.Error(message);
        }

        public static void Start(ILogger logger, IApplicationPaths appPaths, PluginConfiguration configuration)
        {
            _logger = logger;
            _appPaths = appPaths;
            _configuration = configuration;
            _isRunning = true;

            _cacheDirectory = Path.Combine(_appPaths.CachePath, "EmbyCredits", "Fingerprints");
            if (!Directory.Exists(_cacheDirectory))
            {
                Directory.CreateDirectory(_cacheDirectory);
            }

            _audioCacheDirectory = Path.Combine(_appPaths.CachePath, "EmbyCredits", "AudioFingerprints");
            if (!Directory.Exists(_audioCacheDirectory))
            {
                Directory.CreateDirectory(_audioCacheDirectory);
            }

            _detectionCoordinator = new DetectionCoordinator(_logger, _configuration);

            _logger.Info("Credits Detection Service started");
            LogInfo($"Fingerprint cache directory: {_cacheDirectory}");
            LogInfo($"Audio fingerprint cache directory: {_audioCacheDirectory}");

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
            
            if (_logger != null)
            {
                _detectionCoordinator = new DetectionCoordinator(_logger, configuration);
            }
        }

        public static void ClearCache()
        {
            try
            {
                int deletedCount = 0;

                if (!string.IsNullOrEmpty(_cacheDirectory) && Directory.Exists(_cacheDirectory))
                {
                    var files = Directory.GetFiles(_cacheDirectory, "*.json");
                    foreach (var file in files)
                    {
                        try
                        {
                            File.Delete(file);
                            deletedCount++;
                        }
                        catch (Exception ex)
                        {
                            LogWarn($"Failed to delete cache file {file}: {ex.Message}");
                        }
                    }
                }

                if (!string.IsNullOrEmpty(_audioCacheDirectory) && Directory.Exists(_audioCacheDirectory))
                {
                    var files = Directory.GetFiles(_audioCacheDirectory, "*.json");
                    foreach (var file in files)
                    {
                        try
                        {
                            File.Delete(file);
                            deletedCount++;
                        }
                        catch (Exception ex)
                        {
                            LogWarn($"Failed to delete audio cache file {file}: {ex.Message}");
                        }
                    }
                }

                _detectionCoordinator?.ClearCache();

                if (deletedCount > 0)
                {
                    _logger?.Info($"Cleared {deletedCount} fingerprint cache files for fresh detection");
                }
            }
            catch (Exception ex)
            {
                LogError("Error clearing cache", ex);
            }
        }

        public static void Stop()
        {
            _isRunning = false;
            _processingTimer?.Dispose();
            _isProcessing = false;
            
            if (_libraryManager != null)
            {
                _libraryManager.ItemAdded -= OnItemAdded;
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

        private static string GetFfmpegPath()
        {
            if (_ffmpegManager?.FfmpegConfiguration != null)
            {
                var config = _ffmpegManager.FfmpegConfiguration;
                if (!string.IsNullOrEmpty(config.EncoderPath) && File.Exists(config.EncoderPath))
                {
                    LogInfo($"Using Emby's ffmpeg from IFfmpegManager: {config.EncoderPath}");
                    return config.EncoderPath;
                }
            }

            LogWarn("IFfmpegManager not available or EncoderPath not set, falling back to system PATH");
            return "ffmpeg";
        }

        private static string GetFfprobePath()
        {
            if (_ffmpegManager?.FfmpegConfiguration != null)
            {
                var config = _ffmpegManager.FfmpegConfiguration;
                if (!string.IsNullOrEmpty(config.ProbePath) && File.Exists(config.ProbePath))
                {
                    LogDebug($"Using Emby's ffprobe from IFfmpegManager: {config.ProbePath}");
                    return config.ProbePath;
                }
            }

            var ffmpegPath = GetFfmpegPath();

            if (ffmpegPath.Contains(Path.DirectorySeparatorChar))
            {
                var directory = Path.GetDirectoryName(ffmpegPath);
                var ffprobeExe = Path.Combine(directory ?? "", "ffprobe.exe");
                var ffprobe = Path.Combine(directory ?? "", "ffprobe");

                if (File.Exists(ffprobeExe))
                    return ffprobeExe;
                if (File.Exists(ffprobe))
                    return ffprobe;
            }

            return "ffprobe";
        }

        public static void QueueEpisode(Episode episode)
        {
            if (!_processedEpisodes.ContainsKey(episode.Id.ToString()))
            {
                _processingQueue.Enqueue(episode);
                LogDebug($"Queued episode: {episode.Name}");

                if (!_isProcessing && Plugin.Instance != null)
                {
                    Plugin.Progress.Reset();
                    Plugin.Progress.IsRunning = true;
                    Plugin.Progress.TotalItems = 1;
                    Plugin.Progress.StartTime = DateTime.Now;
                }
                else if (_isProcessing && Plugin.Instance != null)
                {
                    Plugin.Progress.TotalItems++;
                }

                if (!_isProcessing)
                {
                    Task.Run(ProcessQueue);
                }
            }
        }

        public static void QueueSeries(List<Episode> episodes)
        {
            ClearCache();
            
            _batchDetectionCache.Clear();

            _cancellationRequested = false;

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

            _isBatchMode = queuedCount >= 3 && _configuration?.UseEpisodeComparison == true && _configuration?.UseCorrelationScoring == true;
            
            if (_isBatchMode)
            {
                LogInfo($"Batch mode enabled: Pre-computing detections for {queuedCount} episodes");
                Task.Run(() => PreComputeBatchDetections(episodes.ToList()));
            }
            else if (!_isProcessing)
            {
                Task.Run(ProcessQueue);
            }
        }

        public static void CancelProcessing()
        {
            _cancellationRequested = true;
            
            while (_processingQueue.TryDequeue(out _)) { }
            
            LogInfo("Cancellation requested for credits detection - queue cleared");

            if (Plugin.Instance != null)
            {
                Plugin.Progress.CurrentItem = "Cancelling...";
            }
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
                        Plugin.Progress.CurrentItemProgress = (int)(progress * 50); // Use first 50% for pre-computation
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
            if (_itemRepository == null)
                return new System.Collections.Generic.List<object>();

            var result = new System.Collections.Generic.List<object>();

            foreach (var episode in episodes)
            {
                try
                {
                    var chapters = _itemRepository.GetChapters(episode)?.ToList() ?? new System.Collections.Generic.List<ChapterInfo>();
                    
                    var creditsMarkers = chapters.Where(c =>
                    {
                        var markerType = GetMarkerType(c);
                        return markerType == "CreditsStart" || 
                               markerType == "Credits" ||
                               (c.Name != null && c.Name.ToLowerInvariant().Contains("credit"));
                    }).Select(c => new
                    {
                        Name = c.Name,
                        StartPositionTicks = c.StartPositionTicks,
                        StartTime = FormatTime(c.StartPositionTicks / TimeSpan.TicksPerSecond),
                        MarkerType = GetMarkerType(c)
                    }).ToList();

                    result.Add(new
                    {
                        EpisodeId = episode.Id.ToString(),
                        EpisodeName = episode.Name,
                        Season = episode.ParentIndexNumber,
                        Episode = episode.IndexNumber,
                        SeasonEpisode = $"S{episode.ParentIndexNumber:D2}E{episode.IndexNumber:D2}",
                        Duration = episode.RunTimeTicks.HasValue ? FormatTime(episode.RunTimeTicks.Value / TimeSpan.TicksPerSecond) : "Unknown",
                        HasCreditsMarker = creditsMarkers.Count > 0,
                        Markers = creditsMarkers,
                        AllChapters = chapters.Select(c => new
                        {
                            Name = c.Name,
                            StartTime = FormatTime(c.StartPositionTicks / TimeSpan.TicksPerSecond),
                            MarkerType = GetMarkerType(c)
                        }).ToList()
                    });
                }
                catch (Exception ex)
                {
                    _logger?.Debug($"Error getting markers for episode {episode.Name}: {ex.Message}");
                }
            }

            return result;
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
                    }
                    else if (_processingQueue.IsEmpty)
                    {
                        Plugin.Progress.IsRunning = false;
                        Plugin.Progress.EndTime = DateTime.Now;
                        Plugin.Progress.CurrentItem = "Complete";
                        Plugin.Progress.CurrentItemProgress = 100;
                        LogInfo($"Processing complete: {Plugin.Progress.SuccessfulItems} succeeded, {Plugin.Progress.FailedItems} failed");
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

                foreach (var episode in episodes)
                {
                    if (!_processedEpisodes.ContainsKey(episode.Id.ToString()))
                    {
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
            if (_configuration == null || _logger == null || _detectionCoordinator == null)
                return;

            var episodeId = episode.Id.ToString();

            var originalPriority = Thread.CurrentThread.Priority;
            if (_configuration.LowerThreadPriority)
            {
                Thread.CurrentThread.Priority = ThreadPriority.BelowNormal;
                LogDebug("Thread priority set to BelowNormal for reduced system impact");
            }

            try
            {
                if (Plugin.Instance != null)
                {
                    Plugin.Progress.CurrentItem = $"{episode.Series?.Name} - S{episode.ParentIndexNumber:D2}E{episode.IndexNumber:D2} - {episode.Name}";
                    Plugin.Progress.CurrentItemProgress = 0;
                }

                _logger.Info($"Processing episode: {episode.Name} (S{episode.ParentIndexNumber}E{episode.IndexNumber})");

                if (string.IsNullOrEmpty(episode.Path) || !File.Exists(episode.Path))
                {
                    _logger.Warn($"Episode file not found: {episode.Path}");
                    return;
                }

                var duration = await GetVideoDuration(episode.Path);
                if (duration <= 0)
                {
                    _logger.Warn($"Could not determine video duration for {episode.Name}");
                    return;
                }

                _logger.Debug($"Video duration: {FormatTime(duration)}");

                if (Plugin.Instance != null)
                {
                    Plugin.Progress.CurrentItemProgress = 10;
                }

                double creditsStart = 0;
                string failureReason = string.Empty;

                if (_configuration.UseEpisodeComparison && _libraryManager != null && episode.Series != null)
                {
                    if (_isBatchMode)
                    {
                        _logger.Info("Using batch mode with cross-episode analysis");
                        
                        var comparisonEpisodeIds = _batchDetectionCache.Keys
                            .Where(id => id != episodeId)
                            .ToList();

                        if (comparisonEpisodeIds.Count > 0)
                        {
                            creditsStart = _detectionCoordinator.AnalyzeBatchDetectionResults(episodeId, comparisonEpisodeIds);
                        }
                    }
                    else
                    {
                        var comparisonEpisodes = _libraryManager.GetItemList(new InternalItemsQuery
                        {
                            IncludeItemTypes = new[] { "Episode" },
                            IsVirtualItem = false,
                            HasPath = true
                        }).OfType<Episode>()
                        .Where(e => e.SeriesId == episode.SeriesId && 
                                   e.ParentIndexNumber == episode.ParentIndexNumber &&
                                   e.Id != episode.Id && 
                                   !string.IsNullOrEmpty(e.Path) && 
                                   File.Exists(e.Path))
                        .Take(_configuration.MinimumEpisodesToCompare)
                        .ToList();

                        if (comparisonEpisodes.Count >= 2)
                        {
                            _logger.Info($"Using cross-episode comparison with {comparisonEpisodes.Count} episodes");
                            var result = await _detectionCoordinator.DetectCreditsWithComparison(
                                episode, duration, comparisonEpisodes);
                            creditsStart = result.timestamp;
                            failureReason = result.failureReason;
                        }
                        else
                        {
                            _logger.Info("Not enough comparison episodes, using single-episode detection");
                            var result = await _detectionCoordinator.DetectCredits(episode.Path, duration, episodeId);
                            creditsStart = result.timestamp;
                            failureReason = result.failureReason;
                        }
                    }
                }
                else
                {
                    _logger.Info("Using single-episode detection");
                    var result = await _detectionCoordinator.DetectCredits(episode.Path, duration, episodeId);
                    creditsStart = result.timestamp;
                    failureReason = result.failureReason;
                }

                if (Plugin.Instance != null)
                {
                    Plugin.Progress.CurrentItemProgress = 95;
                }

                if (creditsStart > 0)
                {
                    SaveCreditsChapterMarker(episode, creditsStart);
                    _logger.Info($"Credits detected at {FormatTime(creditsStart)} for {episode.Name}");

                    if (Plugin.Instance != null)
                    {
                        Plugin.Progress.SuccessfulItems++;
                        
                        var series = episode.Series;
                        var episodeKey = series != null
                            ? $"{series.Name} S{episode.ParentIndexNumber:00}E{episode.IndexNumber:00}"
                            : episode.Name;
                        Plugin.Progress.SuccessDetails[episodeKey] = FormatTime(creditsStart);
                    }
                    
                    _processedEpisodes.TryAdd(episodeId, DateTime.UtcNow);
                }
                else
                {
                    _logger.Info($"No clear credits detected for {episode.Name}");

                    if (Plugin.Instance != null)
                    {
                        Plugin.Progress.FailedItems++;
                        
                        var series = episode.Series;
                        var episodeKey = series != null
                            ? $"{series.Name} S{episode.ParentIndexNumber:00}E{episode.IndexNumber:00}"
                            : episode.Name;
                        Plugin.Progress.FailureReasons[episodeKey] = failureReason;
                    }
                    
                    _processedEpisodes.TryAdd(episodeId, DateTime.UtcNow);
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

                if (_configuration.DelayBetweenEpisodesMs > 0)
                {
                    LogDebug($"Applying {_configuration.DelayBetweenEpisodesMs}ms delay before next episode");
                    await Task.Delay(_configuration.DelayBetweenEpisodesMs);
                }

                if (_configuration.CpuUsageLimit < 100)
                {
                    var throttleDelayMs = CalculateThrottleDelay();
                    if (throttleDelayMs > 0)
                    {
                        LogDebug($"CPU throttling: {throttleDelayMs}ms delay");
                        await Task.Delay(throttleDelayMs);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.ErrorException($"Error processing episode {episode.Name}", ex);

                if (Plugin.Instance != null)
                {
                    Plugin.Progress.FailedItems++;
                    Plugin.Progress.ProcessedItems++;
                }
            }
            finally
            {
                if (_configuration.LowerThreadPriority)
                {
                    Thread.CurrentThread.Priority = originalPriority;
                }
            }
        }

        private static void SaveCreditsChapterMarker(Episode episode, double creditsStartSeconds)
        {
            if (_itemRepository == null)
            {
                LogDebug("ItemRepository not available, skipping chapter marker");
                return;
            }

            try
            {
                var chapters = _itemRepository.GetChapters(episode)?.ToList() ?? new List<ChapterInfo>();

                var existingCreditsMarkers = chapters.Where(c =>
                {
                    var markerType = GetMarkerType(c);
                    if (markerType == "CreditsStart" || markerType == "Credits")
                        return true;
                    
                    if (c.Name != null)
                    {
                        var nameLower = c.Name.ToLowerInvariant();
                        if (nameLower.Contains("credit") || 
                            nameLower.Contains("end title") ||
                            nameLower.Contains("ending") ||
                            nameLower == "credits")
                            return true;
                    }
                    
                    var duration = episode.RunTimeTicks ?? 0;
                    if (duration > 0)
                    {
                        var positionRatio = (double)c.StartPositionTicks / duration;
                        if (positionRatio >= 0.80 && (string.IsNullOrEmpty(c.Name) || c.Name.Length < 3))
                            return true;
                    }
                    
                    return false;
                }).ToList();

                if (existingCreditsMarkers.Count > 0)
                {
                    foreach (var marker in existingCreditsMarkers)
                    {
                        chapters.Remove(marker);
                    }
                    LogInfo($"Removed {existingCreditsMarkers.Count} existing credits marker(s)");
                }

                var creditsMarker = new ChapterInfo
                {
                    Name = "Credits",
                    StartPositionTicks = (long)(creditsStartSeconds * TimeSpan.TicksPerSecond)
                };

                var markerTypeSet = SetMarkerType(creditsMarker, MarkerType.CreditsStart);
                LogInfo($"MarkerType.CreditsStart set: {markerTypeSet}");

                if (markerTypeSet)
                {
                    var verifyType = GetMarkerType(creditsMarker);
                    LogInfo($"Verified MarkerType value: {verifyType}");
                }

                chapters.Add(creditsMarker);
                LogInfo($"Added new CreditsStart marker at {FormatTime(creditsStartSeconds)}");

                _itemRepository.SaveChapters(episode.InternalId, chapters);
                LogInfo($"Saved chapter markers for {episode.Name}");
            }
            catch (Exception ex)
            {
                LogError($"Error saving credits chapter marker for {episode.Name}", ex);
            }
        }

        private static string? GetMarkerType(ChapterInfo chapter)
        {
            try
            {
                var markerTypeProp = chapter.GetType().GetProperty("MarkerType");
                if (markerTypeProp != null && markerTypeProp.CanRead)
                {
                    var value = markerTypeProp.GetValue(chapter);
                    return value?.ToString();
                }
            }
            catch (Exception ex)
            {
                LogDebug($"Error reading MarkerType property: {ex.Message}");
            }
            return null;
        }

        private static bool SetMarkerType(ChapterInfo chapter, MarkerType markerType)
        {
            try
            {
                var markerTypeProp = chapter.GetType().GetProperty("MarkerType");
                if (markerTypeProp != null && markerTypeProp.CanWrite)
                {
                    markerTypeProp.SetValue(chapter, markerType);
                    return true;
                }
            }
            catch (Exception ex)
            {
                LogDebug($"Error setting MarkerType property: {ex.Message}");
            }
            return false;
        }

        private static async Task<double> GetVideoDuration(string videoPath)
        {
            if (_configuration == null)
                return 0;

            try
            {
                var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = GetFfprobePath(),
                        Arguments = $"-v error -show_entries format=duration -of default=noprint_wrappers=1:nokey=1 \"{videoPath}\"",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    }
                };

                process.Start();
                var output = await process.StandardOutput.ReadToEndAsync();
                await process.WaitForExitAsync();

                if (double.TryParse(output.Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out var duration))
                {
                    return duration;
                }
            }
            catch (Exception ex)
            {
                LogDebug($"Error getting video duration: {ex.Message}");
            }

            return 0;
        }

        private static string FormatTime(double seconds)
        {
            var time = TimeSpan.FromSeconds(seconds);
            return $"{(int)time.TotalHours:D2}:{time.Minutes:D2}:{time.Seconds:D2}";
        }

        private static int CalculateThrottleDelay()
        {
            if (_configuration == null || _configuration.CpuUsageLimit >= 100)
                return 0;

            var throttlePercentage = 100 - _configuration.CpuUsageLimit;
            var baseDelayMs = 100; // Base delay of 100ms
            var throttleDelayMs = (int)(baseDelayMs * (throttlePercentage / 100.0));
            
            return throttleDelayMs;
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
    }
}

