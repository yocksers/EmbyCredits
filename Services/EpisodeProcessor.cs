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

namespace EmbyCredits.Services
{
    public class EpisodeProcessor
    {
        private readonly ILogger _logger;
        private readonly ILibraryManager? _libraryManager;
        private readonly DetectionCoordinator _detectionCoordinator;
        private readonly ChapterMarkerService _chapterMarkerService;
        private readonly DebugLogger _debugLogger;
        private readonly PluginConfiguration _configuration;

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
        }

        public async Task<(bool success, double creditsStart, string failureReason)> ProcessEpisode(
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

            try
            {
                _debugLogger.LogInfo($"Processing episode: {episode.Name} (S{episode.ParentIndexNumber}E{episode.IndexNumber})");
                _debugLogger.LogDebug($"Episode path: {episode.Path}");
                _debugLogger.LogDebug($"Episode ID: {episodeId}");

                var normalizedPath = Utilities.FFmpegHelper.NormalizeFilePath(episode.Path);
                if (!string.IsNullOrEmpty(normalizedPath) && normalizedPath != episode.Path)
                {
                    _debugLogger.LogDebug($"Normalized path: {normalizedPath}");
                }

                if (string.IsNullOrEmpty(normalizedPath) || !File.Exists(normalizedPath))
                {
                    _debugLogger.LogWarn($"Episode file not found: {episode.Path}");
                    if (!string.IsNullOrEmpty(normalizedPath) && normalizedPath != episode.Path)
                    {
                        _debugLogger.LogDebug($"Also tried normalized path: {normalizedPath}");
                    }
                    return (false, 0, "File not found");
                }

                var duration = await GetVideoDuration(normalizedPath);
                if (duration <= 0)
                {
                    _debugLogger.LogWarn($"Could not determine video duration for {episode.Name}");
                    return (false, 0, "Could not determine video duration");
                }

                _debugLogger.LogInfo($"Video duration: {FormatTime(duration)}");

                double creditsStart = 0;
                string failureReason = string.Empty;

                if (_configuration.UseEpisodeComparison && _libraryManager != null && episode.Series != null)
                {
                    if (isBatchMode)
                    {
                        _debugLogger.LogInfo("Using batch mode with cross-episode analysis");

                        var comparisonEpisodeIds = batchDetectionCache.Keys
                            .Where(id => id != episodeId)
                            .ToList();

                        if (comparisonEpisodeIds.Count > 0)
                        {
                            _debugLogger.LogInfo($"Analyzing with {comparisonEpisodeIds.Count} comparison episodes from batch cache");
                            creditsStart = _detectionCoordinator.AnalyzeBatchDetectionResults(episodeId, comparisonEpisodeIds);
                        }
                    }
                    else
                    {
                        var comparisonEpisodes = _libraryManager.GetItemList(new InternalItemsQuery
                        {
                            IncludeItemTypes = new[] { "Episode" },
                            IsVirtualItem = false,
                            HasPath = true,
                            AncestorIds = new[] { episode.Series.InternalId }
                        }).OfType<Episode>()
                        .Where(e => e.ParentIndexNumber == episode.ParentIndexNumber &&
                                   e.Id != episode.Id &&
                                   !string.IsNullOrEmpty(e.Path) &&
                                   File.Exists(Utilities.FFmpegHelper.NormalizeFilePath(e.Path)))
                        .Take(_configuration.MinimumEpisodesToCompare)
                        .ToList();

                        if (comparisonEpisodes.Count >= 2)
                        {
                            _debugLogger.LogInfo($"Using cross-episode comparison with {comparisonEpisodes.Count} episodes");
                            _debugLogger.LogDebug($"Comparison episodes: {string.Join(", ", comparisonEpisodes.Select(e => e.Name))}");
                            var result = await _detectionCoordinator.DetectCreditsWithComparison(
                                episode, duration, comparisonEpisodes);
                            creditsStart = result.timestamp;
                            failureReason = result.failureReason;
                            _debugLogger.LogDebug($"Comparison result: timestamp={creditsStart}, reason={failureReason}");
                        }
                        else
                        {
                            _debugLogger.LogInfo($"Not enough comparison episodes (found {comparisonEpisodes.Count}, need 2+), using single-episode detection");
                            var result = await _detectionCoordinator.DetectCredits(normalizedPath, duration, episodeId);
                            creditsStart = result.timestamp;
                            failureReason = result.failureReason;
                            _debugLogger.LogDebug($"Single detection result: timestamp={creditsStart}, reason={failureReason}");
                        }
                    }
                }
                else
                {
                    _debugLogger.LogInfo("Comparison disabled or no series context");
                    var result = await _detectionCoordinator.DetectCredits(normalizedPath, duration, episodeId);
                    creditsStart = result.timestamp;
                    failureReason = result.failureReason;
                    _debugLogger.LogDebug($"Detection result: timestamp={creditsStart}, reason={failureReason}");
                }

                if (creditsStart > 0)
                {
                    if (!isDryRun)
                    {
                        _debugLogger.LogDebug($"Saving chapter marker at {FormatTime(creditsStart)}");
                        _chapterMarkerService.SaveCreditsMarker(episode, creditsStart);
                    }
                    _debugLogger.LogInfo($"✓ [{(isDryRun ? "DRY RUN" : "SAVED")}] Credits detected at {FormatTime(creditsStart)} for {episode.Name}");

                    return (true, creditsStart, string.Empty);
                }
                else
                {
                    _debugLogger.LogWarn($"✗ No clear credits detected for {episode.Name}");
                    if (!string.IsNullOrEmpty(failureReason))
                    {
                        _debugLogger.LogDebug($"Failure reason: {failureReason}");
                    }

                    return (false, 0, failureReason);
                }
            }
            catch (Exception ex)
            {
                _logger.ErrorException($"Error processing episode {episode.Name}", ex);
                return (false, 0, $"Exception: {ex.Message}");
            }
            finally
            {
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

                if (_configuration?.CpuUsageLimit < 100)
                {
                    var throttleDelayMs = CalculateThrottleDelay();
                    if (throttleDelayMs > 0)
                    {
                        _debugLogger.LogDebug($"CPU throttling: {throttleDelayMs}ms delay");
                        await Task.Delay(throttleDelayMs);
                    }
                }
            }
        }

        private async Task<double> GetVideoDuration(string filePath)
        {
            try
            {
                _debugLogger.LogDebug($"Getting video duration for: {filePath}");

                var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = Utilities.FFmpegHelper.GetFfprobePath(),
                        Arguments = $"-v error -show_entries format=duration -of default=noprint_wrappers=1:nokey=1 \"{filePath}\"",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    }
                };

                process.Start();
                var output = await process.StandardOutput.ReadToEndAsync();
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
            catch (Exception ex)
            {
                _debugLogger.LogError($"Error getting video duration: {ex.Message}", ex);
                return 0;
            }
        }

        private int CalculateThrottleDelay()
        {
            var cpuLimit = _configuration?.CpuUsageLimit ?? 100;
            if (cpuLimit >= 100)
                return 0;

            var throttleRatio = (100.0 - cpuLimit) / cpuLimit;
            var baseDelay = _configuration?.DelayBetweenEpisodesMs ?? 1000;
            return (int)(baseDelay * throttleRatio);
        }

        private string FormatTime(double seconds)
        {
            var ts = TimeSpan.FromSeconds(seconds);
            return $"{(int)ts.TotalMinutes}:{ts.Seconds:D2}";
        }
    }
}
