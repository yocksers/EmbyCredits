using MediaBrowser.Controller.Entities.TV;
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

        private DetectionRule? FindMatchingRule(Series series)
        {
            foreach (var rule in _baseConfig.DetectionRules)
            {
                if (rule.Tags != null && rule.Tags.Count > 0 && series.Tags != null && series.Tags.Length > 0)
                {
                    foreach (var ruleTag in rule.Tags)
                    {
                        foreach (var seriesTag in series.Tags)
                        {
                            if (string.Equals(ruleTag, seriesTag, StringComparison.OrdinalIgnoreCase))
                            {
                                _logger.Debug($"[RuleMatching] Series '{series.Name}' matched rule '{rule.Name}' via tag '{ruleTag}'");
                                return rule;
                            }
                        }
                    }
                }

                if (rule.Studios != null && rule.Studios.Count > 0 && series.Studios != null && series.Studios.Length > 0)
                {
                    foreach (var ruleStudio in rule.Studios)
                    {
                        foreach (var seriesStudio in series.Studios)
                        {
                            if (string.Equals(ruleStudio, seriesStudio, StringComparison.OrdinalIgnoreCase))
                            {
                                _logger.Debug($"[RuleMatching] Series '{series.Name}' matched rule '{rule.Name}' via studio '{ruleStudio}'");
                                return rule;
                            }
                        }
                    }
                }
            }

            return null;
        }

        private PluginConfiguration ApplyRuleToConfiguration(DetectionRule rule)
        {
            var effectiveConfig = new PluginConfiguration();
            CopyConfiguration(_baseConfig, effectiveConfig);

            _logger.Debug($"[RuleMatching] Applying rule '{rule.Name}': DetectionMode={rule.DetectionMode?.ToString() ?? "null"}");
            
            if (rule.DetectionMode.HasValue)
            {
                _logger.Info($"[RuleMatching] Overriding DetectionMode from {effectiveConfig.DetectionMode} to {rule.DetectionMode.Value}");
                effectiveConfig.DetectionMode = rule.DetectionMode.Value;
            }

            if (rule.OcrEngine.HasValue)
                effectiveConfig.OcrEngine = rule.OcrEngine.Value;

            if (rule.OcrSearchStartValue.HasValue)
            {
                effectiveConfig.OcrSearchStartValue = rule.OcrSearchStartValue.Value;
                effectiveConfig.OcrMinutesFromEnd = rule.OcrSearchStartValue.Value;
            }

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

            return effectiveConfig;
        }

        private void CopyConfiguration(PluginConfiguration source, PluginConfiguration dest)
        {
            dest.EnableAutoDetection = source.EnableAutoDetection;
            dest.EnableVideoPatternDetection = source.EnableVideoPatternDetection;
            dest.EnableBlackScreenDetection = source.EnableBlackScreenDetection;
            dest.EnableAudioSilenceDetection = source.EnableAudioSilenceDetection;
            dest.EnableAudioPatternDetection = source.EnableAudioPatternDetection;
            dest.EnableTextDetection = source.EnableTextDetection;
            dest.EnableSceneChangeDetection = source.EnableSceneChangeDetection;
            dest.EnableKeywordDetection = source.EnableKeywordDetection;
            dest.VideoPatternSensitivity = source.VideoPatternSensitivity;
            dest.VideoPatternWindowSize = source.VideoPatternWindowSize;
            dest.VideoPatternSearchStart = source.VideoPatternSearchStart;
            dest.AudioPatternSensitivity = source.AudioPatternSensitivity;
            dest.AudioPatternWindowSize = source.AudioPatternWindowSize;
            dest.AudioPatternSearchStart = source.AudioPatternSearchStart;
            dest.BlackScreenThreshold = source.BlackScreenThreshold;
            dest.BlackScreenMinDuration = source.BlackScreenMinDuration;
            dest.BlackScreenSearchStart = source.BlackScreenSearchStart;
            dest.TextDetectionThreshold = source.TextDetectionThreshold;
            dest.TextDetectionMinLines = source.TextDetectionMinLines;
            dest.TextDetectionSearchStart = source.TextDetectionSearchStart;
            dest.AudioSilenceThreshold = source.AudioSilenceThreshold;
            dest.AudioSilenceMinDuration = source.AudioSilenceMinDuration;
            dest.AudioSearchStartPosition = source.AudioSearchStartPosition;
            dest.SceneChangeThreshold = source.SceneChangeThreshold;
            dest.SceneChangeSearchStart = source.SceneChangeSearchStart;
            dest.SceneChangeMinDeviation = source.SceneChangeMinDeviation;
            dest.KeywordDetectionKeywords = source.KeywordDetectionKeywords;
            dest.KeywordDetectionSearchStart = source.KeywordDetectionSearchStart;
            dest.KeywordDetectionMinTextScore = source.KeywordDetectionMinTextScore;
            dest.KeywordDetectionRegionHeight = source.KeywordDetectionRegionHeight;
            dest.DetectionMode = source.DetectionMode;
            dest.OcrEngine = source.OcrEngine;
            dest.OcrEndpoint = source.OcrEndpoint;
            dest.OcrDetectionKeywords = source.OcrDetectionKeywords;
            dest.OcrLanguages = source.OcrLanguages;
            dest.OcrPageSegmentationMode = source.OcrPageSegmentationMode;
            dest.OcrEngineMode = source.OcrEngineMode;
            dest.OcrPreserveInterwordSpaces = source.OcrPreserveInterwordSpaces;
            dest.OcrEnableEpisodeComparison = source.OcrEnableEpisodeComparison;
            dest.OcrEpisodeComparisonTolerance = source.OcrEpisodeComparisonTolerance;
            dest.OcrEpisodeComparisonMinimumEpisodes = source.OcrEpisodeComparisonMinimumEpisodes;
            dest.OcrSearchStartUnit = source.OcrSearchStartUnit;
            dest.OcrSearchStartValue = source.OcrSearchStartValue;
            dest.OcrDetectionSearchStart = source.OcrDetectionSearchStart;
            dest.OcrMinutesFromEnd = source.OcrMinutesFromEnd;
            dest.OcrFrameRate = source.OcrFrameRate;
            dest.OcrMinimumMatches = source.OcrMinimumMatches;
            dest.OcrMaxFramesToProcess = source.OcrMaxFramesToProcess;
            dest.OcrMaxAnalysisDuration = source.OcrMaxAnalysisDuration;
            dest.OcrStopSecondsFromEnd = source.OcrStopSecondsFromEnd;
            dest.OcrJpegQuality = source.OcrJpegQuality;
            dest.OcrMaxResolutionHeight = source.OcrMaxResolutionHeight;
            dest.OcrDelayBetweenFramesMs = source.OcrDelayBetweenFramesMs;
            dest.OcrEnableParallelProcessing = source.OcrEnableParallelProcessing;
            dest.OcrParallelBatchSize = source.OcrParallelBatchSize;
            dest.OcrDelayBetweenBatchesMs = source.OcrDelayBetweenBatchesMs;
            dest.OcrEnableSmartFrameSkipping = source.OcrEnableSmartFrameSkipping;
            dest.OcrConsecutiveMatchesForEarlyStop = source.OcrConsecutiveMatchesForEarlyStop;
            dest.OcrMinimumConfidence = source.OcrMinimumConfidence;
            dest.OcrEnableCharacterDensityDetection = source.OcrEnableCharacterDensityDetection;
            dest.OcrCharacterDensityThreshold = source.OcrCharacterDensityThreshold;
            dest.OcrCharacterDensityConsecutiveFrames = source.OcrCharacterDensityConsecutiveFrames;
            dest.OcrCharacterDensityPrimaryMethod = source.OcrCharacterDensityPrimaryMethod;
            dest.OcrDensityRequireKeyword = source.OcrDensityRequireKeyword;
            dest.OcrDensityKeywordWindowSeconds = source.OcrDensityKeywordWindowSeconds;
            dest.OcrDensityRequireTemporalConsistency = source.OcrDensityRequireTemporalConsistency;
            dest.OcrDensityMinimumDurationSeconds = source.OcrDensityMinimumDurationSeconds;
            dest.OcrDensityRequireStyleConsistency = source.OcrDensityRequireStyleConsistency;
            dest.OcrDensityStyleConsistencyThreshold = source.OcrDensityStyleConsistencyThreshold;
            dest.OcrRetryAttempts = source.OcrRetryAttempts;
            dest.OcrRetryDelayMs = source.OcrRetryDelayMs;
            dest.OcrFfmpegPreInputArgs = source.OcrFfmpegPreInputArgs;
            dest.OcrFfmpegThreads = source.OcrFfmpegThreads;
            dest.OcrFfmpegFilterThreads = source.OcrFfmpegFilterThreads;
            dest.OcrEnableHardwareAcceleration = source.OcrEnableHardwareAcceleration;
            dest.OcrHardwareAccelerationType = source.OcrHardwareAccelerationType;
            dest.OcrHardwareDevice = source.OcrHardwareDevice;
            dest.OcrUseHardwareOutputFormat = source.OcrUseHardwareOutputFormat;
            dest.OcrUseHardwareFilters = source.OcrUseHardwareFilters;
            dest.OcrUseDirectMemoryPipeline = source.OcrUseDirectMemoryPipeline;
            dest.OcrEnableImagePreprocessing = source.OcrEnableImagePreprocessing;
            dest.OcrContrastEnhancement = source.OcrContrastEnhancement;
            dest.OcrBrightnessAdjustment = source.OcrBrightnessAdjustment;
            dest.OcrEnableSharpening = source.OcrEnableSharpening;
            dest.OcrSharpenAmount = source.OcrSharpenAmount;
            dest.OcrEnableRoiDetection = source.OcrEnableRoiDetection;
            dest.OcrRoiRegion = source.OcrRoiRegion;
            dest.OcrEnableFuzzyMatching = source.OcrEnableFuzzyMatching;
            dest.OcrFuzzyMatchMaxDistance = source.OcrFuzzyMatchMaxDistance;
            dest.OcrEnableScrollingDetection = source.OcrEnableScrollingDetection;
            dest.OcrScrollingMinFrames = source.OcrScrollingMinFrames;
            dest.OcrScrollingOverlapThreshold = source.OcrScrollingOverlapThreshold;
            dest.OcrEnableAdaptiveFrameRate = source.OcrEnableAdaptiveFrameRate;
            dest.OcrAdaptiveFrameRateMin = source.OcrAdaptiveFrameRateMin;
            dest.OcrEnableCreditStructureDetection = source.OcrEnableCreditStructureDetection;
            dest.OcrMinimumStructureLines = source.OcrMinimumStructureLines;
            dest.UseCorrelationScoring = source.UseCorrelationScoring;
            dest.CorrelationWindowSeconds = source.CorrelationWindowSeconds;
            dest.DetectionResultSelection = source.DetectionResultSelection;
            dest.VideoPatternPriority = source.VideoPatternPriority;
            dest.AudioPatternPriority = source.AudioPatternPriority;
            dest.BlackScreenPriority = source.BlackScreenPriority;
            dest.AudioSilencePriority = source.AudioSilencePriority;
            dest.TextDetectionPriority = source.TextDetectionPriority;
            dest.SceneChangePriority = source.SceneChangePriority;
            dest.KeywordDetectionPriority = source.KeywordDetectionPriority;
            dest.OcrDetectionPriority = source.OcrDetectionPriority;
            dest.EnableCombinedHeuristic = source.EnableCombinedHeuristic;
            dest.CombinedHeuristicPriority = source.CombinedHeuristicPriority;
            dest.CombinedMinutesFromEnd = source.CombinedMinutesFromEnd;
            dest.CombinedSearchStart = source.CombinedSearchStart;
            dest.CombinedFrameRate = source.CombinedFrameRate;
            dest.CombinedUseKeywords = source.CombinedUseKeywords;
            dest.CombinedUseTextDensity = source.CombinedUseTextDensity;
            dest.CombinedUseDarkness = source.CombinedUseDarkness;
            dest.CombinedKeywordWeight = source.CombinedKeywordWeight;
            dest.CombinedTextDensityWeight = source.CombinedTextDensityWeight;
            dest.CombinedDarknessWeight = source.CombinedDarknessWeight;
            dest.CombinedScoreThreshold = source.CombinedScoreThreshold;
            dest.CombinedMinSustainedSeconds = source.CombinedMinSustainedSeconds;
            dest.CpuUsageLimit = source.CpuUsageLimit;
            dest.CpuThrottleDelayMs = source.CpuThrottleDelayMs;
            dest.DelayBetweenEpisodesMs = source.DelayBetweenEpisodesMs;
            dest.LowerThreadPriority = source.LowerThreadPriority;
            dest.LowerProcessPriority = source.LowerProcessPriority;
            dest.TempFolderPath = source.TempFolderPath;
            dest.EnableDetailedLogging = source.EnableDetailedLogging;
            dest.EnableLogToFile = source.EnableLogToFile;
            dest.LogFileFolderPath = source.LogFileFolderPath;
            dest.LibraryIds = source.LibraryIds;
            dest.ScheduledTaskOnlyProcessMissing = source.ScheduledTaskOnlyProcessMissing;
            dest.BackupImportOverwriteExisting = source.BackupImportOverwriteExisting;
            dest.ManualSkipExistingMarkers = source.ManualSkipExistingMarkers;
            dest.EnableScheduledTaskNotifications = source.EnableScheduledTaskNotifications;
            dest.EnableAutoDetectionNotifications = source.EnableAutoDetectionNotifications;
            dest.NotifyOnSuccessOnly = source.NotifyOnSuccessOnly;
            dest.MinimumEpisodesForNotification = source.MinimumEpisodesForNotification;
            dest.PreventConcurrentPluginProcessing = source.PreventConcurrentPluginProcessing;
            dest.MaxScheduledBackups = source.MaxScheduledBackups;
            dest.BackupFolderPath = source.BackupFolderPath;
            dest.EnableThumbnailGeneration = source.EnableThumbnailGeneration;
            dest.ThumbnailWidth = source.ThumbnailWidth;
            dest.ThumbnailQuality = source.ThumbnailQuality;
            dest.EnableAnimeDetection = source.EnableAnimeDetection;
            dest.AnimeDetectionMethod = source.AnimeDetectionMethod;
            dest.BlackFrameMinimumPercentage = source.BlackFrameMinimumPercentage;
            dest.BlackFrameThreshold = source.BlackFrameThreshold;
            dest.ChromaprintDetectionPriority = source.ChromaprintDetectionPriority;
            dest.ChromaprintUseAudioFingerprinting = source.ChromaprintUseAudioFingerprinting;
            dest.ChromaprintFingerprintDuration = source.ChromaprintFingerprintDuration;
            dest.ChromaprintFingerprintSimilarityThreshold = source.ChromaprintFingerprintSimilarityThreshold;
            dest.ChromaprintEnableEpisodeComparison = source.ChromaprintEnableEpisodeComparison;
            dest.ChromaprintEpisodeComparisonTolerance = source.ChromaprintEpisodeComparisonTolerance;
            dest.ChromaprintEpisodeComparisonMinimumEpisodes = source.ChromaprintEpisodeComparisonMinimumEpisodes;
            dest.ChromaprintMinDuration = source.ChromaprintMinDuration;
            dest.ChromaprintMaxDuration = source.ChromaprintMaxDuration;
            dest.ChromaprintSimilarityThreshold = source.ChromaprintSimilarityThreshold;
            dest.ChromaprintMinEpisodeCount = source.ChromaprintMinEpisodeCount;
            dest.ChromaprintAnalysisPercent = source.ChromaprintAnalysisPercent;
            dest.ChromaprintBlackFrameThreshold = source.ChromaprintBlackFrameThreshold;
            dest.ChromaprintBlackFrameMinDuration = source.ChromaprintBlackFrameMinDuration;
            dest.ChromaprintUseSilenceDetection = source.ChromaprintUseSilenceDetection;
            dest.ChromaprintSilenceThreshold = source.ChromaprintSilenceThreshold;
            dest.ChromaprintSilenceMinDuration = source.ChromaprintSilenceMinDuration;
            dest.ChromaprintSilenceSearchWindow = source.ChromaprintSilenceSearchWindow;
            dest.ChromaprintMinConfidence = source.ChromaprintMinConfidence;
            dest.ChromaprintStopSecondsFromEnd = source.ChromaprintStopSecondsFromEnd;
            dest.ChromaprintLowerProcessPriority = source.ChromaprintLowerProcessPriority;
            dest.ChromaprintFfmpegThreads = source.ChromaprintFfmpegThreads;
            dest.ChromaprintDelayBetweenOperationsMs = source.ChromaprintDelayBetweenOperationsMs;
            dest.DetectionRules = source.DetectionRules;
        }
    }
}
