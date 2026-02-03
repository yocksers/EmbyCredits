using MediaBrowser.Model.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EmbyCredits.Services.Utilities;

namespace EmbyCredits.Services.DetectionMethods
{
    public class ChromaprintDetection : BaseDetectionMethod
    {
        public override string MethodName => "Chromaprint Audio Fingerprint Detection";
        
        private double _calculatedConfidence = 0.90;
        public override double Confidence => _calculatedConfidence;
        
        public override int Priority => Configuration.ChromaprintDetectionPriority;
        public override bool IsEnabled => Configuration.DetectionMode == DetectionMode.HashOnly || 
                                          Configuration.DetectionMode == DetectionMode.HashWithOcrFallback;

        private static readonly ConcurrentDictionary<string, List<double>> _seriesCreditsTimestamps = new ConcurrentDictionary<string, List<double>>();
        private static readonly ConcurrentDictionary<string, DateTime> _cacheLastAccess = new ConcurrentDictionary<string, DateTime>();
        private const int MaxCacheEntries = 100;
        private const int CacheExpirationHours = 24;

        public ChromaprintDetection(ILogger logger, PluginConfiguration configuration)
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
            foreach (var key in _seriesCreditsTimestamps.Keys.ToList())
            {
                if (_seriesCreditsTimestamps.TryRemove(key, out var timestamps))
                {
                    timestamps?.Clear();
                }
            }
            _cacheLastAccess.Clear();
        }

        public override async Task<double> DetectCredits(string videoPath, double duration, CancellationToken cancellationToken = default)
        {
            return await DetectCreditsInternal(videoPath, duration, string.Empty, null!, null, null, cancellationToken);
        }

        public async Task<double> DetectCreditsWithContext(string videoPath, double duration, string episodeId, string seriesId, int? seasonNumber, int? episodeNumber, CancellationToken cancellationToken = default)
        {
            return await DetectCreditsInternal(videoPath, duration, episodeId, seriesId, seasonNumber, episodeNumber, cancellationToken);
        }

        private async Task<double> DetectCreditsInternal(string videoPath, double duration, string episodeId, string seriesId, int? seasonNumber, int? episodeNumber, CancellationToken cancellationToken = default)
        {
            LastError = string.Empty;
            _calculatedConfidence = 0.90;
            bool cacheCleared = false;
            
            try
            {
                Logger.Info($"[{MethodName}] Starting Chromaprint-based credits detection...");
                LogInfo("Starting Chromaprint-based credits detection...");
                
                CleanupCache();

                var analysisPercent = Configuration.ChromaprintAnalysisPercent / 100.0;
                var analysisStartTime = duration * (1.0 - analysisPercent);
                
                LogDebug($"Analyzing last {Configuration.ChromaprintAnalysisPercent}% of video (from {FormatTime(analysisStartTime)})");
                
                var minIntroDuration = Configuration.ChromaprintMinDuration;
                var maxIntroDuration = Configuration.ChromaprintMaxDuration;
                
                LogDebug($"Looking for credit sequences between {minIntroDuration}s and {maxIntroDuration}s");
                
                if (Configuration.ChromaprintEnableEpisodeComparison && !string.IsNullOrEmpty(seriesId) && seasonNumber.HasValue)
                {
                    LogDebug("Episode comparison enabled, checking for existing series data");
                    var cacheKey = $"{seriesId}_S{seasonNumber.Value:D2}";
                    
                    if (_seriesCreditsTimestamps.TryGetValue(cacheKey, out var existingTimestamps) && existingTimestamps.Count >= Configuration.ChromaprintEpisodeComparisonMinimumEpisodes)
                    {
                        _cacheLastAccess[cacheKey] = DateTime.UtcNow;
                        
                        var avgTimestamp = existingTimestamps.Average();
                        var stdDev = Math.Sqrt(existingTimestamps.Average(t => Math.Pow(t - avgTimestamp, 2)));
                        
                        LogDebug($"Found {existingTimestamps.Count} existing episodes in season with avg credits at {FormatTime(avgTimestamp)} (±{stdDev:F1}s)");
                        
                        var tolerance = Math.Max(Configuration.ChromaprintEpisodeComparisonTolerance, stdDev * 2);
                        var searchStart = Math.Max(0, avgTimestamp - tolerance);
                        var searchEnd = Math.Min(duration, avgTimestamp + tolerance);
                        
                        LogDebug($"Narrowing search window to {FormatTime(searchStart)} - {FormatTime(searchEnd)} based on episode comparison");
                        
                        var comparisonBlackFrameTime = await DetectBlackFrameTransition(videoPath, searchStart, searchEnd, cancellationToken);
                        
                        if (comparisonBlackFrameTime > 0)
                        {
                            var creditsDuration = duration - comparisonBlackFrameTime;
                            
                            if (creditsDuration >= minIntroDuration && creditsDuration <= maxIntroDuration)
                            {
                                var deviation = Math.Abs(comparisonBlackFrameTime - avgTimestamp);
                                if (deviation <= tolerance)
                                {
                                    LogInfo($"Detected credits start at {FormatTime(comparisonBlackFrameTime)} via episode comparison (deviation: {deviation:F1}s)");
                                    DetectionReason = $"Black frame transition detected at {FormatTime(comparisonBlackFrameTime)} via episode comparison";
                                    _calculatedConfidence = 0.95;
                                    
                                    if (episodeNumber.HasValue)
                                    {
                                        AddToSeriesCache(cacheKey, comparisonBlackFrameTime);
                                    }
                                    
                                    return comparisonBlackFrameTime;
                                }
                            }
                        }
                        
                        var comparisonSilenceTime = await DetectAudioSilenceTransition(videoPath, searchStart, searchEnd, cancellationToken);
                        
                        if (comparisonSilenceTime > 0)
                        {
                            var creditsDuration = duration - comparisonSilenceTime;
                            
                            if (creditsDuration >= minIntroDuration && creditsDuration <= maxIntroDuration)
                            {
                                var deviation = Math.Abs(comparisonSilenceTime - avgTimestamp);
                                if (deviation <= tolerance)
                                {
                                    LogInfo($"Detected credits start at {FormatTime(comparisonSilenceTime)} via episode comparison (deviation: {deviation:F1}s)");
                                    DetectionReason = $"Audio silence transition detected at {FormatTime(comparisonSilenceTime)} via episode comparison";
                                    _calculatedConfidence = 0.93;
                                    
                                    if (episodeNumber.HasValue)
                                    {
                                        AddToSeriesCache(cacheKey, comparisonSilenceTime);
                                    }
                                    
                                    return comparisonSilenceTime;
                                }
                            }
                        }
                        
                        LogWarn("Episode comparison search did not find valid credits");
                        LogInfo("Retrying with full analysis window (keeping cache)...");
                        
                        EmbyCredits.Services.CreditsDetectionService.AddEpisodeStatusMessage(episodeId, "Retrying with full window");
                        cacheCleared = true;
                    }
                    else
                    {
                        LogDebug($"Insufficient episode data for comparison (have {existingTimestamps?.Count ?? 0}, need {Configuration.ChromaprintMinEpisodeCount})");
                    }
                }
                
                var blackFrameTime = await DetectBlackFrameTransition(videoPath, analysisStartTime, duration, cancellationToken);
                
                if (blackFrameTime > 0)
                {
                    var creditsDuration = duration - blackFrameTime;
                    
                    if (creditsDuration >= minIntroDuration && creditsDuration <= maxIntroDuration)
                    {
                        LogInfo($"Detected credits start at {FormatTime(blackFrameTime)} (duration: {FormatTime(creditsDuration)})");
                        if (cacheCleared)
                        {
                            EmbyCredits.Services.CreditsDetectionService.AddEpisodeStatusMessage(episodeId, "Retry successful");
                        }
                        DetectionReason = $"Black frame transition detected at {FormatTime(blackFrameTime)} with credits duration {FormatTime(creditsDuration)}";
                        _calculatedConfidence = 0.85;
                        
                        if (Configuration.ChromaprintEnableEpisodeComparison && !string.IsNullOrEmpty(seriesId) && seasonNumber.HasValue && episodeNumber.HasValue)
                        {
                            var cacheKey = $"{seriesId}_S{seasonNumber.Value:D2}";
                            AddToSeriesCache(cacheKey, blackFrameTime);
                        }
                        
                        return blackFrameTime;
                    }
                    else if (creditsDuration < minIntroDuration)
                    {
                        LogDebug($"Black frame at {FormatTime(blackFrameTime)} too close to end (duration: {FormatTime(creditsDuration)}s < minimum {minIntroDuration}s)");
                    }
                    else
                    {
                        LogDebug($"Black frame at {FormatTime(blackFrameTime)} too far from end (duration: {FormatTime(creditsDuration)}s > maximum {maxIntroDuration}s)");
                    }
                }
                
                var silenceTime = await DetectAudioSilenceTransition(videoPath, analysisStartTime, duration, cancellationToken);
                
                if (silenceTime > 0)
                {
                    var creditsDuration = duration - silenceTime;
                    
                    if (creditsDuration >= minIntroDuration && creditsDuration <= maxIntroDuration)
                    {
                        LogInfo($"Detected credits start via audio silence at {FormatTime(silenceTime)} (duration: {FormatTime(creditsDuration)})");
                        if (cacheCleared)
                        {
                            EmbyCredits.Services.CreditsDetectionService.AddEpisodeStatusMessage(episodeId, "Retry successful");
                        }
                        DetectionReason = $"Audio silence transition detected at {FormatTime(silenceTime)} with credits duration {FormatTime(creditsDuration)}";
                        _calculatedConfidence = 0.80;
                        
                        if (Configuration.ChromaprintEnableEpisodeComparison && !string.IsNullOrEmpty(seriesId) && seasonNumber.HasValue && episodeNumber.HasValue)
                        {
                            var cacheKey = $"{seriesId}_S{seasonNumber.Value:D2}";
                            AddToSeriesCache(cacheKey, silenceTime);
                        }
                        
                        return silenceTime;
                    }
                    else if (creditsDuration < minIntroDuration)
                    {
                        LogDebug($"Silence at {FormatTime(silenceTime)} too close to end (duration: {FormatTime(creditsDuration)}s < minimum {minIntroDuration}s)");
                    }
                    else
                    {
                        LogDebug($"Silence at {FormatTime(silenceTime)} too far from end (duration: {FormatTime(creditsDuration)}s > maximum {maxIntroDuration}s)");
                    }
                }
                
                LogDebug("=== Chromaprint Detection Failed ===");
                LogDebug($"  Analysis range: {FormatTime(analysisStartTime)} to {FormatTime(duration)} ({Configuration.ChromaprintAnalysisPercent}% of video)");
                LogDebug($"  Required credit duration: {minIntroDuration}s to {maxIntroDuration}s");
                if (blackFrameTime > 0)
                {
                    LogDebug($"  Black frame found at {FormatTime(blackFrameTime)} but duration {FormatTime(duration - blackFrameTime)}s was outside acceptable range");
                }
                else
                {
                    LogDebug($"  No black frame transitions detected in analysis range");
                }
                if (silenceTime > 0)
                {
                    LogDebug($"  Silence found at {FormatTime(silenceTime)} but duration {FormatTime(duration - silenceTime)}s was outside acceptable range");
                }
                else
                {
                    LogDebug($"  No audio silence transitions detected in analysis range");
                }
                LogDebug("  Suggestion: Check if credits duration falls within min/max range or adjust analysis percentage");
                LogDebug("=== End Chromaprint Detection ===");
                
                Logger.Info($"[{MethodName}] Detection complete but no credits found");
                LastError = $"No credits boundary found in analysis range. Black frame: {(blackFrameTime > 0 ? "found but wrong duration" : "not found")}. Silence: {(silenceTime > 0 ? "found but wrong duration" : "not found")}";
                return 0;
            }
            catch (Exception ex)
            {
                LastError = $"Chromaprint detection error: {ex.Message}";
                Logger.ErrorException($"[{MethodName}] Error during Chromaprint detection", ex);
                LogError($"Error during Chromaprint detection: {ex.Message}", ex);
                return 0;
            }
        }

        private void AddToSeriesCache(string cacheKey, double timestamp)
        {
            var timestamps = _seriesCreditsTimestamps.GetOrAdd(cacheKey, _ => new List<double>());
            
            lock (timestamps)
            {
                if (!timestamps.Contains(timestamp))
                {
                    timestamps.Add(timestamp);
                    _cacheLastAccess[cacheKey] = DateTime.UtcNow;
                    LogDebug($"Added timestamp {FormatTime(timestamp)} to series cache (total: {timestamps.Count})");
                }
            }
        }

        private void CleanupCache()
        {
            if (_seriesCreditsTimestamps.Count <= MaxCacheEntries)
                return;

            var keysToRemove = _cacheLastAccess
                .Where(kvp => (DateTime.UtcNow - kvp.Value).TotalHours > CacheExpirationHours)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var key in keysToRemove)
            {
                if (_seriesCreditsTimestamps.TryRemove(key, out var timestamps))
                {
                    timestamps?.Clear();
                }
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
                    if (_seriesCreditsTimestamps.TryRemove(key, out var timestamps))
                    {
                        timestamps?.Clear();
                    }
                    _cacheLastAccess.TryRemove(key, out _);
                }
            }
        }

        private async Task<double> DetectBlackFrameTransition(string videoPath, double startTime, double duration, CancellationToken cancellationToken)
        {
            try
            {
                var blackThreshold = Configuration.ChromaprintBlackFrameThreshold;
                var minDuration = Configuration.ChromaprintBlackFrameMinDuration;
                
                LogDebug($"Detecting black frames (threshold: {blackThreshold}, min duration: {minDuration}s)");
                
                var tempFolder = GetTempFolder();
                var ffmpegPath = FFmpegHelper.GetFfmpegPath();
                
                if (string.IsNullOrWhiteSpace(ffmpegPath))
                {
                    LogWarn("FFmpeg not found, skipping black frame detection");
                    return 0;
                }
                
                var endTime = duration - Configuration.ChromaprintStopSecondsFromEnd;
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
                            var match = System.Text.RegularExpressions.Regex.Match(line, @"black_start:(\d+\.?\d*)");
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

        private async Task<double> DetectAudioSilenceTransition(string videoPath, double startTime, double duration, CancellationToken cancellationToken)
        {
            try
            {
                var silenceThreshold = Configuration.ChromaprintSilenceThreshold;
                var minDuration = Configuration.ChromaprintSilenceMinDuration;
                
                LogDebug($"Detecting audio silence (threshold: {silenceThreshold}dB, min duration: {minDuration}s)");
                
                var ffmpegPath = FFmpegHelper.GetFfmpegPath();
                
                if (string.IsNullOrWhiteSpace(ffmpegPath))
                {
                    LogWarn("FFmpeg not found, skipping silence detection");
                    return 0;
                }
                
                var endTime = duration - Configuration.ChromaprintStopSecondsFromEnd;
                var analysisDuration = endTime - startTime;
                
                if (analysisDuration <= 0)
                {
                    LogDebug("Analysis duration too short, skipping silence detection");
                    return 0;
                }
                
                var threadArgs = Configuration.ChromaprintFfmpegThreads > 0 
                    ? $"-threads {Configuration.ChromaprintFfmpegThreads} " 
                    : "";
                
                var ffmpegInputPath = FFmpegHelper.GetInputArgument(videoPath);
                
                var arguments = $"{threadArgs}-ss {startTime.ToString(CultureInfo.InvariantCulture)} -t {analysisDuration.ToString(CultureInfo.InvariantCulture)} -i {ffmpegInputPath} " +
                               $"-af \"silencedetect=noise={silenceThreshold.ToString(CultureInfo.InvariantCulture)}dB:d={minDuration.ToString(CultureInfo.InvariantCulture)}\" " +
                               $"-vn -f null -";
                
                Logger.Info($"[{MethodName}] Executing FFmpeg silence detection: {ffmpegPath} {arguments}");
                
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
                    
                    var silencePeriods = new List<double>();
                    foreach (var line in output)
                    {
                        if (line.Contains("silencedetect") && line.Contains("silence_start:"))
                        {
                            var match = System.Text.RegularExpressions.Regex.Match(line, @"silence_start:\s*(\d+\.?\d*)");
                            if (match.Success && double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var silenceStart))
                            {
                                var adjustedTime = startTime + silenceStart;
                                silencePeriods.Add(adjustedTime);
                                LogDebug($"Found silence period at {FormatTime(adjustedTime)}");
                            }
                        }
                    }
                    
                    output.Clear();
                    output.TrimExcess();
                    output = null;
                    
                    if (silencePeriods.Count > 0)
                    {
                        var result = silencePeriods.First();
                        silencePeriods.Clear();
                        silencePeriods.TrimExcess();
                        return result;
                    }
                    
                    silencePeriods.Clear();
                    silencePeriods.TrimExcess();
                }
                
                return 0;
            }
            catch (Exception ex)
            {
                LogWarn($"Error detecting silence: {ex.Message}");
                return 0;
            }
        }

        private string GetTempFolder()
        {
            if (!string.IsNullOrWhiteSpace(Configuration.TempFolderPath) && Directory.Exists(Configuration.TempFolderPath))
            {
                return Configuration.TempFolderPath;
            }
            return Path.GetTempPath();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
            }
            base.Dispose(disposing);
        }
    }
}
