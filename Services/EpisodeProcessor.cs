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
        private readonly TheIntroDbService? _theIntroDbService;

        public EpisodeProcessor(
            ILogger logger,
            ILibraryManager? libraryManager,
            DetectionCoordinator detectionCoordinator,
            ChapterMarkerService chapterMarkerService,
            DebugLogger debugLogger,
            PluginConfiguration configuration,
            RuleMatchingService ruleMatchingService,
            TheIntroDbService? theIntroDbService = null)
        {
            _logger = logger;
            _libraryManager = libraryManager;
            _detectionCoordinator = detectionCoordinator;
            _chapterMarkerService = chapterMarkerService;
            _debugLogger = debugLogger;
            _configuration = configuration;
            _cpuThrottler = new CpuThrottler(configuration, logger);
            _ruleMatchingService = ruleMatchingService;
            _videoValidator = new VideoValidator(logger, configuration);
            _theIntroDbService = theIntroDbService;
        }

        public async Task<(bool success, double creditsStart, string failureReason, double confidence, string methodName, string detectionReason)> ProcessEpisode(
            Episode episode,
            bool isDryRun,
            bool isBatchMode,
            System.Collections.Concurrent.ConcurrentDictionary<string, List<(string method, double timestamp)>> batchDetectionCache)
        {
            var episodeId = episode.Id.ToString();

            _cpuThrottler.BeginWork();

            DetectionCoordinator? localCoordinator = null;
            try
            {
                _debugLogger.LogInfo($"Processing episode: {episode.Name} (S{episode.ParentIndexNumber}E{episode.IndexNumber})");

                var normalizedPath = Utilities.FFmpegHelper.NormalizeFilePath(episode.Path);

                if (string.IsNullOrEmpty(normalizedPath))
                {
                    _debugLogger.LogWarn($"Episode file path is empty: {episode.Path}");
                    return (false, 0, "File not found", 0, string.Empty, string.Empty);
                }

                try
                {
                    if (!File.Exists(normalizedPath))
                    {
                        _debugLogger.LogWarn($"Episode file not found: {episode.Path}");
                        return (false, 0, "File not found", 0, string.Empty, string.Empty);
                    }
                }
                catch (Exception ex)
                {
                    _debugLogger.LogWarn($"Could not check file existence for {episode.Path}: {ex.Message}");
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
                        if (_configuration.SkipPreviouslyFailedEpisodes)
                        {
                            if (episode.ProviderIds == null)
                            {
                                episode.ProviderIds = new MediaBrowser.Model.Entities.ProviderIdDictionary();
                            }
                            episode.ProviderIds["EmbyCredits.Fail"] = "true";
                            _debugLogger.LogInfo($"Marked episode {episode.Name} as failed for future skipping");
                            _libraryManager?.UpdateItem(episode, episode.Parent, ItemUpdateType.MetadataEdit, null!);
                        }
                        return (false, 0, $"Video validation failed: {validationResult.errorMessage}", 0, string.Empty, string.Empty);
                    }
                }

                _debugLogger.LogInfo("Running credits detection");
                
                var series = episode.Series;
                var seriesId = series?.Id.ToString();
                var seasonNumber = episode.ParentIndexNumber;
                var episodeNumber = episode.IndexNumber;
                var effectiveConfig = _ruleMatchingService.GetEffectiveConfiguration(episode);

                if (effectiveConfig.DisableDetection)
                {
                    _debugLogger.LogInfo($"Detection disabled by rule for {episode.Name}, skipping");
                    return (false, 0, "Detection disabled by rule", 0, string.Empty, string.Empty);
                }

                if (_configuration.EnableTheIntroDB && _theIntroDbService != null)
                {
                    CreditsDetectionService.AddEpisodeStatusMessage(episodeId, "Querying TheIntroDB...");
                    var introDbTimestamp = await _theIntroDbService.GetCreditsTimestamp(episode).ConfigureAwait(false);
                    if (introDbTimestamp.HasValue && introDbTimestamp.Value > 0)
                    {
                        double introDbCreditsStart = introDbTimestamp.Value;
                        double introDbFinalTimestamp = introDbCreditsStart + _configuration.TimestampOffsetSeconds;

                        if (introDbFinalTimestamp < 0)
                        {
                            _debugLogger.LogWarn($"[TheIntroDB] Final timestamp with offset ({FormatTime(introDbFinalTimestamp)}) is negative for {episode.Name}");
                        }
                        else if (introDbFinalTimestamp >= duration)
                        {
                            _debugLogger.LogWarn($"[TheIntroDB] Final timestamp with offset ({FormatTime(introDbFinalTimestamp)}) exceeds video duration for {episode.Name}");
                        }
                        else
                        {
                            if (!isDryRun)
                                _chapterMarkerService.SaveCreditsMarker(episode, introDbFinalTimestamp);

                            if (episode.ProviderIds != null && episode.ProviderIds.ContainsKey("EmbyCredits.Fail"))
                            {
                                episode.ProviderIds.Remove("EmbyCredits.Fail");
                                _libraryManager?.UpdateItem(episode, episode.Parent, ItemUpdateType.MetadataEdit, null!);
                            }

                            _debugLogger.LogInfo($"✓ [{(isDryRun ? "DRY RUN" : "SAVED")}] [TheIntroDB] Credits at {FormatTime(introDbCreditsStart)}, saved at {FormatTime(introDbFinalTimestamp)} for {episode.Name}");
                            return (true, introDbCreditsStart, string.Empty, 1.0, "TheIntroDB", "Community database timestamp");
                        }
                    }
                    else
                    {
                        CreditsDetectionService.AddEpisodeStatusMessage(episodeId, "TheIntroDB: no data, falling back to detection");
                    }
                }
                
                DetectionCoordinator coordinatorForEpisode;
                if (effectiveConfig != _configuration)
                {
                    _debugLogger.LogInfo("Using rule-based configuration for this episode");
                    localCoordinator = new DetectionCoordinator(_logger, effectiveConfig);
                    coordinatorForEpisode = localCoordinator;
                }
                else
                {
                    coordinatorForEpisode = _detectionCoordinator;
                }
                
                var result = !string.IsNullOrEmpty(seriesId) 
                    ? await coordinatorForEpisode.DetectCreditsWithContext(normalizedPath, duration, episodeId, seriesId, seasonNumber, episodeNumber)
                    : await coordinatorForEpisode.DetectCredits(normalizedPath, duration, episodeId);
                double creditsStart = result.timestamp;
                string failureReason = result.failureReason;
                double confidence = result.confidence;
                string methodName = result.methodName;
                string detectionReason = result.detectionReason;

                if (creditsStart > 0)
                {
                    var containerStartTime = await GetVideoContainerStartTime(normalizedPath);
                    if (containerStartTime > 0)
                    {
                        _debugLogger.LogInfo($"Applying container start_time correction of {containerStartTime:F3}s to {episode.Name}");
                    }
                    double finalTimestamp = creditsStart + containerStartTime + _configuration.TimestampOffsetSeconds;
                    
                    if (finalTimestamp < 0)
                    {
                        _debugLogger.LogWarn($"✗ Final timestamp with offset ({FormatTime(finalTimestamp)}) is negative for {episode.Name}");
                        return (false, 0, $"Final timestamp with offset is negative: {finalTimestamp:F1}s", 0, string.Empty, string.Empty);
                    }
                    
                    if (finalTimestamp >= duration)
                    {
                        _debugLogger.LogWarn($"✗ Final timestamp with offset ({FormatTime(finalTimestamp)}) exceeds video duration ({FormatTime(duration)}) for {episode.Name}");
                        return (false, 0, $"Final timestamp with offset exceeds duration: {finalTimestamp:F1}s >= {duration:F1}s", 0, string.Empty, string.Empty);
                    }

                    if (!isDryRun)
                    {
                        _chapterMarkerService.SaveCreditsMarker(episode, finalTimestamp);
                    }
                    
                    if (episode.ProviderIds != null && episode.ProviderIds.ContainsKey("EmbyCredits.Fail"))
                    {
                        episode.ProviderIds.Remove("EmbyCredits.Fail");
                        _libraryManager?.UpdateItem(episode, episode.Parent, ItemUpdateType.MetadataEdit, null!);
                    }
                    
                    if (_configuration.TimestampOffsetSeconds != 0)
                    {
                        _debugLogger.LogInfo($"✓ [{(isDryRun ? "DRY RUN" : "SAVED")}] Credits detected at {FormatTime(creditsStart)}, saved at {FormatTime(finalTimestamp)} (offset: {_configuration.TimestampOffsetSeconds:+0;-0}s) for {episode.Name} (confidence: {confidence:F2})");
                    }
                    else
                    {
                        _debugLogger.LogInfo($"✓ [{(isDryRun ? "DRY RUN" : "SAVED")}] Credits detected at {FormatTime(creditsStart)}, saved at {FormatTime(finalTimestamp)} for {episode.Name} (confidence: {confidence:F2})");
                    }

                    return (true, creditsStart, string.Empty, confidence, methodName, detectionReason);
                }
                else
                {
                    _debugLogger.LogWarn($"✗ No clear credits detected for {episode.Name}");
                    
                    if (_configuration.SkipPreviouslyFailedEpisodes)
                    {
                        if (episode.ProviderIds == null)
                        {
                            episode.ProviderIds = new MediaBrowser.Model.Entities.ProviderIdDictionary();
                        }
                        episode.ProviderIds["EmbyCredits.Fail"] = "true";
                        _debugLogger.LogInfo($"Marked episode {episode.Name} as failed for future skipping");
                        _libraryManager?.UpdateItem(episode, episode.Parent, ItemUpdateType.MetadataEdit, null!);
                    }
                    
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
                localCoordinator?.Dispose();
                await _cpuThrottler.EndWork(CancellationToken.None).ConfigureAwait(false);

                if (_configuration?.DelayBetweenEpisodesMs > 0)
                {
                    _debugLogger.LogDebug($"Applying {_configuration.DelayBetweenEpisodesMs}ms delay before next episode");
                    try { await Task.Delay(_configuration.DelayBetweenEpisodesMs).ConfigureAwait(false); } catch (OperationCanceledException) { }
                }
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

            _cpuThrottler.BeginWork();

            DetectionCoordinator? localFallbackCoordinator = null;
            try
            {
                _debugLogger.LogInfo($"Processing episode with batch result: {episode.Name} (S{episode.ParentIndexNumber}E{episode.IndexNumber})");

                var effectiveConfig = _ruleMatchingService.GetEffectiveConfiguration(episode);
                if (effectiveConfig.DisableDetection)
                {
                    _debugLogger.LogInfo($"Detection disabled by rule for {episode.Name}, skipping");
                    return (false, 0, "Detection disabled by rule", 0, string.Empty, string.Empty);
                }

                if (batchDetectedTime.HasValue && batchDetectedTime.Value > 0)
                {
                    double creditsStart = batchDetectedTime.Value;
                    var normalizedPath = Utilities.FFmpegHelper.NormalizeFilePath(episode.Path);
                    var containerStartTime = await GetVideoContainerStartTime(normalizedPath).ConfigureAwait(false);
                    if (containerStartTime > 0)
                        _debugLogger.LogInfo($"Applying container start_time correction of {containerStartTime:F3}s to {episode.Name}");
                    double finalTimestamp = creditsStart + containerStartTime + _configuration.TimestampOffsetSeconds;

                    double duration = episode.RunTimeTicks.HasValue && episode.RunTimeTicks.Value > 0
                        ? episode.RunTimeTicks.Value / (double)TimeSpan.TicksPerSecond
                        : 0;

                    if (finalTimestamp < 0)
                    {
                        _debugLogger.LogWarn($"✗ Final timestamp with offset ({FormatTime(finalTimestamp)}) is negative for {episode.Name}");
                        return (false, 0, $"Final timestamp with offset is negative: {finalTimestamp:F1}s", 0, string.Empty, string.Empty);
                    }

                    if (duration > 0 && finalTimestamp >= duration)
                    {
                        _debugLogger.LogWarn($"✗ Final timestamp with offset ({FormatTime(finalTimestamp)}) exceeds video duration ({FormatTime(duration)}) for {episode.Name}");
                        return (false, 0, $"Final timestamp with offset exceeds duration: {finalTimestamp:F1}s >= {duration:F1}s", 0, string.Empty, string.Empty);
                    }

                    if (!isDryRun)
                    {
                        if (_configuration.TimestampOffsetSeconds != 0)
                        {
                            _debugLogger.LogInfo($"Adding chapter marker at {FormatTime(finalTimestamp)} (detected: {FormatTime(creditsStart)}, offset: {_configuration.TimestampOffsetSeconds:+0;-0}s)");
                        }
                        else
                        {
                            _debugLogger.LogInfo($"Adding chapter marker at {FormatTime(finalTimestamp)}");
                        }
                        _chapterMarkerService.SaveCreditsMarker(episode, finalTimestamp);
                        _debugLogger.LogInfo($"Successfully added chapter marker");
                    }
                    else
                    {
                        if (_configuration.TimestampOffsetSeconds != 0)
                        {
                            _debugLogger.LogInfo($"Dry run - would add chapter marker at {FormatTime(finalTimestamp)} (detected: {FormatTime(creditsStart)}, offset: {_configuration.TimestampOffsetSeconds:+0;-0}s)");
                        }
                        else
                        {
                            _debugLogger.LogInfo($"Dry run - would add chapter marker at {FormatTime(creditsStart)}");
                        }
                    }
                    
                    if (episode.ProviderIds != null && episode.ProviderIds.ContainsKey("EmbyCredits.Fail"))
                    {
                        episode.ProviderIds.Remove("EmbyCredits.Fail");
                        _libraryManager?.UpdateItem(episode, episode.Parent, ItemUpdateType.MetadataEdit, null!);
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
                            if (_configuration.SkipPreviouslyFailedEpisodes)
                            {
                                if (episode.ProviderIds == null)
                                {
                                    episode.ProviderIds = new MediaBrowser.Model.Entities.ProviderIdDictionary();
                                }
                                episode.ProviderIds["EmbyCredits.Fail"] = "true";
                                _debugLogger.LogWarn($"Marked episode {episode.Name} as failed for future skipping");
                                _libraryManager?.UpdateItem(episode, episode.Parent, ItemUpdateType.MetadataEdit, null!);
                            }
                            return (false, 0, $"Video validation failed: {validationResult.errorMessage}", 0, string.Empty, string.Empty);
                        }
                        
                        _debugLogger.LogDebug("Video validation passed");
                    }

                    // Try detection methods in priority order
                    DetectionCoordinator coordinatorToUse;
                    if (overrideCoordinator != null)
                    {
                        coordinatorToUse = overrideCoordinator;
                    }
                    else if (effectiveConfig != _configuration)
                    {
                        _debugLogger.LogInfo("Using rule-based configuration for fallback detection");
                        localFallbackCoordinator = new DetectionCoordinator(_logger, effectiveConfig);
                        coordinatorToUse = localFallbackCoordinator;
                    }
                    else
                    {
                        coordinatorToUse = _detectionCoordinator;
                    }
                    var detectionResult = await coordinatorToUse.DetectCreditsWithContext(
                        normalizedPath,
                        duration,
                        episode.Id.ToString(),
                        episode.Series?.Id.ToString() ?? string.Empty,
                        episode.ParentIndexNumber,
                        episode.IndexNumber);

                    if (detectionResult.timestamp > 0)
                    {
                        double creditsStart = detectionResult.timestamp;
                        double finalTimestamp = creditsStart + _configuration.TimestampOffsetSeconds;
                        
                        if (finalTimestamp < 0)
                        {
                            _debugLogger.LogWarn($"✗ Final timestamp with offset ({FormatTime(finalTimestamp)}) is negative for {episode.Name}");
                            return (false, 0, $"Final timestamp with offset is negative: {finalTimestamp:F1}s", 0, string.Empty, string.Empty);
                        }
                        
                        if (finalTimestamp >= duration)
                        {
                            _debugLogger.LogWarn($"✗ Final timestamp with offset ({FormatTime(finalTimestamp)}) exceeds video duration ({FormatTime(duration)}) for {episode.Name}");
                            return (false, 0, $"Final timestamp with offset exceeds duration: {finalTimestamp:F1}s >= {duration:F1}s", 0, string.Empty, string.Empty);
                        }
                        
                        if (!isDryRun)
                        {
                            if (_configuration.TimestampOffsetSeconds != 0)
                            {
                                _debugLogger.LogInfo($"Adding chapter marker at {FormatTime(finalTimestamp)} (detected: {FormatTime(creditsStart)}, offset: {_configuration.TimestampOffsetSeconds:+0;-0}s)");
                            }
                            else
                            {
                                _debugLogger.LogInfo($"Adding chapter marker at {FormatTime(creditsStart)}");
                            }
                            _chapterMarkerService.SaveCreditsMarker(episode, finalTimestamp);
                            _debugLogger.LogInfo($"Successfully added chapter marker");
                        }
                        else
                        {
                            if (_configuration.TimestampOffsetSeconds != 0)
                            {
                                _debugLogger.LogInfo($"Dry run - would add chapter marker at {FormatTime(finalTimestamp)} (detected: {FormatTime(creditsStart)}, offset: {_configuration.TimestampOffsetSeconds:+0;-0}s)");
                            }
                            else
                            {
                                _debugLogger.LogInfo($"Dry run - would add chapter marker at {FormatTime(creditsStart)}");
                            }
                        }
                        
                        if (episode.ProviderIds != null && episode.ProviderIds.ContainsKey("EmbyCredits.Fail"))
                        {
                            episode.ProviderIds.Remove("EmbyCredits.Fail");
                            _libraryManager?.UpdateItem(episode, episode.Parent, ItemUpdateType.MetadataEdit, null!);
                        }

                        return (true, detectionResult.timestamp, string.Empty, detectionResult.confidence, detectionResult.methodName, detectionResult.detectionReason);
                    }
                    else
                    {
                        var durationStr = episode.RunTimeTicks.HasValue && episode.RunTimeTicks.Value > 0
                            ? $" ({FormatTime(episode.RunTimeTicks.Value / (double)TimeSpan.TicksPerSecond)})"
                            : string.Empty;
                        _debugLogger.LogWarn($"Failed to detect credits for {episode.Name}{durationStr}: {detectionResult.failureReason}");
                        
                        if (_configuration.SkipPreviouslyFailedEpisodes)
                        {
                            if (episode.ProviderIds == null)
                            {
                                episode.ProviderIds = new MediaBrowser.Model.Entities.ProviderIdDictionary();
                            }
                            episode.ProviderIds["EmbyCredits.Fail"] = "true";
                            _debugLogger.LogInfo($"Marked episode {episode.Name} as failed for future skipping");
                            _libraryManager?.UpdateItem(episode, episode.Parent, ItemUpdateType.MetadataEdit, null!);
                        }
                        
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
                localFallbackCoordinator?.Dispose();
                await _cpuThrottler.EndWork(CancellationToken.None).ConfigureAwait(false);

                if (_configuration?.DelayBetweenEpisodesMs > 0)
                {
                    _debugLogger.LogDebug($"Applying {_configuration.DelayBetweenEpisodesMs}ms delay before next episode");
                    try { await Task.Delay(_configuration.DelayBetweenEpisodesMs).ConfigureAwait(false); } catch (OperationCanceledException) { }
                }
            }
        }

        public List<DetectionMethods.IDetectionMethod> GetDetectionMethods()
        {
            return _detectionCoordinator.GetAllDetectionMethods();
        }

        private async Task<double> GetVideoContainerStartTime(string filePath)
        {
            try
            {
                var normalizedFilePath = Utilities.FFmpegHelper.ResolveInputPath(filePath);

                var ffprobeStartInfo = new ProcessStartInfo
                {
                    FileName = Utilities.FFmpegHelper.GetFfprobePath(),
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                ffprobeStartInfo.ArgumentList.Add("-v");
                ffprobeStartInfo.ArgumentList.Add("error");
                ffprobeStartInfo.ArgumentList.Add("-show_entries");
                ffprobeStartInfo.ArgumentList.Add("format=start_time");
                ffprobeStartInfo.ArgumentList.Add("-of");
                ffprobeStartInfo.ArgumentList.Add("default=noprint_wrappers=1:nokey=1");
                ffprobeStartInfo.ArgumentList.Add(normalizedFilePath);

                using (var process = new Process { StartInfo = ffprobeStartInfo })
                {
                    process.Start();
                    CpuThrottler.SetProcessPriority(process, _configuration);

                    var stderrTask = process.StandardError.ReadToEndAsync();
                    var output = await process.StandardOutput.ReadToEndAsync();
                    await process.WaitForExitAsync();
                    await stderrTask.ConfigureAwait(false);

                    var trimmed = output.Trim();
                    if (trimmed == "N/A" || string.IsNullOrEmpty(trimmed))
                        return 0;

                    if (double.TryParse(trimmed, System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture, out var startTime))
                    {
                        return startTime > 0 ? startTime : 0;
                    }

                    return 0;
                }
            }
            catch (Exception ex)
            {
                _debugLogger.LogError($"Error getting container start_time: {ex.Message}", ex);
                return 0;
            }
        }

        private async Task<double> GetVideoDuration(string filePath)
        {
            try
            {
                _debugLogger.LogDebug($"Getting video duration for: {filePath}");

                var normalizedFilePath = Utilities.FFmpegHelper.ResolveInputPath(filePath);

                var ffprobeStartInfo = new ProcessStartInfo
                {
                    FileName = Utilities.FFmpegHelper.GetFfprobePath(),
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                ffprobeStartInfo.ArgumentList.Add("-v");
                ffprobeStartInfo.ArgumentList.Add("error");
                ffprobeStartInfo.ArgumentList.Add("-show_entries");
                ffprobeStartInfo.ArgumentList.Add("format=duration");
                ffprobeStartInfo.ArgumentList.Add("-of");
                ffprobeStartInfo.ArgumentList.Add("default=noprint_wrappers=1:nokey=1");
                ffprobeStartInfo.ArgumentList.Add(normalizedFilePath);

                using (var process = new Process { StartInfo = ffprobeStartInfo })
                {
                    process.Start();
                    CpuThrottler.SetProcessPriority(process, _configuration);
                    
                    var stderrTask = process.StandardError.ReadToEndAsync();
                    var output = await process.StandardOutput.ReadToEndAsync();
                    await process.WaitForExitAsync();
                    await stderrTask.ConfigureAwait(false);

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
            catch (Exception ex)
            {
                _debugLogger.LogError($"Error getting video duration: {ex.Message}", ex);
                return 0;
            }
        }

        private string FormatTime(double seconds) => Utilities.ItemLookupHelper.FormatTime(seconds);
    }
}
