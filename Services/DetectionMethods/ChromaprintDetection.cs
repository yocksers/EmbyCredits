using MediaBrowser.Model.Logging;
using System;
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
        public override bool IsEnabled => Configuration.EnableChromaprintDetection;

        public ChromaprintDetection(ILogger logger, PluginConfiguration configuration)
            : base(logger, configuration)
        {
        }

        public override async Task<double> DetectCredits(string videoPath, double duration, CancellationToken cancellationToken = default)
        {
            LastError = string.Empty;
            _calculatedConfidence = 0.90;
            
            try
            {
                Logger.Info($"[{MethodName}] Starting Chromaprint-based credits detection...");
                LogInfo("Starting Chromaprint-based credits detection...");
                
                var analysisPercent = Configuration.ChromaprintAnalysisPercent / 100.0;
                var analysisStartTime = duration * (1.0 - analysisPercent);
                
                LogDebug($"Analyzing last {Configuration.ChromaprintAnalysisPercent}% of video (from {FormatTime(analysisStartTime)})");
                
                var minIntroDuration = Configuration.ChromaprintMinDuration;
                var maxIntroDuration = Configuration.ChromaprintMaxDuration;
                
                LogDebug($"Looking for credit sequences between {minIntroDuration}s and {maxIntroDuration}s");
                
                var blackFrameTime = await DetectBlackFrameTransition(videoPath, analysisStartTime, duration, cancellationToken);
                
                if (blackFrameTime > 0)
                {
                    var creditsDuration = duration - blackFrameTime;
                    
                    if (creditsDuration >= minIntroDuration && creditsDuration <= maxIntroDuration)
                    {
                        LogInfo($"Detected credits start at {FormatTime(blackFrameTime)} (duration: {FormatTime(creditsDuration)})");
                        DetectionReason = $"Black frame transition detected at {FormatTime(blackFrameTime)} with credits duration {FormatTime(creditsDuration)}";
                        _calculatedConfidence = 0.85;
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
                        DetectionReason = $"Audio silence transition detected at {FormatTime(silenceTime)} with credits duration {FormatTime(creditsDuration)}";
                        _calculatedConfidence = 0.80;
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
                    process.ErrorDataReceived += (sender, e) =>
                    {
                        if (!string.IsNullOrEmpty(e.Data))
                        {
                            output.Add(e.Data);
                        }
                    };
                    
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
                    
                    if (blackFrames.Count > 0)
                    {
                        return blackFrames.First();
                    }
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
                    process.ErrorDataReceived += (sender, e) =>
                    {
                        if (!string.IsNullOrEmpty(e.Data))
                        {
                            output.Add(e.Data);
                        }
                    };
                    
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
                    
                    if (silencePeriods.Count > 0)
                    {
                        return silencePeriods.First();
                    }
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
