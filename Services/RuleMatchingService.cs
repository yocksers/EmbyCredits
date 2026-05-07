using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Logging;
using System;
using System.Collections.Generic;
using System.Linq;

namespace EmbyCredits.Services
{
    public class RuleMatchingService
    {
        private readonly ILogger _logger;
        private readonly PluginConfiguration _baseConfig;

        public RuleMatchingService(ILogger logger, PluginConfiguration baseConfig)
        {
            _logger = logger;
            _baseConfig = baseConfig;
        }

        public PluginConfiguration GetEffectiveConfiguration(Episode episode)
        {
            if (_baseConfig.DetectionRules == null || _baseConfig.DetectionRules.Count == 0)
            {
                return _baseConfig;
            }

            var series = episode?.Series;
            if (series == null)
            {
                return _baseConfig;
            }

            var matchingRule = FindMatchingRule(series);
            if (matchingRule == null)
            {
                return _baseConfig;
            }

            _logger.Info($"[RuleMatching] Rule '{matchingRule.Name}' matched for series '{series.Name}'");
            return ApplyRuleToConfiguration(matchingRule);
        }

        public PluginConfiguration GetEffectiveConfiguration(Series series)
        {
            if (_baseConfig.DetectionRules == null || _baseConfig.DetectionRules.Count == 0)
            {
                return _baseConfig;
            }

            if (series == null)
            {
                return _baseConfig;
            }

            var matchingRule = FindMatchingRule(series);
            if (matchingRule == null)
            {
                return _baseConfig;
            }

            _logger.Info($"[RuleMatching] Rule '{matchingRule.Name}' matched for series '{series.Name}'");
            return ApplyRuleToConfiguration(matchingRule);
        }

        public string? GetMatchingRuleName(Episode episode)
        {
            if (_baseConfig.DetectionRules == null || _baseConfig.DetectionRules.Count == 0)
                return null;
            var series = episode?.Series;
            if (series == null) return null;
            return FindMatchingRule(series)?.Name;
        }

        // Specificity scores — higher wins regardless of rule list order.
        // Tiebreaker is list order (earlier rule wins).
        private const int SpecificitySeriesName = 30;
        private const int SpecificityTag        = 20;
        private const int SpecificityStudio     = 20;
        private const int SpecificityLibrary    = 10;

        private DetectionRule? FindMatchingRule(Series series)
        {
            DetectionRule? bestRule = null;
            int bestScore = -1;
            string? bestReason = null;

            var libraryManager = Plugin.Instance?.LibraryManager;

            for (int i = 0; i < _baseConfig.DetectionRules.Count; i++)
            {
                var rule = _baseConfig.DetectionRules[i];

                string? matchReason = null;
                int matchScore = -1;

                if (rule.SeriesNames != null && rule.SeriesNames.Count > 0)
                {
                    foreach (var ruleName in rule.SeriesNames)
                    {
                        if (string.Equals(ruleName, series.Name, StringComparison.OrdinalIgnoreCase))
                        {
                            matchReason = $"series name '{ruleName}'";
                            matchScore = SpecificitySeriesName;
                            break;
                        }
                    }
                }

                if (matchScore < SpecificityTag &&
                    rule.Tags != null && rule.Tags.Count > 0 &&
                    series.Tags != null && series.Tags.Length > 0)
                {
                    foreach (var ruleTag in rule.Tags)
                    {
                        foreach (var seriesTag in series.Tags)
                        {
                            if (string.Equals(ruleTag, seriesTag, StringComparison.OrdinalIgnoreCase))
                            {
                                matchReason = $"tag '{ruleTag}'";
                                matchScore = SpecificityTag;
                                break;
                            }
                        }
                        if (matchScore >= SpecificityTag) break;
                    }
                }

                if (matchScore < SpecificityStudio &&
                    rule.Studios != null && rule.Studios.Count > 0 &&
                    series.Studios != null && series.Studios.Length > 0)
                {
                    foreach (var ruleStudio in rule.Studios)
                    {
                        foreach (var seriesStudio in series.Studios)
                        {
                            if (string.Equals(ruleStudio, seriesStudio, StringComparison.OrdinalIgnoreCase))
                            {
                                matchReason = $"studio '{ruleStudio}'";
                                matchScore = SpecificityStudio;
                                break;
                            }
                        }
                        if (matchScore >= SpecificityStudio) break;
                    }
                }

                bool hasPrimaryMatchers = (rule.SeriesNames != null && rule.SeriesNames.Count > 0) ||
                                          (rule.Tags != null && rule.Tags.Count > 0) ||
                                          (rule.Studios != null && rule.Studios.Count > 0);

                if (rule.LibraryIds != null && rule.LibraryIds.Count > 0 &&
                    libraryManager != null && !string.IsNullOrEmpty(series.Path))
                {
                    if (hasPrimaryMatchers)
                    {
                        // Library acts as a scope constraint: a primary match is discarded
                        // if the series is not in one of the rule's configured libraries.
                        if (matchScore > -1)
                        {
                            var matchedLib = FindLibraryNameForPath(series.Path, rule.LibraryIds, libraryManager);
                            if (matchedLib == null)
                            {
                                matchScore = -1;
                                matchReason = null;
                            }
                        }
                    }
                    else
                    {
                        // No primary matchers: library is the sole match criterion.
                        if (matchScore < SpecificityLibrary)
                        {
                            var matchedLib = FindLibraryNameForPath(series.Path, rule.LibraryIds, libraryManager);
                            if (matchedLib != null)
                            {
                                matchReason = $"library '{matchedLib}'";
                                matchScore = SpecificityLibrary;
                            }
                        }
                    }
                }

                // Accept this rule if it scores strictly higher than the current best.
                // Equal scores preserve list order (earlier rule already stored as best).
                if (matchScore > bestScore)
                {
                    bestScore = matchScore;
                    bestRule = rule;
                    bestReason = matchReason;
                }
            }

            if (bestRule != null)
                _logger.Debug($"[RuleMatching] Series '{series.Name}' matched rule '{bestRule.Name}' via {bestReason} (score={bestScore})");

            return bestRule;
        }

        private PluginConfiguration ApplyRuleToConfiguration(DetectionRule rule)
        {
            var effectiveConfig = _baseConfig.ShallowClone();

            _logger.Debug($"[RuleMatching] Applying rule '{rule.Name}': DetectionMode={rule.DetectionMode?.ToString() ?? "null"}");
            
            if (rule.DetectionMode.HasValue)
            {
                _logger.Info($"[RuleMatching] Overriding DetectionMode from {effectiveConfig.DetectionMode} to {rule.DetectionMode.Value}");
                effectiveConfig.DetectionMode = rule.DetectionMode.Value;
            }

            if (rule.OcrEngine.HasValue)
                effectiveConfig.OcrEngine = rule.OcrEngine.Value;

            if (rule.OcrSearchStartValue.HasValue)
                effectiveConfig.OcrSearchStartValue = rule.OcrSearchStartValue.Value;

            if (rule.OcrMinutesFromEnd.HasValue)
                effectiveConfig.OcrMinutesFromEnd = rule.OcrMinutesFromEnd.Value;

            if (rule.OcrFrameRate.HasValue)
                effectiveConfig.OcrFrameRate = rule.OcrFrameRate.Value;

            if (rule.OcrMinimumMatches.HasValue)
                effectiveConfig.OcrMinimumMatches = rule.OcrMinimumMatches.Value;

            if (rule.OcrMaxFramesToProcess.HasValue)
                effectiveConfig.OcrMaxFramesToProcess = rule.OcrMaxFramesToProcess.Value;

            if (rule.OcrMaxAnalysisDuration.HasValue)
                effectiveConfig.OcrMaxAnalysisDuration = rule.OcrMaxAnalysisDuration.Value;

            if (rule.OcrStopSecondsFromEnd.HasValue)
                effectiveConfig.OcrStopSecondsFromEnd = rule.OcrStopSecondsFromEnd.Value;

            if (!string.IsNullOrEmpty(rule.OcrDetectionKeywords))
                effectiveConfig.OcrDetectionKeywords = rule.OcrDetectionKeywords;

            if (rule.OcrEpisodeComparisonTolerance.HasValue)
                effectiveConfig.OcrEpisodeComparisonTolerance = rule.OcrEpisodeComparisonTolerance.Value;

            if (rule.OcrEnableEpisodeComparison.HasValue)
                effectiveConfig.OcrEnableEpisodeComparison = rule.OcrEnableEpisodeComparison.Value;

            if (rule.OcrEpisodeComparisonMinimumEpisodes.HasValue)
                effectiveConfig.OcrEpisodeComparisonMinimumEpisodes = rule.OcrEpisodeComparisonMinimumEpisodes.Value;

            if (rule.EnableAnimeDetection.HasValue)
                effectiveConfig.EnableAnimeDetection = rule.EnableAnimeDetection.Value;

            if (rule.AnimeDetectionMethod.HasValue)
                effectiveConfig.AnimeDetectionMethod = rule.AnimeDetectionMethod.Value;

            if (rule.BlackFrameMinimumPercentage.HasValue)
                effectiveConfig.BlackFrameMinimumPercentage = rule.BlackFrameMinimumPercentage.Value;

            if (rule.BlackFrameThreshold.HasValue)
                effectiveConfig.BlackFrameThreshold = rule.BlackFrameThreshold.Value;

            if (rule.BlackFrameMinimumDensity.HasValue)
                effectiveConfig.BlackFrameMinimumDensity = rule.BlackFrameMinimumDensity.Value;

            if (rule.BlackFrameMaxCreditsDuration.HasValue)
                effectiveConfig.BlackFrameMaxCreditsDuration = rule.BlackFrameMaxCreditsDuration.Value;

            if (rule.BlackFrameMaxSceneMergeGap.HasValue)
                effectiveConfig.BlackFrameMaxSceneMergeGap = rule.BlackFrameMaxSceneMergeGap.Value;

            if (rule.BlackFrameScanAllFrames.HasValue)
                effectiveConfig.BlackFrameScanAllFrames = rule.BlackFrameScanAllFrames.Value;

            if (rule.BlackFrameAutoFallbackToAllFrames.HasValue)
                effectiveConfig.BlackFrameAutoFallbackToAllFrames = rule.BlackFrameAutoFallbackToAllFrames.Value;

            if (rule.BlackFrameRefineCreditsBoundary.HasValue)
                effectiveConfig.BlackFrameRefineCreditsBoundary = rule.BlackFrameRefineCreditsBoundary.Value;

            if (rule.OcrEnableCharacterDensityDetection.HasValue)
                effectiveConfig.OcrEnableCharacterDensityDetection = rule.OcrEnableCharacterDensityDetection.Value;

            if (rule.OcrCharacterDensityThreshold.HasValue)
                effectiveConfig.OcrCharacterDensityThreshold = rule.OcrCharacterDensityThreshold.Value;

            if (rule.OcrCharacterDensityConsecutiveFrames.HasValue)
                effectiveConfig.OcrCharacterDensityConsecutiveFrames = rule.OcrCharacterDensityConsecutiveFrames.Value;

            if (rule.OcrCharacterDensityPrimaryMethod.HasValue)
                effectiveConfig.OcrCharacterDensityPrimaryMethod = rule.OcrCharacterDensityPrimaryMethod.Value;

            if (rule.OcrDensityRequireKeyword.HasValue)
                effectiveConfig.OcrDensityRequireKeyword = rule.OcrDensityRequireKeyword.Value;

            if (rule.OcrDensityKeywordWindowSeconds.HasValue)
                effectiveConfig.OcrDensityKeywordWindowSeconds = rule.OcrDensityKeywordWindowSeconds.Value;

            if (rule.OcrDensityRequireTemporalConsistency.HasValue)
                effectiveConfig.OcrDensityRequireTemporalConsistency = rule.OcrDensityRequireTemporalConsistency.Value;

            if (rule.OcrDensityMinimumDurationSeconds.HasValue)
                effectiveConfig.OcrDensityMinimumDurationSeconds = rule.OcrDensityMinimumDurationSeconds.Value;

            if (rule.OcrDensityRequireStyleConsistency.HasValue)
                effectiveConfig.OcrDensityRequireStyleConsistency = rule.OcrDensityRequireStyleConsistency.Value;

            if (rule.OcrDensityStyleConsistencyThreshold.HasValue)
                effectiveConfig.OcrDensityStyleConsistencyThreshold = rule.OcrDensityStyleConsistencyThreshold.Value;

            if (rule.DisableDetection.HasValue)
                effectiveConfig.DisableDetection = rule.DisableDetection.Value;

            if (rule.ChromaprintAnalysisPercent.HasValue)
                effectiveConfig.ChromaprintAnalysisPercent = rule.ChromaprintAnalysisPercent.Value;

            if (rule.ChromaprintMinDuration.HasValue)
                effectiveConfig.ChromaprintMinDuration = rule.ChromaprintMinDuration.Value;

            if (rule.ChromaprintMaxDuration.HasValue)
                effectiveConfig.ChromaprintMaxDuration = rule.ChromaprintMaxDuration.Value;

            if (rule.ChromaprintFingerprintDuration.HasValue)
                effectiveConfig.ChromaprintFingerprintDuration = rule.ChromaprintFingerprintDuration.Value;

            if (rule.ChromaprintSimilarityThreshold.HasValue)
                effectiveConfig.ChromaprintSimilarityThreshold = rule.ChromaprintSimilarityThreshold.Value;

            if (rule.ChromaprintEnableEpisodeComparison.HasValue)
                effectiveConfig.ChromaprintEnableEpisodeComparison = rule.ChromaprintEnableEpisodeComparison.Value;

            if (rule.ChromaprintEpisodeComparisonTolerance.HasValue)
                effectiveConfig.ChromaprintEpisodeComparisonTolerance = rule.ChromaprintEpisodeComparisonTolerance.Value;

            if (rule.ChromaprintEpisodeComparisonMinimumEpisodes.HasValue)
                effectiveConfig.ChromaprintEpisodeComparisonMinimumEpisodes = rule.ChromaprintEpisodeComparisonMinimumEpisodes.Value;

            if (rule.ChromaprintStopSecondsFromEnd.HasValue)
                effectiveConfig.ChromaprintStopSecondsFromEnd = rule.ChromaprintStopSecondsFromEnd.Value;

            if (rule.TimestampOffsetSeconds.HasValue)
                effectiveConfig.TimestampOffsetSeconds = rule.TimestampOffsetSeconds.Value;

            if (rule.BlackFrameMinDuration.HasValue)
                effectiveConfig.BlackFrameMinDuration = rule.BlackFrameMinDuration.Value;

            if (!string.IsNullOrEmpty(rule.OcrLanguages))
                effectiveConfig.OcrLanguages = rule.OcrLanguages;

            if (rule.OcrPageSegmentationMode.HasValue)
                effectiveConfig.OcrPageSegmentationMode = rule.OcrPageSegmentationMode.Value;

            if (rule.OcrEngineMode.HasValue)
                effectiveConfig.OcrEngineMode = rule.OcrEngineMode.Value;

            if (rule.OcrPreserveInterwordSpaces.HasValue)
                effectiveConfig.OcrPreserveInterwordSpaces = rule.OcrPreserveInterwordSpaces.Value;

            if (rule.OcrMinimumConfidence.HasValue)
                effectiveConfig.OcrMinimumConfidence = rule.OcrMinimumConfidence.Value;

            if (rule.OcrEnableSmartFrameSkipping.HasValue)
                effectiveConfig.OcrEnableSmartFrameSkipping = rule.OcrEnableSmartFrameSkipping.Value;

            if (rule.OcrConsecutiveMatchesForEarlyStop.HasValue)
                effectiveConfig.OcrConsecutiveMatchesForEarlyStop = rule.OcrConsecutiveMatchesForEarlyStop.Value;

            if (rule.OcrEnableImagePreprocessing.HasValue)
                effectiveConfig.OcrEnableImagePreprocessing = rule.OcrEnableImagePreprocessing.Value;

            if (rule.OcrContrastEnhancement.HasValue)
                effectiveConfig.OcrContrastEnhancement = rule.OcrContrastEnhancement.Value;

            if (rule.OcrBrightnessAdjustment.HasValue)
                effectiveConfig.OcrBrightnessAdjustment = rule.OcrBrightnessAdjustment.Value;

            if (rule.OcrEnableSharpening.HasValue)
                effectiveConfig.OcrEnableSharpening = rule.OcrEnableSharpening.Value;

            if (rule.OcrSharpenAmount.HasValue)
                effectiveConfig.OcrSharpenAmount = rule.OcrSharpenAmount.Value;

            if (rule.OcrEnableRoiDetection.HasValue)
                effectiveConfig.OcrEnableRoiDetection = rule.OcrEnableRoiDetection.Value;

            if (!string.IsNullOrEmpty(rule.OcrRoiRegion))
                effectiveConfig.OcrRoiRegion = rule.OcrRoiRegion;

            if (rule.OcrEnableFuzzyMatching.HasValue)
                effectiveConfig.OcrEnableFuzzyMatching = rule.OcrEnableFuzzyMatching.Value;

            if (rule.OcrFuzzyMatchMaxDistance.HasValue)
                effectiveConfig.OcrFuzzyMatchMaxDistance = rule.OcrFuzzyMatchMaxDistance.Value;

            if (rule.OcrEnableScrollingDetection.HasValue)
                effectiveConfig.OcrEnableScrollingDetection = rule.OcrEnableScrollingDetection.Value;

            if (rule.OcrScrollingMinFrames.HasValue)
                effectiveConfig.OcrScrollingMinFrames = rule.OcrScrollingMinFrames.Value;

            if (rule.OcrScrollingOverlapThreshold.HasValue)
                effectiveConfig.OcrScrollingOverlapThreshold = rule.OcrScrollingOverlapThreshold.Value;

            if (rule.OcrEnableAdaptiveFrameRate.HasValue)
                effectiveConfig.OcrEnableAdaptiveFrameRate = rule.OcrEnableAdaptiveFrameRate.Value;

            if (rule.OcrAdaptiveFrameRateMin.HasValue)
                effectiveConfig.OcrAdaptiveFrameRateMin = rule.OcrAdaptiveFrameRateMin.Value;

            if (rule.OcrEnableCreditStructureDetection.HasValue)
                effectiveConfig.OcrEnableCreditStructureDetection = rule.OcrEnableCreditStructureDetection.Value;

            if (rule.OcrMinimumStructureLines.HasValue)
                effectiveConfig.OcrMinimumStructureLines = rule.OcrMinimumStructureLines.Value;

            return effectiveConfig;
        }

        /// <summary>
        /// Finds the name of the virtual library (CollectionFolder) whose physical Locations contain
        /// the given path, and whose ID matches one of the configured library IDs.
        /// Returns null if no match is found.
        /// Supports both GUID-format IDs (current) and numeric InternalId strings (legacy).
        /// </summary>
        internal static string? FindLibraryNameForPath(string path, IEnumerable<string> configuredIds, ILibraryManager libraryManager)
        {
            var idSet = new HashSet<string>(configuredIds, StringComparer.OrdinalIgnoreCase);
            foreach (var vf in libraryManager.GetVirtualFolders())
            {
                bool pathMatches = vf.Locations != null && vf.Locations.Any(loc =>
                    !string.IsNullOrEmpty(loc) &&
                    path.StartsWith(loc, StringComparison.OrdinalIgnoreCase));

                if (!pathMatches) continue;

                if (!string.IsNullOrEmpty(vf.ItemId))
                {
                    if (idSet.Contains(vf.ItemId))
                        return vf.Name;

                    // Backward compat: config may have stored the numeric InternalId
                    if (Guid.TryParse(vf.ItemId, out var vfGuid))
                    {
                        var vfItem = libraryManager.GetItemById(vfGuid);
                        if (vfItem != null && idSet.Contains(vfItem.InternalId.ToString()))
                            return vf.Name;
                    }
                }
            }
            return null;
        }

    }
}
