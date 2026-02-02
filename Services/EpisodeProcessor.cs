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
        private readonly DetectionCoordinator _detectionCoordinator;
        private readonly ChapterMarkerService _chapterMarkerService;
        private readonly DebugLogger _debugLogger;
        private readonly PluginConfiguration _configuration;
        private readonly CpuThrottler _cpuThrottler;

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
        }

        public async Task<(bool success, double creditsStart, string failureReason, double confidence)> ProcessEpisode(
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
                _debugLogger.LogDebug($"Episode path: {episode.Path}");
                _debugLogger.LogDebug($"Episode ID: {episodeId}");

                var normalizedPath = Utilities.FFmpegHelper.NormalizeFilePath(episode.Path);
                if (!string.IsNullOrEmpty(normalizedPath) && normalizedPath != episode.Path)
                {
                    _debugLogger.LogDebug($"Normalized path: {normalizedPath}");
                }

                if (string.IsNullOrEmpty(normalizedPath))
                {
                    _debugLogger.LogWarn($"Path normalization failed for: {episode.Path}");
                    return (false, 0, "Path normalization failed", 0);
                }

                if (!normalizedPath.StartsWith("smb://"))
                {
                    bool fileExists = false;
                    bool checkFailed = false;
                    try
                    {
                        fileExists = File.Exists(normalizedPath);
                    }
                    catch (Exception ex)
                    {
                        _debugLogger.LogDebug($"File.Exists check threw exception: {ex.Message}");
                        checkFailed = true;
                    }

                    if (!checkFailed && !fileExists)
                    {
                        _debugLogger.LogWarn($"Episode file not found: {episode.Path}");
                        if (!string.IsNullOrEmpty(normalizedPath) && normalizedPath != episode.Path)
                        {
                            _debugLogger.LogDebug($"Also tried normalized path: {normalizedPath}");
                        }
                        return (false, 0, "File not found", 0);
                    }
                }
                else
                {
                    _debugLogger.LogDebug($"SMB path detected - will be handled by Emby's MediaEncoder");
                }

                double duration = 0;
                if (episode.RunTimeTicks.HasValue && episode.RunTimeTicks.Value > 0)
                {
                    duration = episode.RunTimeTicks.Value / (double)TimeSpan.TicksPerSecond;
                    _debugLogger.LogDebug($"Using duration from Emby metadata: {duration} seconds");
                }
                else
                {
                    _debugLogger.LogDebug("Episode duration not available from Emby, trying ffprobe");
                    duration = await GetVideoDuration(normalizedPath);
                }

                if (duration <= 0)
                {
                    _debugLogger.LogWarn($"Could not determine video duration for {episode.Name}");
                    return (false, 0, "Could not determine video duration", 0);
                }

                _debugLogger.LogInfo($"Video duration: {FormatTime(duration)}");

                _debugLogger.LogInfo("Running credits detection");
                var result = await _detectionCoordinator.DetectCredits(normalizedPath, duration, episodeId);
                double creditsStart = result.timestamp;
                string failureReason = result.failureReason;
                double confidence = result.confidence;
                _debugLogger.LogDebug($"Detection result: timestamp={creditsStart}, confidence={confidence:F2}, reason={failureReason}");

                if (creditsStart > 0)
                {
                    if (creditsStart >= duration)
                    {
                        _debugLogger.LogWarn($"✗ Detected timestamp ({FormatTime(creditsStart)}) exceeds video duration ({FormatTime(duration)}) for {episode.Name}");
                        return (false, 0, $"Detected timestamp exceeds duration: {creditsStart:F1}s >= {duration:F1}s", 0);
                    }

                    if (!isDryRun)
                    {
                        _debugLogger.LogDebug($"Saving chapter marker at {FormatTime(creditsStart)}");
                        _chapterMarkerService.SaveCreditsMarker(episode, creditsStart);
                    }
                    _debugLogger.LogInfo($"✓ [{(isDryRun ? "DRY RUN" : "SAVED")}] Credits detected at {FormatTime(creditsStart)} for {episode.Name} (confidence: {confidence:F2})");

                    return (true, creditsStart, string.Empty, confidence);
                }
                else
                {
                    _debugLogger.LogWarn($"✗ No clear credits detected for {episode.Name}");
                    if (!string.IsNullOrEmpty(failureReason))
                    {
                        _debugLogger.LogDebug($"Failure reason: {failureReason}");
                    }

                    return (false, 0, failureReason, 0);
                }
            }
            catch (Exception ex)
            {
                _logger.ErrorException($"Error processing episode {episode.Name}", ex);
                return (false, 0, $"Exception: {ex.Message}", 0);
            }
            finally
            {
                await _cpuThrottler.EndWork();

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
