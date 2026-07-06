using MediaBrowser.Model.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using EmbyCredits.Services.Utilities;

namespace EmbyCredits.Services.DetectionMethods
{
    public class BlackFrameDetection : BaseDetectionMethod
    {
        private static readonly DetectionTimestampCache _cache = new DetectionTimestampCache();
        
        private double _calculatedConfidence = 0.85;
        
        public override string MethodName => "BlackFrame";
        public override double Confidence => _calculatedConfidence;
        public override int Priority => Configuration.BlackScreenPriority;
        public override bool IsEnabled => Configuration.DetectionMode == DetectionMode.BlackFrameOnly ||
                                          (Configuration.EnableAnimeDetection && Configuration.AnimeDetectionMethod == AnimeDetectionMethod.BlackFrame);

        public BlackFrameDetection(ILogger logger, PluginConfiguration configuration) 
            : base(logger, configuration)
        {
        }
        
        public static void ClearSeriesCache(string seriesId, int seasonNumber) =>
            _cache.ClearSeries(seriesId, seasonNumber);

        public static void ClearAllCache() =>
            _cache.ClearAll();

        public override async Task<double> DetectCredits(string videoPath, double duration, CancellationToken cancellationToken = default)
        {
            LastError = string.Empty;
            DetectionReason = string.Empty;
            _calculatedConfidence = 0.85;

            try
            {
                var minimumCreditsDuration = Configuration.BlackFrameMinCreditsDuration;
                var maxCreditsDuration = Configuration.BlackFrameMaxCreditsDuration;

                var analysisPercent = Configuration.ChromaprintAnalysisPercent / 100.0;
                var analysisStartTime = duration * (1.0 - analysisPercent);
                var endTime = duration - 5.0;

                if (endTime <= analysisStartTime)
                {
                    LastError = "Video too short for black frame analysis";
                    LogDebug("Video too short for black frame analysis");
                    return 0;
                }

                LogDebug($"=== Black Frame Detection ===");
                LogDebug($"  Analysis range: {FormatTime(analysisStartTime)} to {FormatTime(endTime)}");
                LogDebug($"  Required credit duration: {minimumCreditsDuration}s to {maxCreditsDuration}s");

                var result = await RunDetectionPipeline(videoPath, duration, analysisStartTime, endTime, minimumCreditsDuration, maxCreditsDuration, cancellationToken);

                if (result > 0)
                {
                    LogInfo($"Black frame detected at {FormatTime(result)}");
                    DetectionReason = $"Black frame transition at {FormatTime(result)}";
                }
                else
                {
                    LastError = "No valid credit scene detected";
                    LogDebug("No valid credit scene detected");
                }

                LogDebug("=== End Black Frame Detection ===");
                return result;
            }
            catch (Exception ex)
            {
                LastError = $"Black frame detection error: {ex.Message}";
                LogError($"Error during black frame detection: {ex.Message}", ex);
                return 0;
            }
        }

        public async Task<double> DetectCreditsWithContext(string videoPath, double duration, string episodeId, string seriesId, int seasonNumber, int episodeNumber, CancellationToken cancellationToken = default)
        {
            var cacheKey = DetectionTimestampCache.MakeCacheKey(seriesId, seasonNumber);
            _cache.TouchAccess(cacheKey);
            _cache.EnsureCleanedIfNeeded();

            double result = 0;
            
            bool usedCache = false;
            if (_cache.TryGetTimestamps(cacheKey, out var cachedTimestamps))
            {
                double averageTimestamp = 0;
                double standardDeviation = 0;
                bool cacheReady;
                lock (cachedTimestamps)
                {
                    cacheReady = cachedTimestamps.Count >= 2;
                    if (cacheReady)
                    {
                        var snapshot = cachedTimestamps.ToList();
                        averageTimestamp = snapshot.Average();
                        var variance = snapshot.Select(t => Math.Pow(t - averageTimestamp, 2)).Average();
                        standardDeviation = Math.Sqrt(variance);
                    }
                }

                if (cacheReady)
                {
                    usedCache = true;

                    var tolerance = Math.Max(30.0, standardDeviation * 4);
                    var narrowStartTime = Math.Max(0, averageTimestamp - tolerance);
                    var narrowEndTime = duration - 5.0;

                    LogInfo($"BlackFrame Episode comparison for {cacheKey} E{episodeNumber:D2}: Using {cachedTimestamps.Count} episodes, avg={FormatTime(averageTimestamp)}, stdDev={standardDeviation:F1}s");
                    LogInfo($"BlackFrame Narrowing start time to {FormatTime(narrowStartTime)} (skipping {FormatTime(narrowStartTime)}), scanning to {FormatTime(narrowEndTime)} (tolerance: -{tolerance:F0}s from avg)");

                    result = await DetectBlackFrameInRange(videoPath, duration, narrowStartTime, narrowEndTime, cancellationToken);

                    if (result > 0)
                    {
                        _calculatedConfidence = 0.95;
                    }
                    else
                    {
                        LogWarn($"BlackFrame Episode comparison failed to detect in narrowed window for {cacheKey} E{episodeNumber:D2}");
                        LogInfo($"Retrying with full search window (keeping cache)...");

                        EmbyCredits.Services.CreditsDetectionService.AddEpisodeStatusMessage(episodeId, "Retrying with full window");

                        result = await DetectCredits(videoPath, duration, cancellationToken);

                        if (result > 0)
                        {
                            LogInfo($"BlackFrame Retry successful - credits found at {FormatTime(result)} (was outside comparison window)");
                            LogInfo($"Adding new timestamp to cache - this will widen tolerance for future episodes");
                            EmbyCredits.Services.CreditsDetectionService.AddEpisodeStatusMessage(episodeId, "Retry successful");
                        }
                    }
                }
            }

            if (!usedCache)
            {
                result = await DetectCredits(videoPath, duration, cancellationToken);
            }

            if (result > 0)
            {
                var episodeTimestamps = _cache.GetOrAddList(cacheKey);
                lock (episodeTimestamps)
                {
                    episodeTimestamps.Add(result);
                    var snap = episodeTimestamps.ToList();
                    if (snap.Count > 1)
                    {
                        var newAvg = snap.Average();
                        var newStdDev = Math.Sqrt(snap.Average(t => Math.Pow(t - newAvg, 2)));
                        LogInfo($"Stored BlackFrame timestamp {FormatTime(result)} for {cacheKey} E{episodeNumber:D2} (total: {snap.Count} episodes, new avg: {FormatTime(newAvg)}, stdDev: {newStdDev:F1}s)");
                    }
                    else
                    {
                        LogInfo($"Stored first BlackFrame timestamp {FormatTime(result)} for {cacheKey} E{episodeNumber:D2}");
                    }
                }
                
                _cache.TouchAccess(cacheKey);
            }

            return result;
        }

        private async Task<double> DetectBlackFrameInRange(string videoPath, double duration, double startTime, double endTime, CancellationToken cancellationToken)
        {
            try
            {
                var minimumCreditsDuration = Configuration.BlackFrameMinCreditsDuration;
                var maxCreditsDuration = Configuration.BlackFrameMaxCreditsDuration;

                if (endTime <= startTime)
                {
                    LogDebug("Invalid range for black frame detection");
                    return 0;
                }

                var result = await RunDetectionPipeline(videoPath, duration, startTime, endTime, minimumCreditsDuration, maxCreditsDuration, cancellationToken);

                if (result > 0)
                {
                    LogInfo($"Black frame detected at {FormatTime(result)} (narrowed range)");
                    DetectionReason = $"Black frame transition at {FormatTime(result)} (episode comparison)";
                }

                return result;
            }
            catch (Exception ex)
            {
                LogWarn($"Error in narrowed black frame detection: {ex.Message}");
                return 0;
            }
        }

        private async Task<double> RunDetectionPipeline(
            string videoPath,
            double duration,
            double startTime,
            double endTime,
            double minimumCreditsDuration,
            double maxCreditsDuration,
            CancellationToken cancellationToken,
            bool forceAllFrames = false)
        {
            var threshold = Configuration.BlackFrameThreshold;
            var minimumPercentage = Configuration.BlackFrameMinimumPercentage;

            var frames = await ScanKeyframes(videoPath, startTime, endTime, threshold, cancellationToken, forceAllFrames);

            if (frames.Count == 0)
            {
                LogDebug("No keyframes found in analysis range");
                return 0;
            }

            var (minimum, sceneChange) = NormalizeThresholds(frames, minimumPercentage);
            LogDebug($"Normalized thresholds: minimum={minimum}%, sceneChange={sceneChange}%");

            var scenes = DetectCreditScenes(frames, minimum, sceneChange, MinimumBlackFrameDensity, MaximumTimeSkip);

            if (scenes.Count == 0)
            {
                LogDebug("No credit scenes detected after density gating");
                return 0;
            }

            LogDebug($"Detected {scenes.Count} candidate credit scene(s)");

            for (var i = scenes.Count - 1; i >= 0; i--)
            {
                var scene = scenes[i];
                var absoluteStart = scene.StartTime + startTime;
                var creditsDuration = duration - absoluteStart;

                if (creditsDuration < minimumCreditsDuration || creditsDuration > maxCreditsDuration)
                {
                    LogDebug($"Scene at {FormatTime(absoluteStart)} rejected: credits duration {creditsDuration:F1}s out of range");
                    continue;
                }

                double refinedRelative;
                if (Configuration.BlackFrameRefineCreditsBoundary)
                {
                    refinedRelative = await RefineBoundary(videoPath, frames, scene, sceneChange, threshold, startTime, cancellationToken);
                }
                else
                {
                    refinedRelative = scene.StartTime;
                }
                var refinedAbsolute = refinedRelative + startTime;

                LogDebug($"Valid credit scene: start={FormatTime(refinedAbsolute)}, credits duration={FormatTime(duration - refinedAbsolute)}");
                return refinedAbsolute;
            }

            return 0;
        }

        private static (int Minimum, int SceneChange) NormalizeThresholds(List<BlackFrameData> frames, int minimumPercentage)
        {
            var ordered = frames.OrderBy(f => f.Percentage).ToList();
            var percentileIndex = (int)(frames.Count * 0.01);
            var floor = Math.Min(ordered[percentileIndex].Percentage, 30);
            var minimum = (minimumPercentage * (100 - floor) / 100) + floor;
            var sceneChange = (95 * (100 - floor) / 100) + floor;
            return (minimum, sceneChange);
        }

        private async Task<List<BlackFrameData>> ScanKeyframes(string videoPath, double startTime, double endTime, int threshold, CancellationToken cancellationToken, bool forceAllFrames = false)
        {
            var analysisDuration = endTime - startTime;
            if (analysisDuration <= 0)
                return new List<BlackFrameData>();

            var ffmpegPath = FFmpegHelper.GetFfmpegPath();
            if (string.IsNullOrWhiteSpace(ffmpegPath))
            {
                LogWarn("FFmpeg not found");
                return new List<BlackFrameData>();
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };

            if (!forceAllFrames && !Configuration.BlackFrameScanAllFrames)
            {
                startInfo.ArgumentList.Add("-skip_frame");
                startInfo.ArgumentList.Add("nokey");
            }
            startInfo.ArgumentList.Add("-ss");
            startInfo.ArgumentList.Add(startTime.ToString(CultureInfo.InvariantCulture));
            if (Configuration.BlackFrameFfmpegThreads > 0)
            {
                startInfo.ArgumentList.Add("-threads");
                startInfo.ArgumentList.Add(Configuration.BlackFrameFfmpegThreads.ToString(CultureInfo.InvariantCulture));
            }
            startInfo.ArgumentList.Add("-i");
            startInfo.ArgumentList.Add(FFmpegHelper.NormalizeFilePath(videoPath));
            startInfo.ArgumentList.Add("-t");
            startInfo.ArgumentList.Add(analysisDuration.ToString(CultureInfo.InvariantCulture));
            startInfo.ArgumentList.Add("-an");
            startInfo.ArgumentList.Add("-dn");
            startInfo.ArgumentList.Add("-sn");
            startInfo.ArgumentList.Add("-vf");
            startInfo.ArgumentList.Add($"blackframe=amount=0:threshold={threshold}");
            startInfo.ArgumentList.Add("-f");
            startInfo.ArgumentList.Add("null");
            startInfo.ArgumentList.Add("-");

            return await RunFfmpegAndParse(startInfo, cancellationToken, $"KeyframeScan: {System.IO.Path.GetFileName(videoPath)}");
        }

        private async Task<List<BlackFrameData>> ScanFullFrames(string videoPath, double startTime, double endTime, int threshold, CancellationToken cancellationToken)
        {
            var analysisDuration = endTime - startTime;
            if (analysisDuration <= 0)
                return new List<BlackFrameData>();

            var ffmpegPath = FFmpegHelper.GetFfmpegPath();
            if (string.IsNullOrWhiteSpace(ffmpegPath))
                return new List<BlackFrameData>();

            var startInfo = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };

            startInfo.ArgumentList.Add("-ss");
            startInfo.ArgumentList.Add(startTime.ToString(CultureInfo.InvariantCulture));
            if (Configuration.BlackFrameFfmpegThreads > 0)
            {
                startInfo.ArgumentList.Add("-threads");
                startInfo.ArgumentList.Add(Configuration.BlackFrameFfmpegThreads.ToString(CultureInfo.InvariantCulture));
            }
            startInfo.ArgumentList.Add("-i");
            startInfo.ArgumentList.Add(FFmpegHelper.NormalizeFilePath(videoPath));
            startInfo.ArgumentList.Add("-t");
            startInfo.ArgumentList.Add(analysisDuration.ToString(CultureInfo.InvariantCulture));
            startInfo.ArgumentList.Add("-an");
            startInfo.ArgumentList.Add("-dn");
            startInfo.ArgumentList.Add("-sn");
            startInfo.ArgumentList.Add("-vf");
            startInfo.ArgumentList.Add($"blackframe=amount=50:threshold={threshold}");
            startInfo.ArgumentList.Add("-f");
            startInfo.ArgumentList.Add("null");
            startInfo.ArgumentList.Add("-");

            return await RunFfmpegAndParse(startInfo, cancellationToken, $"BoundaryProbe: {System.IO.Path.GetFileName(videoPath)}");
        }

        private async Task<List<BlackFrameData>> RunFfmpegAndParse(ProcessStartInfo startInfo, CancellationToken cancellationToken, string description)
        {
            var outputLines = new List<string>();

            using (var process = new Process { StartInfo = startInfo })
            {
                if (Configuration.ChromaprintLowerProcessPriority)
                    CpuThrottler.SetProcessPriority(process, Configuration);

                DataReceivedEventHandler errorHandler = (sender, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                    {
                        outputLines.Add(e.Data);
                        FFmpegHelper.UpdateLastOutputTime(process.Id);
                    }
                };

                try
                {
                    process.ErrorDataReceived += errorHandler;
                    process.Start();
                    FFmpegHelper.RegisterProcess(process, description);

                    if (Configuration.ChromaprintLowerProcessPriority)
                        CpuThrottler.SetProcessPriority(process, Configuration);

                    process.BeginErrorReadLine();
                    await process.WaitForExitAsync(cancellationToken);

                    if (Configuration.ChromaprintDelayBetweenOperationsMs > 0)
                        await Task.Delay(Configuration.ChromaprintDelayBetweenOperationsMs, cancellationToken);
                }
                finally
                {
                    try
                    {
                        process.ErrorDataReceived -= errorHandler;
                        process.CancelErrorRead();
                    }
                    catch { }
                    FFmpegHelper.UnregisterProcess(process);
                }
            }

            return ParseBlackFrameOutput(outputLines);
        }

        private static readonly Regex _blackFrameRegex = new Regex(
            @"frame:(\d+)\s+pblack:(\d+)\s+pts:\d+\s+t:(\d+\.?\d*)",
            RegexOptions.Compiled);

        private static List<BlackFrameData> ParseBlackFrameOutput(List<string> lines)
        {
            var frames = new List<BlackFrameData>();
            foreach (var line in lines)
            {
                var match = _blackFrameRegex.Match(line);
                if (!match.Success)
                    continue;

                if (!int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var frameNum) ||
                    !int.TryParse(match.Groups[2].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var pblack) ||
                    !double.TryParse(match.Groups[3].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var time))
                    continue;

                frames.Add(new BlackFrameData { Frame = frameNum, Percentage = pblack, Time = time });
            }
            return frames;
        }

        private static List<CreditSceneData> DetectCreditScenes(List<BlackFrameData> frames, int minimum, int sceneChange, double minimumDensity, double maximumMergeGap)
        {
            var rawScenes = new List<CreditSceneData>();
            BlackFrameData? sceneStart = null;
            BlackFrameData? lastBlack = null;

            for (var i = 0; i < frames.Count; i++)
            {
                var frame = frames[i];
                var isBlack = frame.Percentage >= minimum;

                if (isBlack && sceneStart is null)
                {
                    sceneStart = frame;
                    lastBlack = frame;
                }
                else if (isBlack)
                {
                    lastBlack = frame;
                }
                else if (sceneStart.HasValue && lastBlack.HasValue &&
                         (i == frames.Count - 1 || frame.Frame - lastBlack.Value.Frame > 5))
                {
                    if (lastBlack.Value.Frame - sceneStart.Value.Frame >= 5)
                        rawScenes.Add(new CreditSceneData { StartFrame = sceneStart.Value.Frame, EndFrame = lastBlack.Value.Frame, StartTime = sceneStart.Value.Time, EndTime = lastBlack.Value.Time });
                    sceneStart = null;
                    lastBlack = null;
                }
            }

            if (sceneStart.HasValue && lastBlack.HasValue && lastBlack.Value.Frame - sceneStart.Value.Frame >= 5)
                rawScenes.Add(new CreditSceneData { StartFrame = sceneStart.Value.Frame, EndFrame = lastBlack.Value.Frame, StartTime = sceneStart.Value.Time, EndTime = lastBlack.Value.Time });

            var densityFiltered = new List<CreditSceneData>(rawScenes.Count);
            foreach (var scene in rawScenes)
            {
                if (HasMinimumBlackFrameDensity(frames, scene, minimum, minimumDensity))
                    densityFiltered.Add(scene);
            }

            if (densityFiltered.Count == 0)
                return densityFiltered;

            List<CreditSceneData> merged;
            if (densityFiltered.Count <= 1)
            {
                merged = densityFiltered;
            }
            else
            {
                merged = new List<CreditSceneData>(densityFiltered.Count);
                var current = densityFiltered[0];

                for (var i = 1; i < densityFiltered.Count; i++)
                {
                    var next = densityFiltered[i];
                    var span = new CreditSceneData { StartFrame = current.StartFrame, EndFrame = next.EndFrame, StartTime = current.StartTime, EndTime = next.EndTime };

                    if (next.StartTime - current.EndTime <= maximumMergeGap && HasMinimumBlackFrameDensity(frames, span, minimum, minimumDensity))
                        current = span;
                    else
                    {
                        merged.Add(current);
                        current = next;
                    }
                }
                merged.Add(current);
            }

            var finalScenes = new List<CreditSceneData>(merged.Count);
            foreach (var scene in merged)
            {
                var startFrame = scene.StartFrame;
                var startTime = scene.StartTime;

                foreach (var frame in frames)
                {
                    if (frame.Frame > scene.EndFrame)
                        break;
                    if (frame.Frame >= scene.StartFrame && frame.Percentage >= sceneChange)
                    {
                        startFrame = frame.Frame;
                        startTime = frame.Time;
                        break;
                    }
                }

                finalScenes.Add(new CreditSceneData { StartFrame = startFrame, EndFrame = scene.EndFrame, StartTime = startTime, EndTime = scene.EndTime });
            }

            return finalScenes;
        }

        private static bool HasMinimumBlackFrameDensity(List<BlackFrameData> frames, CreditSceneData scene, int minimum, double minimumDensity)
        {
            var totalInScene = 0;
            var blackInScene = 0;
            foreach (var frame in frames)
            {
                if (frame.Time > scene.EndTime)
                    break;
                if (frame.Time >= scene.StartTime)
                {
                    totalInScene++;
                    if (frame.Percentage >= minimum)
                        blackInScene++;
                }
            }
            return totalInScene > 0 && (double)blackInScene / totalInScene >= minimumDensity;
        }

        private async Task<double> RefineBoundary(string videoPath, List<BlackFrameData> frames, CreditSceneData scene, int sceneChange, int threshold, double startTimeOffset, CancellationToken cancellationToken)
        {
            double? lastKeyframeTime = null;
            double? firstBlackTime = null;

            foreach (var frame in frames)
            {
                if (frame.Time < scene.StartTime)
                    lastKeyframeTime = frame.Time;
                else if (firstBlackTime is null)
                {
                    firstBlackTime = frame.Time;
                    break;
                }
            }

            if (!lastKeyframeTime.HasValue || !firstBlackTime.HasValue)
                return scene.StartTime;

            var gap = scene.StartTime - lastKeyframeTime.Value;
            if (gap <= MinimumBoundaryProbeWindow)
                return scene.StartTime;

            var currentDuration = scene.EndTime - scene.StartTime;
            if (currentDuration + gap < 30.0)
                return scene.StartTime;

            var startFrame = default(BlackFrameData);
            foreach (var frame in frames)
            {
                if (frame.Frame == scene.StartFrame)
                {
                    startFrame = frame;
                    break;
                }
            }
            var probeMinimum = startFrame.Percentage > 0 ? Math.Min(startFrame.Percentage, sceneChange) : sceneChange;

            var absoluteProbeStart = lastKeyframeTime.Value + startTimeOffset;
            var absoluteProbeEnd = firstBlackTime.Value + startTimeOffset;

            var probeFrames = await ScanFullFrames(videoPath, absoluteProbeStart, absoluteProbeEnd, threshold, cancellationToken);

            BlackFrameData? probeHit = null;
            foreach (var pf in probeFrames)
            {
                if (pf.Percentage >= probeMinimum)
                {
                    probeHit = pf;
                    break;
                }
            }

            if (!probeHit.HasValue)
                return scene.StartTime;

            var refinedTime = probeHit.Value.Time + lastKeyframeTime.Value;
            if (refinedTime <= lastKeyframeTime.Value || refinedTime > scene.StartTime)
                return scene.StartTime;

            LogDebug($"Refined boundary from {FormatTime(scene.StartTime + startTimeOffset)} to {FormatTime(refinedTime + startTimeOffset)}");
            return refinedTime;
        }

        private struct BlackFrameData
        {
            public int Frame;
            public int Percentage;
            public double Time;
        }

        private struct CreditSceneData
        {
            public int StartFrame;
            public int EndFrame;
            public double StartTime;
            public double EndTime;
        }

        private double MinimumBlackFrameDensity => Configuration.BlackFrameMinimumDensity;
        private double MaximumTimeSkip => Configuration.BlackFrameMaxSceneMergeGap;
        private const double MinimumBoundaryProbeWindow = 0.50;
    }
}
