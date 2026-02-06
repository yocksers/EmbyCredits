using MediaBrowser.Model.Logging;
using System;
using System.Collections.Concurrent;
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
    public class BlackFrameDetectionFallback : BaseDetectionMethod
    {
        private static readonly ConcurrentDictionary<string, List<double>> _seriesCreditsTimestamps = new ConcurrentDictionary<string, List<double>>();
        private static readonly ConcurrentDictionary<string, DateTime> _cacheLastAccess = new ConcurrentDictionary<string, DateTime>();
        private const int MaxCacheEntries = 100;
        private const int CacheExpirationHours = 24;
        
        private double _calculatedConfidence = 0.85;
        
        public override string MethodName => "BlackFrame (Fallback)";
        public override double Confidence => _calculatedConfidence;
        public override int Priority => Configuration.BlackScreenPriority;
        public override bool IsEnabled => true;

        public BlackFrameDetectionFallback(ILogger logger, PluginConfiguration configuration) 
            : base(logger, configuration)
        {
        }
        
        public static void ClearSeriesCache(string seriesId, int seasonNumber)
        {
            var cacheKey = $"{seriesId}_S{seasonNumber:D2}";
            if (_seriesCreditsTimestamps.TryRemove(cacheKey, out var timestamps))
            {
                timestamps?.Clear();
            }
            _cacheLastAccess.TryRemove(cacheKey, out _);
        }

        public static void ClearAllCache()
        {
            foreach (var timestamps in _seriesCreditsTimestamps.Values)
            {
                timestamps?.Clear();
            }
            _seriesCreditsTimestamps.Clear();
            _cacheLastAccess.Clear();
        }

        public override async Task<double> DetectCredits(string videoPath, double duration, CancellationToken cancellationToken = default)
        {
            LastError = string.Empty;
            DetectionReason = string.Empty;
            _calculatedConfidence = 0.85;

            try
            {
                var minimumCreditsDuration = 30.0;
                var maxCreditsDuration = 240.0;
                
                var analysisStartTime = Math.Max(duration * 0.75, duration - 240);
                var endTime = duration - 5.0;
                
                if (endTime <= analysisStartTime)
                {
                    LastError = "Video too short for black frame analysis";
                    LogDebug("Video too short for black frame analysis");
                    return 0;
                }
                
                LogDebug($"=== Black Frame Detection (Fallback) ===");
                LogDebug($"  Analysis range: {FormatTime(analysisStartTime)} to {FormatTime(endTime)}");
                LogDebug($"  Required credit duration: {minimumCreditsDuration}s to {maxCreditsDuration}s");
                
                var blackFrameTime = await DetectBlackFrameTransition(videoPath, analysisStartTime, endTime, cancellationToken);
                
                if (blackFrameTime > 0)
                {
                    var creditsDuration = duration - blackFrameTime;
                    
                    if (creditsDuration >= minimumCreditsDuration && creditsDuration <= maxCreditsDuration)
                    {
                        LogInfo($"Black frame detected at {FormatTime(blackFrameTime)}");
                        DetectionReason = $"Black frame transition at {FormatTime(blackFrameTime)}";
                        return blackFrameTime;
                    }
                    else if (creditsDuration < minimumCreditsDuration)
                    {
                        LogDebug($"Black frame at {FormatTime(blackFrameTime)} too close to end (duration: {FormatTime(creditsDuration)}s < minimum {minimumCreditsDuration}s)");
                        LastError = $"Black frame too close to end (duration {creditsDuration:F1}s < {minimumCreditsDuration}s)";
                    }
                    else
                    {
                        LogDebug($"Black frame at {FormatTime(blackFrameTime)} too far from end (duration: {FormatTime(creditsDuration)}s > maximum {maxCreditsDuration}s)");
                        LastError = $"Black frame too far from end (duration {creditsDuration:F1}s > {maxCreditsDuration}s)";
                    }
                }
                else
                {
                    LastError = "No black frames detected in analysis range";
                    LogDebug("No black frame transitions detected in analysis range");
                }
                
                LogDebug("=== End Black Frame Detection (Fallback) ===");
                return 0;
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
            var cacheKey = $"{seriesId}_S{seasonNumber:D2}";
            _cacheLastAccess[cacheKey] = DateTime.UtcNow;

            if (_seriesCreditsTimestamps.Count > MaxCacheEntries)
            {
                CleanupExpiredCacheEntries();
            }

            double result;
            
            if (_seriesCreditsTimestamps.TryGetValue(cacheKey, out var cachedTimestamps) && cachedTimestamps.Count >= 2)
            {
                double averageTimestamp;
                double standardDeviation;
                lock (cachedTimestamps)
                {
                    averageTimestamp = cachedTimestamps.Average();
                    var variance = cachedTimestamps.Select(t => Math.Pow(t - averageTimestamp, 2)).Average();
                    standardDeviation = Math.Sqrt(variance);
                }

                var tolerance = Math.Max(30.0, standardDeviation * 4);
                var narrowStartTime = Math.Max(0, averageTimestamp - tolerance);
                var narrowEndTime = duration - 5.0;

                LogInfo($"BlackFrame (Fallback) Episode comparison for {cacheKey} E{episodeNumber:D2}: Using {cachedTimestamps.Count} episodes, avg={FormatTime(averageTimestamp)}, stdDev={standardDeviation:F1}s");
                LogInfo($"BlackFrame (Fallback) Narrowing start time to {FormatTime(narrowStartTime)} (skipping {FormatTime(narrowStartTime)}), scanning to {FormatTime(narrowEndTime)} (tolerance: -{tolerance:F0}s from avg)");

                result = await DetectBlackFrameInRange(videoPath, duration, narrowStartTime, narrowEndTime, cancellationToken);

                if (result > 0)
                {
                    _calculatedConfidence = 0.95;
                }
                else
                {
                    LogWarn($"BlackFrame (Fallback) Episode comparison failed to detect in narrowed window for {cacheKey} E{episodeNumber:D2}");
                    LogInfo($"Retrying with full search window (keeping cache)...");
                    
                    EmbyCredits.Services.CreditsDetectionService.AddEpisodeStatusMessage(episodeId, "Retrying with full window");
                    
                    result = await DetectCredits(videoPath, duration, cancellationToken);
                    
                    if (result > 0)
                    {
                        LogInfo($"BlackFrame (Fallback) Retry successful - credits found at {FormatTime(result)} (was outside comparison window)");
                        LogInfo($"Adding new timestamp to cache - this will widen tolerance for future episodes");
                        EmbyCredits.Services.CreditsDetectionService.AddEpisodeStatusMessage(episodeId, "Retry successful");
                    }
                }
            }
            else
            {
                result = await DetectCredits(videoPath, duration, cancellationToken);
            }

            if (result > 0)
            {
                var episodeTimestamps = _seriesCreditsTimestamps.GetOrAdd(cacheKey, _ => new List<double>());
                lock (episodeTimestamps)
                {
                    episodeTimestamps.Add(result);
                    
                    if (episodeTimestamps.Count > 1)
                    {
                        var newAvg = episodeTimestamps.Average();
                        var newStdDev = Math.Sqrt(episodeTimestamps.Average(t => Math.Pow(t - newAvg, 2)));
                        LogInfo($"Stored BlackFrame (Fallback) timestamp {FormatTime(result)} for {cacheKey} E{episodeNumber:D2} (total: {episodeTimestamps.Count} episodes, new avg: {FormatTime(newAvg)}, stdDev: {newStdDev:F1}s)");
                    }
                    else
                    {
                        LogInfo($"Stored first BlackFrame (Fallback) timestamp {FormatTime(result)} for {cacheKey} E{episodeNumber:D2}");
                    }
                }
                
                _cacheLastAccess[cacheKey] = DateTime.UtcNow;
            }

            return result;
        }

        private void CleanupExpiredCacheEntries()
        {
            var keysToRemove = _cacheLastAccess
                .Where(kvp => (DateTime.UtcNow - kvp.Value).TotalHours > CacheExpirationHours)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var key in keysToRemove)
            {
                _seriesCreditsTimestamps.TryRemove(key, out _);
                _cacheLastAccess.TryRemove(key, out _);
            }

            if (_seriesCreditsTimestamps.Count > MaxCacheEntries)
            {
                var oldestKeys = _cacheLastAccess
                    .OrderBy(kvp => kvp.Value)
                    .Take(_seriesCreditsTimestamps.Count - MaxCacheEntries)
                    .Select(kvp => kvp.Key)
                    .ToList();

                foreach (var key in oldestKeys)
                {
                    _seriesCreditsTimestamps.TryRemove(key, out _);
                    _cacheLastAccess.TryRemove(key, out _);
                }
            }
        }

        private async Task<double> DetectBlackFrameInRange(string videoPath, double duration, double startTime, double endTime, CancellationToken cancellationToken)
        {
            try
            {
                var minimumCreditsDuration = 30.0;
                var maxCreditsDuration = 240.0;
                
                if (endTime <= startTime)
                {
                    LogDebug("Invalid range for black frame detection");
                    return 0;
                }
                
                var blackFrameTime = await DetectBlackFrameTransition(videoPath, startTime, endTime, cancellationToken);
                
                if (blackFrameTime > 0)
                {
                    var creditsDuration = duration - blackFrameTime;
                    
                    if (creditsDuration >= minimumCreditsDuration && creditsDuration <= maxCreditsDuration)
                    {
                        LogInfo($"Black frame detected at {FormatTime(blackFrameTime)} (narrowed range)");
                        DetectionReason = $"Black frame transition at {FormatTime(blackFrameTime)} (episode comparison)";
                        return blackFrameTime;
                    }
                }
                
                return 0;
            }
            catch (Exception ex)
            {
                LogWarn($"Error in narrowed black frame detection: {ex.Message}");
                return 0;
            }
        }

        private async Task<double> DetectBlackFrameTransition(string videoPath, double startTime, double endTime, CancellationToken cancellationToken)
        {
            try
            {
                var blackThreshold = Configuration.BlackFrameThreshold / 100.0;
                var minDuration = 0.5;
                
                LogDebug($"Detecting black frames (threshold: {blackThreshold:F2}, min duration: {minDuration}s)");
                
                var ffmpegPath = FFmpegHelper.GetFfmpegPath();
                
                if (string.IsNullOrWhiteSpace(ffmpegPath))
                {
                    LogWarn("FFmpeg not found, skipping black frame detection");
                    return 0;
                }
                
                var analysisDuration = endTime - startTime;
                
                if (analysisDuration <= 0)
                {
                    LogDebug("Analysis duration too short, skipping black frame detection");
                    return 0;
                }
                
                var threadArgs = Configuration.ChromaprintFfmpegThreads > 0 
                    ? $"-threads {Configuration.ChromaprintFfmpegThreads} " 
                    : "";
                
                var ffmpegInputPath = FFmpegHelper.GetInputArgument(videoPath);
                
                var arguments = $"{threadArgs}-ss {startTime.ToString(CultureInfo.InvariantCulture)} -t {analysisDuration.ToString(CultureInfo.InvariantCulture)} -i {ffmpegInputPath} " +
                               $"-vf \"blackdetect=d={minDuration.ToString(CultureInfo.InvariantCulture)}:pix_th={blackThreshold.ToString(CultureInfo.InvariantCulture)}\" " +
                               $"-an -f null -";
                
                Logger.Info($"[{MethodName}] Executing FFmpeg black detection: {ffmpegPath} {arguments}");
                
                using (var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = ffmpegPath,
                        Arguments = arguments,
                        UseShellExecute = false,
                        RedirectStandardError = true,
                        RedirectStandardOutput = true,
                        CreateNoWindow = true
                    }
                })
                {
                    if (Configuration.ChromaprintLowerProcessPriority)
                    {
                        CpuThrottler.SetProcessPriority(process, Configuration);
                    }
                    
                    var output = new List<string>();
                    DataReceivedEventHandler errorHandler = (sender, e) =>
                    {
                        if (!string.IsNullOrEmpty(e.Data))
                        {
                            output.Add(e.Data);
                        }
                    };
                    
                    try
                    {
                        process.ErrorDataReceived += errorHandler;
                        
                        process.Start();
                        
                        if (Configuration.ChromaprintLowerProcessPriority)
                        {
                            CpuThrottler.SetProcessPriority(process, Configuration);
                        }
                        
                        process.BeginErrorReadLine();
                        
                        await process.WaitForExitAsync(cancellationToken);
                    
                        if (Configuration.ChromaprintDelayBetweenOperationsMs > 0)
                        {
                            await Task.Delay(Configuration.ChromaprintDelayBetweenOperationsMs, cancellationToken);
                        }
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
                    
                    var blackFrames = new List<double>();
                    foreach (var line in output)
                    {
                        if (line.Contains("blackdetect") && line.Contains("black_start:"))
                        {
                            var match = Regex.Match(line, @"black_start:(\d+\.?\d*)");
                            if (match.Success && double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var blackStart))
                            {
                                var adjustedTime = startTime + blackStart;
                                blackFrames.Add(adjustedTime);
                                LogDebug($"Found black frame at {FormatTime(adjustedTime)}");
                            }
                        }
                    }
                    
                    output.Clear();
                    output.TrimExcess();
                    output = null;
                    
                    if (blackFrames.Count > 0)
                    {
                        var result = blackFrames.First();
                        blackFrames.Clear();
                        blackFrames.TrimExcess();
                        return result;
                    }
                    
                    blackFrames.Clear();
                    blackFrames.TrimExcess();
                }
                
                return 0;
            }
            catch (Exception ex)
            {
                LogWarn($"Error detecting black frames: {ex.Message}");
                return 0;
            }
        }
    }
}
