using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using EmbyCredits.Services.DetectionMethods;

namespace EmbyCredits.Services
{

    public class DetectionCoordinator
    {
        private readonly ILogger _logger;
        private readonly PluginConfiguration _configuration;
        private readonly List<IDetectionMethod> _detectionMethods;

        private readonly Dictionary<string, List<(string method, double timestamp)>> _batchDetectionCache;

        public DetectionCoordinator(ILogger logger, PluginConfiguration configuration)
        {
            _logger = logger;
            _configuration = configuration;
            _detectionMethods = new List<IDetectionMethod>();
            _batchDetectionCache = new Dictionary<string, List<(string method, double timestamp)>>();

            InitializeDetectionMethods();
        }

        private void InitializeDetectionMethods()
        {
            _detectionMethods.Add(new OcrDetection(_logger, _configuration));
        }

        public async Task<(double timestamp, string failureReason)> DetectCredits(string videoPath, double duration, string episodeId)
        {
            var (detectionResults, methodErrors) = await RunAllDetectionMethods(videoPath, duration, episodeId);

            if (detectionResults.Count == 0)
            {
                _logger.Info("No credits detected by any method");
                var failureReason = methodErrors.Count > 0 
                    ? string.Join("; ", methodErrors.Values)
                    : "No credits detected by any enabled method";
                return (0, failureReason);
            }

            return (SelectByStrategy(detectionResults), string.Empty);
        }

        public async Task<(double timestamp, string failureReason)> DetectCreditsWithComparison(
            Episode episode,
            double duration,
            List<Episode> comparisonEpisodes)
        {
            var crossEpisodeResults = await RunCrossEpisodeDetection(episode, duration, comparisonEpisodes);
            return (SelectByStrategy(crossEpisodeResults), string.Empty);
        }

        public async Task PreComputeBatchDetections(List<Episode> episodes, Func<double, Task> progressCallback)
        {
            _batchDetectionCache.Clear();

            var totalEpisodes = episodes.Count;
            var processedCount = 0;

            foreach (var episode in episodes)
            {
                processedCount++;
                await progressCallback((double)processedCount / totalEpisodes);

                var episodeId = episode.Id.ToString();
                _logger.Info($"Pre-computing detections for {episode.Name} ({processedCount}/{totalEpisodes})");

                var duration = episode.RunTimeTicks.HasValue 
                    ? episode.RunTimeTicks.Value / TimeSpan.TicksPerSecond 
                    : 0;

                if (duration <= 0) continue;

                var (detectionResults, _) = await RunAllDetectionMethods(episode.Path, duration, episodeId);
                _batchDetectionCache[episodeId] = detectionResults.Select(r => (r.method, r.timestamp)).ToList();

                _logger.Info($"  Cached {detectionResults.Count} detection results for {episode.Name}");
            }

            _logger.Info($"Batch pre-computation complete: {_batchDetectionCache.Count} episodes");
        }

        public double AnalyzeBatchDetectionResults(string episodeId, List<string> comparisonEpisodeIds)
        {
            if (!_batchDetectionCache.TryGetValue(episodeId, out var currentResults))
            {
                _logger.Warn($"No cached detection results found for episode {episodeId}");
                return 0;
            }

            if (currentResults.Count == 0)
            {
                _logger.Info("No detections found for episode");

                if (_configuration.EnableFailedEpisodeFallback)
                {
                    _logger.Info("Attempting fallback for failed episode in batch mode...");
                    var fallbackTimestamp = CalculateFallbackTimestampFromCache(episodeId, comparisonEpisodeIds);
                    if (fallbackTimestamp > 0)
                    {
                        _logger.Info($"Using fallback timestamp: {FormatTime(fallbackTimestamp)}");
                        return fallbackTimestamp;
                    }
                }

                return 0;
            }

            _logger.Info($"Analyzing {currentResults.Count} cached detection(s)");

            var results = new List<(string method, double timestamp, double confidence, int priority)>();

            foreach (var (method, timestamp) in currentResults)
            {
                int agreementCount = 0;
                var agreementMethods = new List<string> { method };

                foreach (var otherId in comparisonEpisodeIds)
                {
                    if (_batchDetectionCache.TryGetValue(otherId, out var otherResults))
                    {
                        foreach (var (otherMethod, otherTimestamp) in otherResults)
                        {
                            if (Math.Abs(timestamp - otherTimestamp) <= _configuration.CorrelationWindowSeconds)
                            {
                                agreementCount++;
                                if (!agreementMethods.Contains(otherMethod))
                                {
                                    agreementMethods.Add(otherMethod);
                                }
                                break; 
                            }
                        }
                    }
                }

                double baseConfidence = GetMethodConfidence(method);
                double agreementBonus = comparisonEpisodeIds.Count > 0 
                    ? (agreementCount / (double)comparisonEpisodeIds.Count) * 0.5 
                    : 0;
                double methodDiversityBonus = (agreementMethods.Count - 1) * 0.1;
                double totalConfidence = Math.Min(1.0, baseConfidence + agreementBonus + methodDiversityBonus);

                _logger.Info($"{method} at {FormatTime(timestamp)}: {agreementCount}/{comparisonEpisodeIds.Count} episodes agree (confidence: {totalConfidence:F2}, methods: {string.Join(", ", agreementMethods)})");

                results.Add((method, timestamp, totalConfidence, GetMethodPriority(method)));
            }

            if (results.Count > 0)
            {
                return AnalyzeWithCorrelationScoring(results);
            }

            return 0;
        }

        public void ClearCache()
        {
            _batchDetectionCache.Clear();
        }

        private double CalculateFallbackTimestampFromCache(string currentEpisodeId, List<string> comparisonEpisodeIds)
        {
            var successfulTimestamps = new List<double>();

            foreach (var episodeId in comparisonEpisodeIds)
            {
                if (_batchDetectionCache.TryGetValue(episodeId, out var results) && results.Count > 0)
                {
                    var avgTimestamp = results.Average(r => r.timestamp);
                    successfulTimestamps.Add(avgTimestamp);
                }
            }

            if (successfulTimestamps.Count == 0)
            {
                _logger.Info("No successful episodes in cache to calculate fallback from");
                return 0;
            }

            var successRate = (double)successfulTimestamps.Count / comparisonEpisodeIds.Count;
            _logger.Info($"Cache success rate: {successRate:P0} ({successfulTimestamps.Count}/{comparisonEpisodeIds.Count} episodes)");

            if (successRate < _configuration.MinimumSuccessRateForFallback)
            {
                _logger.Info($"Success rate {successRate:P0} is below minimum threshold {_configuration.MinimumSuccessRateForFallback:P0}");
                return 0;
            }

            successfulTimestamps.Sort();
            var medianTimestamp = successfulTimestamps.Count % 2 == 0
                ? (successfulTimestamps[successfulTimestamps.Count / 2 - 1] + successfulTimestamps[successfulTimestamps.Count / 2]) / 2
                : successfulTimestamps[successfulTimestamps.Count / 2];

            _logger.Info($"Calculated fallback timestamp from cache (median of {successfulTimestamps.Count} episodes): {FormatTime(medianTimestamp)}");
            return medianTimestamp;
        }

        private double CalculateFallbackTimestamp(Dictionary<string, List<(string method, double timestamp)>> episodeDetectionResults, string currentEpisodeId, int totalComparisonEpisodes)
        {
            var successfulTimestamps = new List<double>();

            foreach (var kvp in episodeDetectionResults)
            {
                if (kvp.Key == currentEpisodeId)
                    continue;

                if (kvp.Value.Count > 0)
                {
                    var avgTimestamp = kvp.Value.Average(r => r.timestamp);
                    successfulTimestamps.Add(avgTimestamp);
                }
            }

            if (successfulTimestamps.Count == 0)
            {
                _logger.Info("No successful episodes to calculate fallback from");
                return 0;
            }

            var successRate = (double)successfulTimestamps.Count / totalComparisonEpisodes;
            _logger.Info($"Success rate: {successRate:P0} ({successfulTimestamps.Count}/{totalComparisonEpisodes} episodes)");

            if (successRate < _configuration.MinimumSuccessRateForFallback)
            {
                _logger.Info($"Success rate {successRate:P0} is below minimum threshold {_configuration.MinimumSuccessRateForFallback:P0}");
                return 0;
            }

            successfulTimestamps.Sort();
            var medianTimestamp = successfulTimestamps.Count % 2 == 0
                ? (successfulTimestamps[successfulTimestamps.Count / 2 - 1] + successfulTimestamps[successfulTimestamps.Count / 2]) / 2
                : successfulTimestamps[successfulTimestamps.Count / 2];

            _logger.Info($"Calculated fallback timestamp (median of {successfulTimestamps.Count} successful episodes): {FormatTime(medianTimestamp)}");
            return medianTimestamp;
        }

        private async Task<(List<(string method, double timestamp, double confidence, int priority)> results, Dictionary<string, string> errors)> RunAllDetectionMethods(
            string videoPath, 
            double duration, 
            string episodeId)
        {
            var results = new List<(string method, double timestamp, double confidence, int priority)>();
            var errors = new Dictionary<string, string>();

            foreach (var method in _detectionMethods)
            {
                if (!method.IsEnabled)
                {
                    _logger.Debug($"Skipping {method.MethodName} (disabled)");
                    continue;
                }

                try
                {
                    var timestamp = await method.DetectCredits(videoPath, duration);
                    if (timestamp > 0)
                    {
                        results.Add((method.MethodName, timestamp, method.Confidence, method.Priority));
                        _logger.Info($"{method.MethodName} detection: {FormatTime(timestamp)}");
                    }
                    else
                    {
                        var errorMsg = method.GetLastError();
                        if (!string.IsNullOrEmpty(errorMsg))
                        {
                            errors[method.MethodName] = errorMsg;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.ErrorException($"Error running {method.MethodName}", ex);
                    errors[method.MethodName] = ex.Message;
                }
            }

            return (results, errors);
        }

        private async Task<List<(string method, double timestamp, double confidence, int priority)>> RunCrossEpisodeDetection(
            Episode episode,
            double duration,
            List<Episode> comparisonEpisodes)
        {
            var results = new List<(string method, double timestamp, double confidence, int priority)>();

            if (comparisonEpisodes.Count < 2)
            {
                _logger.Info("Not enough episodes for comparison. Running detection methods independently.");
                var (detectionResults, _) = await RunAllDetectionMethods(episode.Path, duration, episode.Id.ToString());
                return detectionResults;
            }

            _logger.Info($"Comparing with {comparisonEpisodes.Count} other episodes from the same season");

            var episodeDetectionResults = new Dictionary<string, List<(string method, double timestamp)>>();
            episodeDetectionResults[episode.Id.ToString()] = new List<(string method, double timestamp)>();

            var (currentResults, _) = await RunAllDetectionMethods(episode.Path, duration, episode.Id.ToString());
            episodeDetectionResults[episode.Id.ToString()].AddRange(currentResults.Select(r => (r.method, r.timestamp)));

            foreach (var compEpisode in comparisonEpisodes)
            {
                episodeDetectionResults[compEpisode.Id.ToString()] = new List<(string method, double timestamp)>();

                var compDuration = compEpisode.RunTimeTicks.HasValue 
                    ? compEpisode.RunTimeTicks.Value / TimeSpan.TicksPerSecond 
                    : 0;

                if (compDuration <= 0) continue;

                if (Plugin.Instance != null)
                {
                    Plugin.Progress.CurrentItem = $"{compEpisode.Series?.Name} - S{compEpisode.ParentIndexNumber:D2}E{compEpisode.IndexNumber:D2} - {compEpisode.Name} (comparison)";
                    Plugin.Progress.CurrentItemProgress = 0;
                }

                _logger.Debug($"Running detection methods on {compEpisode.Name}");
                var (compResults, _) = await RunAllDetectionMethods(compEpisode.Path, compDuration, compEpisode.Id.ToString());
                episodeDetectionResults[compEpisode.Id.ToString()].AddRange(compResults.Select(r => (r.method, r.timestamp)));
            }

            _logger.Info("Analyzing cross-episode detection patterns");

            var currentEpisodeResults = episodeDetectionResults[episode.Id.ToString()];

            if (currentEpisodeResults.Count == 0 && _configuration.EnableFailedEpisodeFallback)
            {
                _logger.Info("Current episode failed detection. Checking if fallback is possible...");
                var fallbackTimestamp = CalculateFallbackTimestamp(episodeDetectionResults, episode.Id.ToString(), comparisonEpisodes.Count);
                if (fallbackTimestamp > 0)
                {
                    _logger.Info($"Using fallback timestamp from successful episodes: {FormatTime(fallbackTimestamp)}");
                    var fallbackConfidence = 0.6;
                    results.Add(("OCR Detection (Fallback)", fallbackTimestamp, fallbackConfidence, 1));
                    return results;
                }
                else
                {
                    _logger.Info("Fallback not possible: insufficient successful episodes");
                    return results;
                }
            }

            foreach (var (method, timestamp) in currentEpisodeResults)
            {
                int agreementCount = 0;
                var agreementMethods = new List<string> { method };

                foreach (var compEpisode in comparisonEpisodes)
                {
                    if (episodeDetectionResults.TryGetValue(compEpisode.Id.ToString(), out var compResults))
                    {
                        foreach (var (compMethod, compTimestamp) in compResults)
                        {
                            if (Math.Abs(timestamp - compTimestamp) <= _configuration.CorrelationWindowSeconds)
                            {
                                agreementCount++;
                                if (!agreementMethods.Contains(compMethod))
                                {
                                    agreementMethods.Add(compMethod);
                                }
                                break;
                            }
                        }
                    }
                }

                double baseConfidence = GetMethodConfidence(method);
                double agreementBonus = comparisonEpisodes.Count > 0 
                    ? (agreementCount / (double)comparisonEpisodes.Count) * 0.5 
                    : 0;
                double methodDiversityBonus = (agreementMethods.Count - 1) * 0.1;
                double totalConfidence = Math.Min(1.0, baseConfidence + agreementBonus + methodDiversityBonus);

                _logger.Info($"{method} at {FormatTime(timestamp)}: {agreementCount}/{comparisonEpisodes.Count} episodes agree " +
                           $"(confidence: {totalConfidence:F2}, methods: {string.Join(", ", agreementMethods)})");

                results.Add((method, timestamp, totalConfidence, GetMethodPriority(method)));
            }

            return results;
        }

        private double AnalyzeWithCorrelationScoring(List<(string method, double timestamp, double confidence, int priority)> detectionResults)
        {
            if (detectionResults.Count == 0)
                return 0;

            var correlationWindow = _configuration.CorrelationWindowSeconds;
            var groupedResults = new List<(double timestamp, double combinedScore, List<string> methods)>();

            foreach (var result in detectionResults)
            {
                var existingGroup = groupedResults.FirstOrDefault(g => Math.Abs(g.timestamp - result.timestamp) <= correlationWindow);

                if (existingGroup.timestamp > 0)
                {
                    var index = groupedResults.IndexOf(existingGroup);
                    existingGroup.combinedScore += result.confidence;
                    existingGroup.methods.Add(result.method);
                    groupedResults[index] = existingGroup;
                }
                else
                {
                    groupedResults.Add((result.timestamp, result.confidence, new List<string> { result.method }));
                }
            }

            var bestGroup = groupedResults.OrderByDescending(g => g.combinedScore).First();
            var creditsStart = bestGroup.timestamp;

            _logger.Info($"Correlation scoring selected {FormatTime(creditsStart)} " +
                       $"(score: {bestGroup.combinedScore:F2}, methods: {string.Join(", ", bestGroup.methods)})");

            return creditsStart;
        }

        private double SelectByStrategy(List<(string method, double timestamp, double confidence, int priority)> detectionResults)
        {
            if (detectionResults.Count == 0)
                return 0;

            var strategy = _configuration.DetectionResultSelection ?? "CorrelationScoring";

            switch (strategy)
            {
                case "Earliest":
                    var earliest = detectionResults.OrderBy(r => r.timestamp).First();
                    _logger.Info($"Earliest mode selected {earliest.method} at {FormatTime(earliest.timestamp)}");
                    return earliest.timestamp;

                case "Latest":
                    var latest = detectionResults.OrderByDescending(r => r.timestamp).First();
                    _logger.Info($"Latest mode selected {latest.method} at {FormatTime(latest.timestamp)}");
                    return latest.timestamp;

                case "Average":
                    var average = detectionResults.Average(r => r.timestamp);
                    _logger.Info($"Average mode calculated {FormatTime(average)} from {detectionResults.Count} detections");
                    return average;

                case "Median":
                    var sorted = detectionResults.OrderBy(r => r.timestamp).ToList();
                    var median = sorted.Count % 2 == 0
                        ? (sorted[sorted.Count / 2 - 1].timestamp + sorted[sorted.Count / 2].timestamp) / 2
                        : sorted[sorted.Count / 2].timestamp;
                    _logger.Info($"Median mode calculated {FormatTime(median)} from {detectionResults.Count} detections");
                    return median;

                case "Priority":
                    var byPriority = detectionResults.OrderBy(r => r.priority).First();
                    _logger.Info($"Priority mode selected {byPriority.method} at {FormatTime(byPriority.timestamp)} (priority: {byPriority.priority})");
                    return byPriority.timestamp;

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
    }
}
