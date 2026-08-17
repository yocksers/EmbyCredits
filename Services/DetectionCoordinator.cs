using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using EmbyCredits.Services.DetectionMethods;

namespace EmbyCredits.Services
{

    public class DetectionCoordinator : IDisposable
    {
        private readonly ILogger _logger;
        private readonly PluginConfiguration _configuration;
        private readonly List<IDetectionMethod> _detectionMethods;
        private bool _disposed = false;
        private CancellationTokenSource? _cancellationTokenSource;

        public DetectionCoordinator(ILogger logger, PluginConfiguration configuration)
        {
            _logger = logger;
            _configuration = configuration;
            _detectionMethods = new List<IDetectionMethod>();
            _cancellationTokenSource = new CancellationTokenSource();

            InitializeDetectionMethods();
        }

        private void InitializeDetectionMethods()
        {
            if (_configuration.EnableDetailedLogging)
            {
                _logger.Debug($"[DetectionCoordinator] Initializing detection methods");
            }
            
            if (_configuration.DetectionMode == DetectionMode.OcrOnly || 
                _configuration.DetectionMode == DetectionMode.OcrWithHashFallback)
            {
                var ocrMethod = new OcrDetection(_logger, _configuration);
                _detectionMethods.Add(ocrMethod);
                if (_configuration.EnableDetailedLogging)
                {
                    _logger.Debug($"[DetectionCoordinator] Added OcrDetection. IsEnabled: {ocrMethod.IsEnabled}");
                }
                
                var chromaprintMethod = new ChromaprintDetection(_logger, _configuration);
                _detectionMethods.Add(chromaprintMethod);
                if (_configuration.EnableDetailedLogging)
                {
                    _logger.Debug($"[DetectionCoordinator] Added ChromaprintDetection. IsEnabled: {chromaprintMethod.IsEnabled}");
                }
            }
            else if (_configuration.DetectionMode == DetectionMode.BlackFrameOnly)
            {
                var blackFrameMethod = new BlackFrameDetection(_logger, _configuration);
                _detectionMethods.Add(blackFrameMethod);
                if (_configuration.EnableDetailedLogging)
                {
                    _logger.Debug($"[DetectionCoordinator] Added BlackFrameDetection (BlackFrameOnly mode). IsEnabled: {blackFrameMethod.IsEnabled}");
                }
            }
            else
            {
                var chromaprintMethod = new ChromaprintDetection(_logger, _configuration);
                _detectionMethods.Add(chromaprintMethod);
                if (_configuration.EnableDetailedLogging)
                {
                    _logger.Debug($"[DetectionCoordinator] Added ChromaprintDetection. IsEnabled: {chromaprintMethod.IsEnabled}");
                }
                
                var ocrMethod = new OcrDetection(_logger, _configuration);
                _detectionMethods.Add(ocrMethod);
                if (_configuration.EnableDetailedLogging)
                {
                    _logger.Debug($"[DetectionCoordinator] Added OcrDetection. IsEnabled: {ocrMethod.IsEnabled}");
                }
            }
        }

        private List<IDetectionMethod> BuildAnimeMethodsList()
        {
            var methods = new List<IDetectionMethod>();
            if (_configuration.EnableDetailedLogging)
            {
                _logger.Debug($"[DetectionCoordinator] Initializing detection methods for anime (DetectionMode: {_configuration.DetectionMode}, AnimeDetectionMethod: {_configuration.AnimeDetectionMethod})");
            }

            if (_configuration.AnimeDetectionMethod == AnimeDetectionMethod.BlackFrame)
            {
                var blackFrameMethod = new BlackFrameDetection(_logger, _configuration);
                methods.Add(blackFrameMethod);
                if (_configuration.EnableDetailedLogging)
                {
                    _logger.Debug($"[DetectionCoordinator] Added BlackFrameDetection for anime. IsEnabled: {blackFrameMethod.IsEnabled}");
                }
            }
            else if (_configuration.AnimeDetectionMethod == AnimeDetectionMethod.Ocr)
            {
                var ocrMethod = new OcrDetection(_logger, _configuration, isForAnime: true);
                methods.Add(ocrMethod);
                if (_configuration.EnableDetailedLogging)
                {
                    _logger.Debug($"[DetectionCoordinator] Added OcrDetection for anime. IsEnabled: {ocrMethod.IsEnabled}");
                }
            }
            return methods;
        }

        private void LogDebug(string message)
        {
            if (CreditsDetectionService.IsDebugMode)
            {
                CreditsDetectionService.LogToDebug("DEBUG", $"[DetectionCoordinator] {message}");
            }
        }

        private void LogInfo(string message)
        {
            if (CreditsDetectionService.IsDebugMode)
            {
                CreditsDetectionService.LogToDebug("INFO", $"[DetectionCoordinator] {message}");
            }
        }

        private void LogWarn(string message)
        {
            if (CreditsDetectionService.IsDebugMode)
            {
                CreditsDetectionService.LogToDebug("WARN", $"[DetectionCoordinator] {message}");
            }
            _logger.Warn($"[DetectionCoordinator] {message}");
        }

        public async Task<(double timestamp, string failureReason, double confidence, string methodName, string detectionReason)> DetectCredits(string videoPath, double duration, string episodeId)
        {
            return await DetectCreditsInternal(videoPath, duration, episodeId, null, null, null);
        }

        public async Task<(double timestamp, string failureReason, double confidence, string methodName, string detectionReason)> DetectCreditsWithContext(string videoPath, double duration, string episodeId, string seriesId, int? seasonNumber, int? episodeNumber)
        {
            return await DetectCreditsInternal(videoPath, duration, episodeId, seriesId, seasonNumber, episodeNumber);
        }

        private async Task<(double timestamp, string failureReason, double confidence, string methodName, string detectionReason)> DetectCreditsInternal(string videoPath, double duration, string episodeId, string? seriesId, int? seasonNumber, int? episodeNumber)
        {
            LogDebug($"DetectCredits called: duration={FormatTime(duration)}, seriesId={seriesId}");

            bool isAnime = false;
            List<IDetectionMethod>? animeMethods = null;
            if (_configuration.EnableAnimeDetection && !string.IsNullOrEmpty(seriesId))
            {
                _logger.Debug($"[DetectionCoordinator] Anime detection enabled, checking seriesId: {seriesId}");
                isAnime = CheckIfAnime(seriesId);
                if (isAnime)
                {
                    _logger.Info($"[DetectionCoordinator] Series identified as anime - using anime-specific detection methods");
                    LogDebug("Series identified as anime - using anime-specific detection methods");
                    animeMethods = BuildAnimeMethodsList();
                }
                else
                {
                    _logger.Debug($"[DetectionCoordinator] Series is not anime, using standard detection methods");
                }
            }
            else
            {
                if (!_configuration.EnableAnimeDetection)
                {
                    _logger.Debug($"[DetectionCoordinator] Anime detection is disabled in configuration");
                }
                if (string.IsNullOrEmpty(seriesId))
                {
                    _logger.Debug($"[DetectionCoordinator] No seriesId provided for anime detection");
                }
            }

            try
            {
            var (detectionResults, methodErrors) = await RunAllDetectionMethods(videoPath, duration, episodeId, seriesId, seasonNumber, episodeNumber, animeMethods);

            // For anime: If all detection methods failed, apply mode-specific fallback logic
            if (isAnime && detectionResults.Count == 0 && _configuration.AnimeDetectionMethod == AnimeDetectionMethod.BlackFrame && seasonNumber.HasValue && episodeNumber.HasValue)
            {
                if (methodErrors.ContainsKey("BlackFrame"))
                {
                    LogInfo("Anime detection: BlackFrame failed, attempting fallback based on detection mode...");

                    foreach (var fallbackMethod in GetAnimeFallbackOrder())
                    {
                        EmbyCredits.Services.CreditsDetectionService.AddEpisodeStatusMessage(episodeId, $"BlackFrame failed, trying {fallbackMethod.MethodName}");
                        var fbResult = await TryFallbackDetectionMethod(fallbackMethod, videoPath, duration, episodeId, seriesId ?? string.Empty, seasonNumber.Value, episodeNumber.Value, "Anime fallback");
                        if (fbResult.HasValue)
                            return fbResult.Value;
                    }

                    LogDebug("All anime fallback attempts failed");
                }
            }

            if (!isAnime && detectionResults.Count == 0 && seasonNumber.HasValue && episodeNumber.HasValue)
            {
                if (_configuration.DetectionMode == DetectionMode.OcrWithHashFallback)
                {
                    var chromaprint = _detectionMethods.FirstOrDefault(m => m is ChromaprintDetection && m.IsEnabled) as ChromaprintDetection;
                    if (chromaprint != null)
                    {
                        LogDebug("OCR detection failed, attempting Hash fallback...");
                        EmbyCredits.Services.CreditsDetectionService.AddEpisodeStatusMessage(episodeId, "OCR failed, trying Hash fallback");
                        var fbResult = await TryFallbackDetectionMethod(chromaprint, videoPath, duration, episodeId, seriesId ?? string.Empty, seasonNumber.Value, episodeNumber.Value, "Fallback");
                        if (fbResult.HasValue)
                        {
                            EmbyCredits.Services.CreditsDetectionService.AddEpisodeStatusMessage(episodeId, "Hash fallback successful");
                            return fbResult.Value;
                        }
                        LogDebug("Hash fallback also failed");
                    }
                }
                else if (_configuration.DetectionMode == DetectionMode.HashWithOcrFallback)
                {
                    var ocr = _detectionMethods.FirstOrDefault(m => m is OcrDetection && m.IsEnabled) as OcrDetection;
                    if (ocr != null)
                    {
                        LogDebug("Hash detection failed, attempting OCR fallback...");
                        EmbyCredits.Services.CreditsDetectionService.AddEpisodeStatusMessage(episodeId, "Hash failed, trying OCR fallback");
                        var fbResult = await TryFallbackDetectionMethod(ocr, videoPath, duration, episodeId, seriesId ?? string.Empty, seasonNumber.Value, episodeNumber.Value, "Fallback");
                        if (fbResult.HasValue)
                        {
                            EmbyCredits.Services.CreditsDetectionService.AddEpisodeStatusMessage(episodeId, "OCR fallback successful");
                            return fbResult.Value;
                        }
                        LogDebug("OCR fallback also failed");
                    }
                }
                else if (_configuration.DetectionMode == DetectionMode.HashOnly && _configuration.ChromaprintEnableBlackFrameFallback)
                {
                    LogDebug("Hash detection failed for non-anime series, attempting BlackFrame detection as fallback...");
                    EmbyCredits.Services.CreditsDetectionService.AddEpisodeStatusMessage(episodeId, "Hash failed, trying BlackFrame fallback");
                    var blackFrame = new BlackFrameDetection(_logger, _configuration);
                    try
                    {
                        double timestamp = await blackFrame.DetectCredits(videoPath, duration, _cancellationTokenSource?.Token ?? default);
                        if (timestamp > 0)
                        {
                            LogInfo($"BlackFrame fallback successful at {FormatTime(timestamp)}");
                            EmbyCredits.Services.CreditsDetectionService.AddEpisodeStatusMessage(episodeId, "BlackFrame fallback successful");
                            var reason = blackFrame.GetDetectionReason();
                            return await ApplySilenceRefinementAndReturn(videoPath, duration, timestamp, $"Fallback: {reason}", blackFrame.Confidence, blackFrame.MethodName, reason);
                        }
                        LogDebug("BlackFrame fallback also failed");
                        methodErrors[blackFrame.MethodName] = string.IsNullOrWhiteSpace(blackFrame.GetLastError()) ? "No black frame transition detected" : blackFrame.GetLastError();
                    }
                    catch (Exception ex)
                    {
                        LogWarn($"BlackFrame fallback error: {ex.Message}");
                        methodErrors["BlackFrame (Fallback)"] = ex.Message;
                    }
                    finally
                    {
                        try { blackFrame?.Dispose(); } catch { }
                    }
                }
            }

            bool shouldTryFallback = false;
            DetectionMode fallbackMode = _configuration.DetectionMode;
            
            if (detectionResults.Count == 0 && (!seasonNumber.HasValue || !episodeNumber.HasValue))
            {
                if (_configuration.DetectionMode == DetectionMode.OcrWithHashFallback)
                {
                    LogDebug("OCR detection failed, attempting Hash fallback (legacy path)...");
                    EmbyCredits.Services.CreditsDetectionService.AddEpisodeStatusMessage(episodeId, "OCR failed, using Hash fallback");
                    shouldTryFallback = true;
                    fallbackMode = DetectionMode.HashOnly;
                }
                else if (_configuration.DetectionMode == DetectionMode.HashWithOcrFallback)
                {
                    LogDebug("Hash detection failed, attempting OCR fallback (legacy path)...");
                    EmbyCredits.Services.CreditsDetectionService.AddEpisodeStatusMessage(episodeId, "Hash failed, using OCR fallback");
                    shouldTryFallback = true;
                    fallbackMode = DetectionMode.OcrOnly;
                }
            }

            if (shouldTryFallback)
            {
                var fallbackConfig = _configuration.ShallowClone();
                fallbackConfig.DetectionMode = fallbackMode;

                var fallbackCoordinator = new DetectionCoordinator(_logger, fallbackConfig);
                try
                {
                    LogDebug($"Running fallback detection with mode: {fallbackMode}");
                    var (fallbackResults, fallbackErrors) = await fallbackCoordinator.RunAllDetectionMethods(videoPath, duration, episodeId, seriesId, seasonNumber, episodeNumber);

                    if (fallbackResults.Count > 0)
                    {
                        LogDebug($"Fallback detection successful! Found {fallbackResults.Count} result(s)");
                        EmbyCredits.Services.CreditsDetectionService.AddEpisodeStatusMessage(episodeId, "Fallback method successful");
                        var fallbackResult = SelectByStrategy(fallbackResults);
                        LogDebug($"Selected timestamp: {FormatTime(fallbackResult.timestamp)} with confidence: {fallbackResult.confidence:F2}");
                        var fallbackMethodName = fallbackResults.FirstOrDefault(r => Math.Abs(r.timestamp - fallbackResult.timestamp) < 0.1).method ?? "Unknown";
                        return (fallbackResult.timestamp, $"Fallback: {fallbackResult.reason}", fallbackResult.confidence, fallbackMethodName, fallbackResult.reason);
                    }
                    else
                    {
                        LogDebug("Fallback detection also failed");
                        foreach (var kvp in fallbackErrors)
                        {
                            if (!methodErrors.ContainsKey(kvp.Key))
                                methodErrors[kvp.Key] = kvp.Value;
                        }
                    }
                }
                finally
                {
                    fallbackCoordinator.Dispose();
                }
            }

            if (detectionResults.Count == 0)
            {
                _logger.Info($"No credits detected (mode: {_configuration.DetectionMode}, duration: {FormatTime(duration)})");
                if (methodErrors.Count > 0)
                {
                    foreach (var kvp in methodErrors)
                        _logger.Info($"  - {kvp.Key}: {kvp.Value}");
                }
                LogDebug("No credits detected by any method");
                
                LogDebug("=== DETECTION FAILURE SUMMARY ===");
                if (methodErrors.Count > 0)
                {
                    LogDebug($"Methods with errors: {methodErrors.Count}");
                    foreach (var error in methodErrors)
                    {
                        LogDebug($"  • {error.Key}: {error.Value}");
                    }
                }
                
                var effectiveMethods = animeMethods ?? _detectionMethods;
                var disabledMethods = effectiveMethods.Where(m => !m.IsEnabled).ToList();
                if (disabledMethods.Count > 0)
                {
                    LogDebug($"Disabled methods: {disabledMethods.Count}");
                    foreach (var method in disabledMethods)
                    {
                        LogDebug($"  • {method.MethodName}");
                    }
                }
                
                var successfulButNoResult = effectiveMethods.Where(m => m.IsEnabled).Count() - methodErrors.Count;
                if (successfulButNoResult > 0)
                {
                    LogDebug($"Methods that ran successfully but found no credits: {successfulButNoResult}");
                    LogDebug("  These methods completed without errors but did not detect any credit markers.");
                }
                
                LogDebug("=== END SUMMARY ===");
                
                string failureReason;
                if (methodErrors.Count == 0)
                {
                    failureReason = "No credits detected by any enabled method";
                }
                else if (methodErrors.Count == 1)
                {
                    failureReason = methodErrors.Values.First();
                }
                else
                {
                    var parts = methodErrors.Select(kvp => $"{kvp.Key}: {kvp.Value}");
                    failureReason = "All detection methods failed — " + string.Join("; ", parts);
                }
                LogDebug($"Overall failure reason: {failureReason}");
                return (0, failureReason, 0, string.Empty, string.Empty);
            }

            LogDebug($"Found {detectionResults.Count} detection result(s)");
            var result = SelectByStrategy(detectionResults);
            LogDebug($"Selected timestamp: {FormatTime(result.timestamp)} with confidence: {result.confidence:F2}");
            
            var selectedMethod = detectionResults.FirstOrDefault(r => Math.Abs(r.timestamp - result.timestamp) < 0.1).method ?? "Unknown";
            
            if (_configuration.ChromaprintUseSilenceDetection && result.timestamp > 0)
            {
                _logger.Info($"[DetectionCoordinator] Applying silence refinement to timestamp {FormatTime(result.timestamp)}");
                var refinedTimestamp = await RefineTimestampWithSilence(videoPath, result.timestamp, duration);
                if (refinedTimestamp > 0 && refinedTimestamp != result.timestamp)
                {
                    _logger.Info($"[DetectionCoordinator] Refined timestamp with silence detection: {FormatTime(result.timestamp)} -> {FormatTime(refinedTimestamp)}");
                    result = (refinedTimestamp, result.reason, result.confidence);
                }
                else if (refinedTimestamp == 0)
                {
                    _logger.Info($"[DetectionCoordinator] Silence detection found no alternative timestamp");
                }
                else
                {
                    _logger.Info($"[DetectionCoordinator] Silence detection confirmed original timestamp");
                }
            }
            
            return (result.timestamp, result.reason, result.confidence, selectedMethod, result.reason);
            }
            finally
            {
                if (animeMethods != null)
                {
                    foreach (var method in animeMethods)
                    {
                        try { method?.Dispose(); } catch { }
                    }
                }
            }
        }
        
        private async Task<(double timestamp, string failureReason, double confidence, string methodName, string detectionReason)> ApplySilenceRefinementAndReturn(
            string videoPath, 
            double duration, 
            double timestamp, 
            string failureReason, 
            double confidence, 
            string methodName, 
            string detectionReason)
        {
            if (_configuration.ChromaprintUseSilenceDetection && timestamp > 0)
            {
                _logger.Info($"[DetectionCoordinator] Applying silence refinement to fallback timestamp {FormatTime(timestamp)}");
                var refinedTimestamp = await RefineTimestampWithSilence(videoPath, timestamp, duration);
                if (refinedTimestamp > 0 && refinedTimestamp != timestamp)
                {
                    _logger.Info($"[DetectionCoordinator] Refined fallback timestamp with silence detection: {FormatTime(timestamp)} -> {FormatTime(refinedTimestamp)}");
                    return (refinedTimestamp, failureReason, confidence, methodName, detectionReason);
                }
                else if (refinedTimestamp == 0)
                {
                    _logger.Info($"[DetectionCoordinator] Silence detection found no alternative for fallback timestamp");
                }
                else
                {
                    _logger.Info($"[DetectionCoordinator] Silence detection confirmed fallback timestamp");
                }
            }
            return (timestamp, failureReason, confidence, methodName, detectionReason);
        }
        
        private async Task<double> RefineTimestampWithSilence(string videoPath, double targetTime, double duration)
        {
            try
            {
                var searchWindow = _configuration.ChromaprintSilenceSearchWindow;
                var startTime = Math.Max(0, targetTime - searchWindow);
                var endTime = Math.Min(duration, targetTime + searchWindow);
                var analysisDuration = endTime - startTime;
                
                if (analysisDuration <= 0)
                {
                    return 0;
                }
                
                var silenceThreshold = _configuration.ChromaprintSilenceThreshold;
                var minDuration = _configuration.ChromaprintSilenceMinDuration;
                var ffmpegPath = Utilities.FFmpegHelper.GetFfmpegPath();
                
                if (string.IsNullOrWhiteSpace(ffmpegPath))
                {
                    return 0;
                }
                
                var normalizedVideoPath = Utilities.FFmpegHelper.NormalizeFilePath(videoPath);

                var silenceStartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                silenceStartInfo.ArgumentList.Add("-ss");
                silenceStartInfo.ArgumentList.Add(startTime.ToString(System.Globalization.CultureInfo.InvariantCulture));
                silenceStartInfo.ArgumentList.Add("-t");
                silenceStartInfo.ArgumentList.Add(analysisDuration.ToString(System.Globalization.CultureInfo.InvariantCulture));
                silenceStartInfo.ArgumentList.Add("-i");
                silenceStartInfo.ArgumentList.Add(normalizedVideoPath);
                silenceStartInfo.ArgumentList.Add("-af");
                silenceStartInfo.ArgumentList.Add($"silencedetect=noise={silenceThreshold.ToString(System.Globalization.CultureInfo.InvariantCulture)}dB:d={minDuration.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
                silenceStartInfo.ArgumentList.Add("-vn");
                silenceStartInfo.ArgumentList.Add("-f");
                silenceStartInfo.ArgumentList.Add("null");
                silenceStartInfo.ArgumentList.Add("-");
                
                _logger.Info($"[DetectionCoordinator] Running silence detection: {ffmpegPath} -ss {startTime} -t {analysisDuration} -i <path> -af silencedetect...");
                
                using (var process = new System.Diagnostics.Process { StartInfo = silenceStartInfo })
                {
                    process.Start();
                    var output = await process.StandardError.ReadToEndAsync();
                    await process.WaitForExitAsync(_cancellationTokenSource?.Token ?? default);
                    
                    var silenceTimes = new List<double>();
                    var lines = output.Split('\n');
                    
                    foreach (var line in lines)
                    {
                        if (line.Contains("silencedetect") && line.Contains("silence_start:"))
                        {
                            var match = System.Text.RegularExpressions.Regex.Match(line, @"silence_start:\s*(\d+\.?\d*)");
                            if (match.Success && double.TryParse(match.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var silenceStart))
                            {
                                var absoluteTime = startTime + silenceStart;
                                if (absoluteTime >= startTime && absoluteTime <= endTime)
                                {
                                    silenceTimes.Add(absoluteTime);
                                }
                            }
                        }
                    }
                    
                    if (silenceTimes.Count > 0)
                    {
                        var closestSilence = silenceTimes.OrderBy(t => Math.Abs(t - targetTime)).First();
                        _logger.Info($"[DetectionCoordinator] Found {silenceTimes.Count} silence point(s), closest at {FormatTime(closestSilence)} (target was {FormatTime(targetTime)})");
                        return closestSilence;
                    }
                    else
                    {
                        _logger.Info($"[DetectionCoordinator] No silence points found near {FormatTime(targetTime)}");
                    }
                }
                
                return 0;
            }
            catch (Exception ex)
            {
                LogWarn($"Silence detection refinement error: {ex.Message}");
                return 0;
            }
        }

        public void ClearCache()
        {
        }

        public List<IDetectionMethod> GetAllDetectionMethods()
        {
            return _detectionMethods;
        }

        internal async Task<(List<(string method, double timestamp, double confidence, int priority, string reason)> results, Dictionary<string, string> errors)> RunAllDetectionMethods(
            string videoPath, 
            double duration,
            string episodeId,
            string? seriesId = null,
            int? seasonNumber = null,
            int? episodeNumber = null,
            IList<IDetectionMethod>? methodsOverride = null)
        {
            var results = new List<(string method, double timestamp, double confidence, int priority, string reason)>();
            var errors = new Dictionary<string, string>();
            var methodsList = methodsOverride ?? _detectionMethods;

            LogDebug($"Running detection methods for video (duration: {FormatTime(duration)})");
            LogDebug($"Total detection methods: {methodsList.Count}");
            LogDebug($"Enabled methods: {string.Join(", ", methodsList.Where(m => m.IsEnabled).Select(m => m.MethodName))}");

            foreach (var method in methodsList)
            {
                if (!method.IsEnabled)
                {
                    if (_configuration.EnableDetailedLogging)
                    {
                        _logger.Debug($"Skipping {method.MethodName} (disabled)");
                    }
                    LogDebug($"Skipping {method.MethodName} (disabled)");
                    continue;
                }

                try
                {
                    LogDebug($"Running {method.MethodName}...");
                    
                    double timestamp = 0;
                    if (method is ChromaprintDetection chromaprintMethod && !string.IsNullOrEmpty(seriesId) && seasonNumber.HasValue && episodeNumber.HasValue)
                    {
                        timestamp = await chromaprintMethod.DetectCreditsWithContext(videoPath, duration, episodeId, seriesId, seasonNumber.Value, episodeNumber.Value, _cancellationTokenSource?.Token ?? default);
                    }
                    else if (method is OcrDetection ocrMethod && !string.IsNullOrEmpty(seriesId) && seasonNumber.HasValue && episodeNumber.HasValue)
                    {
                        timestamp = await ocrMethod.DetectCreditsWithContext(videoPath, duration, episodeId, seriesId, seasonNumber.Value, episodeNumber.Value, _cancellationTokenSource?.Token ?? default);
                    }
                    else if (method is BlackFrameDetection blackFrameMethod && !string.IsNullOrEmpty(seriesId) && seasonNumber.HasValue && episodeNumber.HasValue)
                    {
                        timestamp = await blackFrameMethod.DetectCreditsWithContext(videoPath, duration, episodeId, seriesId, seasonNumber.Value, episodeNumber.Value, _cancellationTokenSource?.Token ?? default);
                    }
                    else
                    {
                        timestamp = await method.DetectCredits(videoPath, duration, _cancellationTokenSource?.Token ?? default);
                    }
                    
                    if (timestamp > 0)
                    {
                        var reason = method.GetDetectionReason();
                        results.Add((method.MethodName, timestamp, method.Confidence, method.Priority, reason));
                        if (_configuration.EnableDetailedLogging)
                        {
                            _logger.Info($"{method.MethodName} detection: {FormatTime(timestamp)}");
                        }
                        LogInfo($"{method.MethodName} detected credits at {FormatTime(timestamp)} (confidence: {method.Confidence}, priority: {method.Priority})");
                        if (!string.IsNullOrEmpty(reason))
                        {
                            LogInfo($"  Reason: {reason}");
                        }
                    }
                    else
                    {
                        var errorMsg = method.GetLastError();
                        if (!string.IsNullOrEmpty(errorMsg))
                        {
                            errors[method.MethodName] = errorMsg;
                            if (_configuration.EnableDetailedLogging)
                            {
                                _logger.Debug($"Skipping {method.MethodName}: {errorMsg}");
                            }
                            LogDebug($"{method.MethodName} failed: {errorMsg}");
                        }
                        else
                        {
                            if (_configuration.EnableDetailedLogging)
                            {
                                _logger.Debug($"{method.MethodName} found no credits");
                            }
                            LogDebug($"{method.MethodName} returned 0 (no credits detected - this may be normal if no credit markers match the configured criteria)");
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.ErrorException($"Error running {method.MethodName}", ex);
                    errors[method.MethodName] = ex.Message;
                    LogDebug($"{method.MethodName} threw exception: {ex.Message}");
                }
            }

            LogDebug($"Detection complete: {results.Count} successful, {errors.Count} errors");
            return (results, errors);
        }

        private (double timestamp, string reason, double confidence) AnalyzeWithCorrelationScoring(List<(string method, double timestamp, double confidence, int priority, string reason)> detectionResults)
        {
            if (detectionResults.Count == 0)
                return (0, string.Empty, 0);

            var correlationWindow = _configuration.CorrelationWindowSeconds;
            var groupedResults = new List<(double timestamp, double combinedScore, List<string> methods, List<string> reasons)>();

            foreach (var result in detectionResults)
            {
                var existingGroup = groupedResults.FirstOrDefault(g => Math.Abs(g.timestamp - result.timestamp) <= correlationWindow);

                if (existingGroup.timestamp > 0)
                {
                    var index = groupedResults.IndexOf(existingGroup);
                    existingGroup.combinedScore += result.confidence;
                    existingGroup.methods.Add(result.method);
                    if (!string.IsNullOrEmpty(result.reason))
                        existingGroup.reasons.Add(result.reason);
                    groupedResults[index] = existingGroup;
                }
                else
                {
                    var reasons = !string.IsNullOrEmpty(result.reason) ? new List<string> { result.reason } : new List<string>();
                    groupedResults.Add((result.timestamp, result.confidence, new List<string> { result.method }, reasons));
                }
            }

            var bestGroup = groupedResults.OrderByDescending(g => g.combinedScore).First();
            var creditsStart = bestGroup.timestamp;
            var combinedReasons = bestGroup.reasons.Count > 0 ? string.Join(" | ", bestGroup.reasons.Distinct()) : string.Empty;
            var normalizedConfidence = Math.Min(1.0, bestGroup.combinedScore / bestGroup.methods.Count);

            _logger.Info($"Correlation scoring selected {FormatTime(creditsStart)} " +
                       $"(score: {bestGroup.combinedScore:F2}, confidence: {normalizedConfidence:F2}, methods: {string.Join(", ", bestGroup.methods)})");

            foreach (var group in groupedResults)
            {
                group.methods?.Clear();
                group.reasons?.Clear();
            }
            groupedResults.Clear();
            groupedResults.TrimExcess();

            return (creditsStart, combinedReasons, normalizedConfidence);
        }

        private (double timestamp, string reason, double confidence) SelectByStrategy(List<(string method, double timestamp, double confidence, int priority, string reason)> detectionResults)
        {
            if (detectionResults.Count == 0)
                return (0, string.Empty, 0);

            var strategy = _configuration.DetectionResultSelection ?? "CorrelationScoring";

            switch (strategy)
            {
                case "Earliest":
                    var earliest = detectionResults.OrderBy(r => r.timestamp).First();
                    _logger.Info($"Earliest mode selected {earliest.method} at {FormatTime(earliest.timestamp)}");
                    return (earliest.timestamp, earliest.reason, earliest.confidence);

                case "Latest":
                    var latest = detectionResults.OrderByDescending(r => r.timestamp).First();
                    _logger.Info($"Latest mode selected {latest.method} at {FormatTime(latest.timestamp)}");
                    return (latest.timestamp, latest.reason, latest.confidence);

                case "Average":
                    var average = detectionResults.Average(r => r.timestamp);
                    var avgConfidence = detectionResults.Average(r => r.confidence);
                    _logger.Info($"Average mode calculated {FormatTime(average)} from {detectionResults.Count} detections");
                    var avgReasons = string.Join(" | ", detectionResults.Select(r => r.reason).Where(r => !string.IsNullOrEmpty(r)));
                    return (average, avgReasons, avgConfidence);

                case "Median":
                    var sorted = detectionResults.OrderBy(r => r.timestamp).ToList();
                    var median = sorted.Count % 2 == 0
                        ? (sorted[sorted.Count / 2 - 1].timestamp + sorted[sorted.Count / 2].timestamp) / 2
                        : sorted[sorted.Count / 2].timestamp;
                    var medianConfidence = sorted.Count % 2 == 0
                        ? (sorted[sorted.Count / 2 - 1].confidence + sorted[sorted.Count / 2].confidence) / 2
                        : sorted[sorted.Count / 2].confidence;
                    _logger.Info($"Median mode calculated {FormatTime(median)} from {detectionResults.Count} detections");
                    var medianReason = sorted.Count % 2 == 0 
                        ? $"{sorted[sorted.Count / 2 - 1].reason} | {sorted[sorted.Count / 2].reason}"
                        : sorted[sorted.Count / 2].reason;
                    return (median, medianReason, medianConfidence);

                case "Priority":
                    var byPriority = detectionResults.OrderBy(r => r.priority).First();
                    _logger.Info($"Priority mode selected {byPriority.method} at {FormatTime(byPriority.timestamp)} (priority: {byPriority.priority})");
                    return (byPriority.timestamp, byPriority.reason, byPriority.confidence);

                case "CorrelationScoring":
                default:
                    return AnalyzeWithCorrelationScoring(detectionResults);
            }
        }

        private string FormatTime(double seconds) => Utilities.ItemLookupHelper.FormatTime(seconds);

        private IEnumerable<IDetectionMethod> GetAnimeFallbackOrder()
        {
            var candidates = new List<IDetectionMethod?>();
            switch (_configuration.DetectionMode)
            {
                case DetectionMode.HashOnly:
                    candidates.Add(_detectionMethods.FirstOrDefault(m => m is ChromaprintDetection));
                    break;
                case DetectionMode.OcrOnly:
                    candidates.Add(_detectionMethods.FirstOrDefault(m => m is OcrDetection));
                    break;
                case DetectionMode.OcrWithHashFallback:
                    candidates.Add(_detectionMethods.FirstOrDefault(m => m is OcrDetection));
                    candidates.Add(_detectionMethods.FirstOrDefault(m => m is ChromaprintDetection));
                    break;
                case DetectionMode.HashWithOcrFallback:
                    candidates.Add(_detectionMethods.FirstOrDefault(m => m is ChromaprintDetection));
                    candidates.Add(_detectionMethods.FirstOrDefault(m => m is OcrDetection));
                    break;
            }
            return candidates.OfType<IDetectionMethod>();
        }

        private async Task<(double timestamp, string failureReason, double confidence, string methodName, string detectionReason)?> TryFallbackDetectionMethod(
            IDetectionMethod method,
            string videoPath, double duration, string episodeId, string seriesId, int seasonNumber, int episodeNumber,
            string reasonPrefix)
        {
            LogDebug($"Running {method.MethodName} fallback...");
            try
            {
                double timestamp = method switch
                {
                    ChromaprintDetection cp => await cp.DetectCreditsWithContext(videoPath, duration, episodeId, seriesId, seasonNumber, episodeNumber, _cancellationTokenSource?.Token ?? default),
                    OcrDetection ocr => await ocr.DetectCreditsWithContext(videoPath, duration, episodeId, seriesId, seasonNumber, episodeNumber, _cancellationTokenSource?.Token ?? default),
                    BlackFrameDetection bf => await bf.DetectCreditsWithContext(videoPath, duration, episodeId, seriesId, seasonNumber, episodeNumber, _cancellationTokenSource?.Token ?? default),
                    _ => await method.DetectCredits(videoPath, duration, _cancellationTokenSource?.Token ?? default)
                };

                if (timestamp > 0)
                {
                    LogInfo($"{method.MethodName} fallback successful at {FormatTime(timestamp)}");
                    var reason = method.GetDetectionReason();
                    return await ApplySilenceRefinementAndReturn(videoPath, duration, timestamp, $"{reasonPrefix}: {reason}", method.Confidence, method.MethodName, reason);
                }
            }
            catch (Exception ex)
            {
                LogWarn($"{method.MethodName} fallback error: {ex.Message}");
            }
            return null;
        }

        public void CancelDetection()        {
            try
            {
                _cancellationTokenSource?.Cancel();
                _cancellationTokenSource?.Dispose();
            }
            catch (ObjectDisposedException)
            {
            }
            finally
            {
                // Recreate for next detection run to prevent using a cancelled token
                _cancellationTokenSource = new CancellationTokenSource();
            }
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                try
                {
                    _cancellationTokenSource?.Cancel();
                }
                catch { }
                
                _cancellationTokenSource?.Dispose();
                _cancellationTokenSource = null;

                foreach (var method in _detectionMethods)
                {
                    try
                    {
                        method?.Dispose();
                    }
                    catch { }
                }
                _detectionMethods.Clear();
                
                _disposed = true;
                
                GC.SuppressFinalize(this);
            }
        }

        private bool CheckIfAnime(string seriesId)
        {
            try
            {
                _logger.Debug($"[DetectionCoordinator] CheckIfAnime called for seriesId: {seriesId}");
                
                if (Plugin.Instance?.LibraryManager == null)
                {
                    _logger.Warn($"[DetectionCoordinator] CheckIfAnime: _libraryManager is null");
                    return false;
                }

                if (!Guid.TryParse(seriesId, out var seriesGuid))
                {
                    _logger.Warn($"[DetectionCoordinator] CheckIfAnime: Failed to parse seriesId as Guid: {seriesId}");
                    return false;
                }

                var item = Plugin.Instance.LibraryManager.GetItemById(seriesGuid);
                if (item == null)
                {
                    _logger.Warn($"[DetectionCoordinator] CheckIfAnime: GetItemById returned null for Guid: {seriesGuid}");
                    return false;
                }

                _logger.Debug($"[DetectionCoordinator] CheckIfAnime: Found item type: {item.GetType().Name}, Name: {item.Name}");

                if (item is MediaBrowser.Controller.Entities.TV.Series series)
                {
                    _logger.Debug($"[DetectionCoordinator] CheckIfAnime: Series found - Name: {series.Name}");

                    if (series.Tags != null && series.Tags.Length > 0)
                    {
                        if (_configuration.EnableDetailedLogging)
                            _logger.Debug($"[DetectionCoordinator] CheckIfAnime: Tags count: {series.Tags.Length}");
                        
                        for (int i = 0; i < series.Tags.Length; i++)
                        {
                            if (series.Tags[i].Equals("anime", StringComparison.OrdinalIgnoreCase))
                            {
                                _logger.Info($"[DetectionCoordinator] CheckIfAnime: Series '{series.Name}' identified as ANIME via Tags");
                                return true;
                            }
                        }
                    }

                    if (series.Genres != null && series.Genres.Length > 0)
                    {
                        if (_configuration.EnableDetailedLogging)
                            _logger.Debug($"[DetectionCoordinator] CheckIfAnime: Genres count: {series.Genres.Length}");
                        
                        for (int i = 0; i < series.Genres.Length; i++)
                        {
                            if (series.Genres[i].Equals("anime", StringComparison.OrdinalIgnoreCase))
                            {
                                _logger.Info($"[DetectionCoordinator] CheckIfAnime: Series '{series.Name}' identified as ANIME via Genres");
                                return true;
                            }
                        }
                    }

                    _logger.Debug($"[DetectionCoordinator] CheckIfAnime: Series '{series.Name}' is NOT anime");
                }
                else
                {
                    _logger.Warn($"[DetectionCoordinator] CheckIfAnime: Item is not a Series, it's: {item.GetType().Name}");
                }

                return false;
            }
            catch (Exception ex)
            {
                _logger.ErrorException($"[DetectionCoordinator] CheckIfAnime exception for seriesId: {seriesId}", ex);
                return false;
            }
        }
    }
}
