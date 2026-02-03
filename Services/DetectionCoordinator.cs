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

        private readonly Dictionary<string, List<(string method, double timestamp)>> _batchDetectionCache;

        public DetectionCoordinator(ILogger logger, PluginConfiguration configuration)
        {
            _logger = logger;
            _configuration = configuration;
            _detectionMethods = new List<IDetectionMethod>();
            _batchDetectionCache = new Dictionary<string, List<(string method, double timestamp)>>();
            _cancellationTokenSource = new CancellationTokenSource();

            InitializeDetectionMethods();
        }

        private void InitializeDetectionMethods()
        {
            if (_configuration.EnableDetailedLogging)
            {
                _logger.Debug($"[DetectionCoordinator] Initializing detection methods");
            }
            
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

        public async Task<(double timestamp, string failureReason, double confidence)> DetectCredits(string videoPath, double duration, string episodeId)
        {
            return await DetectCreditsInternal(videoPath, duration, episodeId, null!, null, null);
        }

        public async Task<(double timestamp, string failureReason, double confidence)> DetectCreditsWithContext(string videoPath, double duration, string episodeId, string seriesId, int? seasonNumber, int? episodeNumber)
        {
            return await DetectCreditsInternal(videoPath, duration, episodeId, seriesId, seasonNumber, episodeNumber);
        }

        private async Task<(double timestamp, string failureReason, double confidence)> DetectCreditsInternal(string videoPath, double duration, string episodeId, string seriesId, int? seasonNumber, int? episodeNumber)
        {
            LogDebug($"DetectCredits called: duration={FormatTime(duration)}");
            var (detectionResults, methodErrors) = await RunAllDetectionMethods(videoPath, duration, episodeId, seriesId, seasonNumber, episodeNumber);

            // Check if fallback is needed
            bool shouldTryFallback = false;
            DetectionMode fallbackMode = _configuration.DetectionMode;
            
            if (detectionResults.Count == 0)
            {
                if (_configuration.DetectionMode == DetectionMode.OcrWithHashFallback)
                {
                    LogDebug("OCR detection failed, attempting Hash fallback...");
                    EmbyCredits.Services.CreditsDetectionService.AddEpisodeStatusMessage(episodeId, "OCR failed, using Hash fallback");
                    shouldTryFallback = true;
                    fallbackMode = DetectionMode.HashOnly;
                }
                else if (_configuration.DetectionMode == DetectionMode.HashWithOcrFallback)
                {
                    LogDebug("Hash detection failed, attempting OCR fallback...");
                    EmbyCredits.Services.CreditsDetectionService.AddEpisodeStatusMessage(episodeId, "Hash failed, using OCR fallback");
                    shouldTryFallback = true;
                    fallbackMode = DetectionMode.OcrOnly;
                }
            }

            // Try fallback if needed
            if (shouldTryFallback)
            {
                var originalMode = _configuration.DetectionMode;
                try
                {
                    // Temporarily change mode for fallback
                    _configuration.DetectionMode = fallbackMode;
                    
                    // Re-initialize detection methods with fallback mode
                    // Dispose old detection methods before clearing
                    foreach (var method in _detectionMethods)
                    {
                        try { method?.Dispose(); } catch { }
                    }
                    _detectionMethods.Clear();
                    InitializeDetectionMethods();
                    
                    LogDebug($"Running fallback detection with mode: {fallbackMode}");
                    var (fallbackResults, fallbackErrors) = await RunAllDetectionMethods(videoPath, duration, episodeId, seriesId, seasonNumber, episodeNumber);
                    
                    if (fallbackResults.Count > 0)
                    {
                        LogDebug($"Fallback detection successful! Found {fallbackResults.Count} result(s)");
                        EmbyCredits.Services.CreditsDetectionService.AddEpisodeStatusMessage(episodeId, "Fallback method successful");
                        var fallbackResult = SelectByStrategy(fallbackResults);
                        LogDebug($"Selected timestamp: {FormatTime(fallbackResult.timestamp)} with confidence: {fallbackResult.confidence:F2}");
                        return (fallbackResult.timestamp, $"Fallback: {fallbackResult.reason}", fallbackResult.confidence);
                    }
                    else
                    {
                        LogDebug("Fallback detection also failed");
                        methodErrors = methodErrors.Concat(fallbackErrors).ToDictionary(x => x.Key, x => x.Value);
                    }
                }
                finally
                {
                    // Restore original mode
                    _configuration.DetectionMode = originalMode;
                    // Dispose detection methods from fallback mode
                    foreach (var method in _detectionMethods)
                    {
                        try { method?.Dispose(); } catch { }
                    }
                    _detectionMethods.Clear();
                    InitializeDetectionMethods();
                }
            }

            if (detectionResults.Count == 0)
            {
                _logger.Info("No credits detected by any method");
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
                
                var disabledMethods = _detectionMethods.Where(m => !m.IsEnabled).ToList();
                if (disabledMethods.Count > 0)
                {
                    LogDebug($"Disabled methods: {disabledMethods.Count}");
                    foreach (var method in disabledMethods)
                    {
                        LogDebug($"  • {method.MethodName}");
                    }
                }
                
                var successfulButNoResult = _detectionMethods.Where(m => m.IsEnabled).Count() - methodErrors.Count;
                if (successfulButNoResult > 0)
                {
                    LogDebug($"Methods that ran successfully but found no credits: {successfulButNoResult}");
                    LogDebug("  These methods completed without errors but did not detect any credit markers.");
                }
                
                LogDebug("=== END SUMMARY ===");
                
                var failureReason = methodErrors.Count > 0 
                    ? string.Join("; ", methodErrors.Values)
                    : "No credits detected by any enabled method";
                LogDebug($"Overall failure reason: {failureReason}");
                return (0, failureReason, 0);
            }

            LogDebug($"Found {detectionResults.Count} detection result(s)");
            var result = SelectByStrategy(detectionResults);
            LogDebug($"Selected timestamp: {FormatTime(result.timestamp)} with confidence: {result.confidence:F2}");

            return (result.timestamp, result.reason, result.confidence);
        }

        public void ClearCache()
        {
            _batchDetectionCache.Clear();
            _batchDetectionCache.TrimExcess(); // Release capacity to prevent memory retention
            if (_batchDetectionCache.Count > 0)
            {
                _batchDetectionCache.Clear();
                _batchDetectionCache.TrimExcess();
            }
            GC.Collect(1, GCCollectionMode.Optimized, false);
        }

        private async Task<(List<(string method, double timestamp, double confidence, int priority, string reason)> results, Dictionary<string, string> errors)> RunAllDetectionMethods(
            string videoPath, 
            double duration,
            string episodeId,
            string? seriesId = null,
            int? seasonNumber = null,
            int? episodeNumber = null)
        {
            var results = new List<(string method, double timestamp, double confidence, int priority, string reason)>();
            var errors = new Dictionary<string, string>();

            LogDebug($"Running detection methods for video (duration: {FormatTime(duration)})");
            LogDebug($"Total detection methods: {_detectionMethods.Count}");
            LogDebug($"Enabled methods: {string.Join(", ", _detectionMethods.Where(m => m.IsEnabled).Select(m => m.MethodName))}");

            foreach (var method in _detectionMethods)
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
                            LogDebug($"{method.MethodName} failed: {errorMsg}");
                        }
                        else
                        {
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

        private double GetMethodConfidence(string methodName)
        {
            return methodName switch
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

        private int GetMethodPriority(string methodName)
        {
            return methodName switch
            {
                "Video Pattern" => _configuration.VideoPatternPriority,
                "Audio Pattern" => _configuration.AudioPatternPriority,
                "Audio Silence" => _configuration.AudioSilencePriority,
                "Text Detection" => _configuration.TextDetectionPriority,
                "Scene Change" => _configuration.SceneChangePriority,
                "Black Screen" => _configuration.BlackScreenPriority,
                _ => 99
            };
        }

        private string FormatTime(double seconds)
        {
            var ts = TimeSpan.FromSeconds(seconds);
            return $"{(int)ts.TotalMinutes}:{ts.Seconds:D2}";
        }

        public void CancelDetection()
        {
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
                _batchDetectionCache.Clear();
                
                _disposed = true;
                
                GC.SuppressFinalize(this);
            }
        }
    }
}
