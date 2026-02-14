using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Querying;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EmbyCredits.Services.Utilities;

namespace EmbyCredits.Services
{
    public class EpisodeProcessor
    {
        private readonly ILogger _logger;
        private readonly ILibraryManager? _libraryManager;
        private DetectionCoordinator _detectionCoordinator;
        private readonly ChapterMarkerService _chapterMarkerService;
        private readonly DebugLogger _debugLogger;
        private readonly PluginConfiguration _configuration;
        private readonly CpuThrottler _cpuThrottler;
        private readonly RuleMatchingService _ruleMatchingService;
        private readonly VideoValidator _videoValidator;

        public EpisodeProcessor(
            ILogger logger,
            ILibraryManager? libraryManager,
            DetectionCoordinator detectionCoordinator,
            ChapterMarkerService chapterMarkerService,
            DebugLogger debugLogger,
            PluginConfiguration configuration)
        {
            _logger = logger;
            _libraryManager = libraryManager;
            _detectionCoordinator = detectionCoordinator;
            _chapterMarkerService = chapterMarkerService;
            _debugLogger = debugLogger;
            _configuration = configuration;
            _cpuThrottler = new CpuThrottler(configuration);
            _ruleMatchingService = new RuleMatchingService(logger, configuration);
            _videoValidator = new VideoValidator(logger, configuration);
        }

        public async Task<(bool success, double creditsStart, string failureReason, double confidence, string methodName, string detectionReason)> ProcessEpisode(
            Episode episode,
            bool isDryRun,
            bool isBatchMode,
            System.Collections.Concurrent.ConcurrentDictionary<string, List<(string method, double timestamp)>> batchDetectionCache)
        {
            var episodeId = episode.Id.ToString();
            var originalPriority = Thread.CurrentThread.Priority;
            bool priorityChanged = false;

            if (_configuration.LowerThreadPriority)
            {
                try
                {
                    Thread.CurrentThread.Priority = ThreadPriority.BelowNormal;
                    priorityChanged = true;
                    _debugLogger.LogDebug("Thread priority set to BelowNormal for reduced system impact");
                }
                catch (Exception ex)
                {
                    _debugLogger.LogWarn($"Failed to lower thread priority: {ex.Message}");
                }
            }

            _cpuThrottler.BeginWork();

            try
            {
                _debugLogger.LogInfo($"Processing episode: {episode.Name} (S{episode.ParentIndexNumber}E{episode.IndexNumber})");

                var normalizedPath = Utilities.FFmpegHelper.NormalizeFilePath(episode.Path);

                if (string.IsNullOrEmpty(normalizedPath))
                {
                    bool fileExists = false;
                    bool checkFailed = false;
                    try
                    {
                        fileExists = File.Exists(normalizedPath);
                    }
                    catch
                    {
                        checkFailed = true;
                    }

                    if (!checkFailed && !fileExists)
                    {
                        _debugLogger.LogWarn($"Episode file not found: {episode.Path}");
                        return (false, 0, "File not found", 0, string.Empty, string.Empty);
                    }
                }

                double duration = 0;
                if (episode.RunTimeTicks.HasValue && episode.RunTimeTicks.Value > 0)
                {
                    duration = episode.RunTimeTicks.Value / (double)TimeSpan.TicksPerSecond;
                }
                else
                {
                    duration = await GetVideoDuration(normalizedPath);
                }

                if (duration <= 0)
                {
                    _debugLogger.LogWarn($"Could not determine video duration for {episode.Name}");
                    return (false, 0, "Could not determine video duration", 0, string.Empty, string.Empty);
                }

                if (_configuration.EnableVideoValidation)
                {
                    var validationResult = await _videoValidator.ValidateVideo(normalizedPath, CancellationToken.None);
                    
                    if (!validationResult.isValid)
                    {
                        _debugLogger.LogWarn($"Video validation failed for {episode.Name}: {validationResult.errorMessage}");
                        return (false, 0, $"Video validation failed: {validationResult.errorMessage}", 0, string.Empty, string.Empty);
                    }
                }

                _debugLogger.LogInfo("Running credits detection");
                
                var series = episode.Series;
                var seriesId = series?.Id.ToString();
                var seasonNumber = episode.ParentIndexNumber;
                var episodeNumber = episode.IndexNumber;
                var effectiveConfig = _ruleMatchingService.GetEffectiveConfiguration(episode);
                
                if (effectiveConfig != _configuration)
                {
                    _debugLogger.LogInfo("Using rule-based configuration for this episode");
                    _detectionCoordinator?.Dispose();
                    _detectionCoordinator = new DetectionCoordinator(_logger, effectiveConfig);
                }
                
                var result = !string.IsNullOrEmpty(seriesId) 
                    ? await _detectionCoordinator.DetectCreditsWithContext(normalizedPath, duration, episodeId, seriesId, seasonNumber, episodeNumber)
                    : await _detectionCoordinator.DetectCredits(normalizedPath, duration, episodeId);
                double creditsStart = result.timestamp;
                string failureReason = result.failureReason;
                double confidence = result.confidence;
                string methodName = result.methodName;
                string detectionReason = result.detectionReason;

                if (creditsStart > 0)
                {
                    if (creditsStart >= duration)
                    {
                        _debugLogger.LogWarn($"✗ Detected timestamp ({FormatTime(creditsStart)}) exceeds video duration ({FormatTime(duration)}) for {episode.Name}");
                        return (false, 0, $"Detected timestamp exceeds duration: {creditsStart:F1}s >= {duration:F1}s", 0, string.Empty, string.Empty);
                    }

                    if (!isDryRun)
                    {
                        _chapterMarkerService.SaveCreditsMarker(episode, creditsStart);
                    }
                    _debugLogger.LogInfo($"✓ [{(isDryRun ? "DRY RUN" : "SAVED")}] Credits detected at {FormatTime(creditsStart)} for {episode.Name} (confidence: {confidence:F2})");

                    return (true, creditsStart, string.Empty, confidence, methodName, detectionReason);
                }
                else
                {
                    _debugLogger.LogWarn($"✗ No clear credits detected for {episode.Name}");
                    return (false, 0, failureReason, 0, string.Empty, string.Empty);
                }
            }
            catch (Exception ex)
            {
                _logger.ErrorException($"Error processing episode {episode.Name}", ex);
                return (false, 0, $"Exception: {ex.Message}", 0, string.Empty, string.Empty);
            }
            finally
            {
                await _cpuThrottler.EndWork().ConfigureAwait(false);

                if (priorityChanged)
                {
                    try
                    {
                        Thread.CurrentThread.Priority = originalPriority;
                    }
                    catch (Exception ex)
                    {
                        _debugLogger.LogWarn($"Failed to restore thread priority: {ex.Message}");
                    }
                }

                if (_configuration?.DelayBetweenEpisodesMs > 0)
                {
                    _debugLogger.LogDebug($"Applying {_configuration.DelayBetweenEpisodesMs}ms delay before next episode");
                    await Task.Delay(_configuration.DelayBetweenEpisodesMs);
                }
                
                GC.Collect(2, GCCollectionMode.Optimized, false);
            }
        }

        public async Task<(bool success, double creditsStart, string failureReason, double confidence, string methodName, string detectionReason)> ProcessEpisodeWithBatchResult(
            Episode episode,
            bool isDryRun,
            double? batchDetectedTime,
            DetectionMethods.ChromaprintDetection? chromaprintMethod = null,
            DetectionCoordinator? overrideCoordinator = null)
        {
            var episodeId = episode.Id.ToString();
            var originalPriority = Thread.CurrentThread.Priority;
            bool priorityChanged = false;

            if (_configuration.LowerThreadPriority)
            {
                try
                {
                    Thread.CurrentThread.Priority = ThreadPriority.BelowNormal;
                    priorityChanged = true;
                    _debugLogger.LogDebug("Thread priority set to BelowNormal for reduced system impact");
                }
                catch (Exception ex)
                {
                    _debugLogger.LogWarn($"Failed to lower thread priority: {ex.Message}");
                }
            }

            _cpuThrottler.BeginWork();

            try
            {
                _debugLogger.LogInfo($"Processing episode with batch result: {episode.Name} (S{episode.ParentIndexNumber}E{episode.IndexNumber})");

                if (batchDetectedTime.HasValue && batchDetectedTime.Value > 0)
                {
                    double creditsStart = batchDetectedTime.Value;
                    
                    if (!isDryRun)
                    {
                        var normalizedPath = Utilities.FFmpegHelper.NormalizeFilePath(episode.Path);
                        double duration = 0;
                        if (episode.RunTimeTicks.HasValue && episode.RunTimeTicks.Value > 0)
                        {
                            duration = episode.RunTimeTicks.Value / (double)TimeSpan.TicksPerSecond;
                        }

                        _debugLogger.LogInfo($"Adding chapter marker at {FormatTime(creditsStart)}");
                        _chapterMarkerService.SaveCreditsMarker(episode, creditsStart);
                        _debugLogger.LogInfo($"Successfully added chapter marker");
                    }
                    else
                    {
                        _debugLogger.LogInfo($"Dry run - would add chapter marker at {FormatTime(creditsStart)}");
                    }

                    // Get actual confidence from chromaprint batch processing
                    double confidence = chromaprintMethod?.GetBatchConfidence(episode.Id.ToString()) ?? Plugin.Instance?.Configuration.ChromaprintMinConfidence ?? 0.85;
                    return (true, creditsStart, string.Empty, confidence, "Chromaprint Audio Fingerprint Detection", "Batch comparison");
                }
                else
                {
                    // No batch result - fall back to regular detection methods (OCR, etc.)
                    _debugLogger.LogDebug($"No batch result for {episode.Name}, trying other detection methods");
                    
                    var normalizedPath = Utilities.FFmpegHelper.NormalizeFilePath(episode.Path);
                    if (string.IsNullOrEmpty(normalizedPath))
                    {
                        _debugLogger.LogWarn($"Path normalization failed for: {episode.Path}");
                        return (false, 0, "Path normalization failed", 0, string.Empty, string.Empty);
                    }

                    double duration = 0;
                    if (episode.RunTimeTicks.HasValue && episode.RunTimeTicks.Value > 0)
                    {
                        duration = episode.RunTimeTicks.Value / (double)TimeSpan.TicksPerSecond;
                    }
                    else
                    {
                        _debugLogger.LogWarn($"No valid duration for episode {episode.Name}");
                        return (false, 0, "No valid duration", 0, string.Empty, string.Empty);
                    }

                    if (_configuration.EnableVideoValidation)
                    {
                        _debugLogger.LogDebug("Validating video file integrity");
                        var validationResult = await _videoValidator.ValidateVideo(normalizedPath, CancellationToken.None);
                        
                        if (!validationResult.isValid)
                        {
                            _debugLogger.LogWarn($"Video validation failed for {episode.Name}: {validationResult.errorMessage}");
                            return (false, 0, $"Video validation failed: {validationResult.errorMessage}", 0, string.Empty, string.Empty);
                        }
                        
                        _debugLogger.LogDebug("Video validation passed");
                    }

                    // Try detection methods in priority order
                    var coordinatorToUse = overrideCoordinator ?? _detectionCoordinator;
                    var detectionResult = await coordinatorToUse.DetectCreditsWithContext(
                        normalizedPath,
                        duration,
                        episode.Id.ToString(),
                        episode.Series?.Id.ToString() ?? string.Empty,
                        episode.ParentIndexNumber,
                        episode.IndexNumber);

                    if (detectionResult.timestamp > 0)
                    {
                        if (!isDryRun)
                        {
                            _debugLogger.LogInfo($"Adding chapter marker at {FormatTime(detectionResult.timestamp)}");
                            _chapterMarkerService.SaveCreditsMarker(episode, detectionResult.timestamp);
                            _debugLogger.LogInfo($"Successfully added chapter marker");
                        }
                        else
                        {
                            _debugLogger.LogInfo($"Dry run - would add chapter marker at {FormatTime(detectionResult.timestamp)}");
                        }

                        return (true, detectionResult.timestamp, string.Empty, detectionResult.confidence, detectionResult.methodName, detectionResult.detectionReason);
                    }
                    else
                    {
                        _debugLogger.LogWarn($"Failed to detect credits for {episode.Name}: {detectionResult.failureReason}");
                        return (false, 0, detectionResult.failureReason, 0, string.Empty, string.Empty);
                    }
                }
            }
            catch (Exception ex)
            {
                _debugLogger.LogError($"Error processing episode: {ex.Message}", ex);
                return (false, 0, $"Error: {ex.Message}", 0, string.Empty, string.Empty);
            }
            finally
            {
                await _cpuThrottler.EndWork().ConfigureAwait(false);

                if (priorityChanged)
                {
                    try
                    {
                        Thread.CurrentThread.Priority = originalPriority;
                    }
                    catch (Exception ex)
                    {
                        _debugLogger.LogWarn($"Failed to restore thread priority: {ex.Message}");
                    }
                }

                if (_configuration?.DelayBetweenEpisodesMs > 0)
                {
                    _debugLogger.LogDebug($"Applying {_configuration.DelayBetweenEpisodesMs}ms delay before next episode");
                    await Task.Delay(_configuration.DelayBetweenEpisodesMs);
                }
            }
        }

        public List<DetectionMethods.IDetectionMethod> GetDetectionMethods()
        {
            return _detectionCoordinator.GetAllDetectionMethods();
        }

        private async Task<double> GetVideoDuration(string filePath)
        {
            try
            {
                _debugLogger.LogDebug($"Getting video duration for: {filePath}");

                var ffprobeInputPath = Utilities.FFmpegHelper.GetInputArgument(filePath);

                using (var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = Utilities.FFmpegHelper.GetFfprobePath(),
                        Arguments = $"-v error -show_entries format=duration -of default=noprint_wrappers=1:nokey=1 {ffprobeInputPath}",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    }
                })
                {
                    process.Start();
                    CpuThrottler.SetProcessPriority(process, _configuration);
                    
                    using (var outputReader = process.StandardOutput)
                    {
                        var output = await outputReader.ReadToEndAsync();
                        await process.WaitForExitAsync();

                        if (double.TryParse(output.Trim(), System.Globalization.NumberStyles.Any, 
                            System.Globalization.CultureInfo.InvariantCulture, out var duration))
                        {
                            _debugLogger.LogDebug($"Duration result: {duration} seconds");
                            return duration;
                        }

                        _debugLogger.LogError("Failed to parse duration output", null);
                        return 0;
                    }
                }
            }
            catch (Exception ex)
            {
                _debugLogger.LogError($"Error getting video duration: {ex.Message}", ex);
                return 0;
            }
        }

        private string FormatTime(double seconds)
        {
            var ts = TimeSpan.FromSeconds(seconds);
            return $"{(int)ts.TotalMinutes}:{ts.Seconds:D2}";
        }
    }
}
