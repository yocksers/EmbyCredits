using MediaBrowser.Model.Plugins;
using System;
using System.Collections.Generic;

namespace EmbyCredits
{
    public enum DetectionMode
    {
        OcrOnly,
        HashOnly,
        OcrWithHashFallback,
        HashWithOcrFallback,
        BlackFrameOnly
    }

    public enum AnimeDetectionMethod
    {
        BlackFrame,
        Ocr
    }

    public enum OcrEngine
    {
        Tesseract,
        PaddleOCR
    }

    public class DetectionRule
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Name { get; set; } = "";
        public List<string> Tags { get; set; } = new List<string>();
        public List<string> Studios { get; set; } = new List<string>();
        public List<string> SeriesNames { get; set; } = new List<string>();
        public List<string> LibraryIds { get; set; } = new List<string>();
        
        public DetectionMode? DetectionMode { get; set; }
        public OcrEngine? OcrEngine { get; set; }
        public double? OcrSearchStartValue { get; set; }
        public double? OcrMinutesFromEnd { get; set; }
        public double? OcrFrameRate { get; set; }
        public int? OcrMinimumMatches { get; set; }
        public int? OcrMaxFramesToProcess { get; set; }
        public double? OcrMaxAnalysisDuration { get; set; }
        public double? OcrStopSecondsFromEnd { get; set; }
        public string? OcrDetectionKeywords { get; set; }
        public double? OcrEpisodeComparisonTolerance { get; set; }
        public bool? OcrEnableEpisodeComparison { get; set; }
        public int? OcrEpisodeComparisonMinimumEpisodes { get; set; }
        public bool? EnableAnimeDetection { get; set; }
        public AnimeDetectionMethod? AnimeDetectionMethod { get; set; }
        public int? BlackFrameMinimumPercentage { get; set; }
        public int? BlackFrameThreshold { get; set; }
        public double? BlackFrameMinimumDensity { get; set; }
        public double? BlackFrameMaxCreditsDuration { get; set; }
        public double? BlackFrameMaxSceneMergeGap { get; set; }
        public bool? BlackFrameScanAllFrames { get; set; }
        public bool? BlackFrameAutoFallbackToAllFrames { get; set; }
        public bool? BlackFrameRefineCreditsBoundary { get; set; }
        public bool? OcrEnableCharacterDensityDetection { get; set; }
        public int? OcrCharacterDensityThreshold { get; set; }
        public int? OcrCharacterDensityConsecutiveFrames { get; set; }
        public bool? OcrCharacterDensityPrimaryMethod { get; set; }
        public bool? OcrDensityRequireKeyword { get; set; }
        public double? OcrDensityKeywordWindowSeconds { get; set; }
        public bool? OcrDensityRequireTemporalConsistency { get; set; }
        public double? OcrDensityMinimumDurationSeconds { get; set; }
        public bool? OcrDensityRequireStyleConsistency { get; set; }
        public double? OcrDensityStyleConsistencyThreshold { get; set; }
        public bool? DisableDetection { get; set; }
        public double? ChromaprintAnalysisPercent { get; set; }
        public int? ChromaprintMinDuration { get; set; }
        public int? ChromaprintMaxDuration { get; set; }
        public int? ChromaprintFingerprintDuration { get; set; }
        public double? ChromaprintSimilarityThreshold { get; set; }
        public bool? ChromaprintEnableEpisodeComparison { get; set; }
        public double? ChromaprintEpisodeComparisonTolerance { get; set; }
        public int? ChromaprintEpisodeComparisonMinimumEpisodes { get; set; }
        public double? ChromaprintStopSecondsFromEnd { get; set; }
        public double? TimestampOffsetSeconds { get; set; }
        public double? BlackFrameMinDuration { get; set; }
        public string? OcrLanguages { get; set; }
        public int? OcrPageSegmentationMode { get; set; }
        public int? OcrEngineMode { get; set; }
        public bool? OcrPreserveInterwordSpaces { get; set; }
        public double? OcrMinimumConfidence { get; set; }
        public bool? OcrEnableSmartFrameSkipping { get; set; }
        public int? OcrConsecutiveMatchesForEarlyStop { get; set; }
        public bool? OcrEnableImagePreprocessing { get; set; }
        public double? OcrContrastEnhancement { get; set; }
        public double? OcrBrightnessAdjustment { get; set; }
        public bool? OcrEnableSharpening { get; set; }
        public double? OcrSharpenAmount { get; set; }
        public bool? OcrEnableRoiDetection { get; set; }
        public string? OcrRoiRegion { get; set; }
        public bool? OcrEnableFuzzyMatching { get; set; }
        public int? OcrFuzzyMatchMaxDistance { get; set; }
        public bool? OcrEnableScrollingDetection { get; set; }
        public int? OcrScrollingMinFrames { get; set; }
        public double? OcrScrollingOverlapThreshold { get; set; }
        public bool? OcrEnableAdaptiveFrameRate { get; set; }
        public double? OcrAdaptiveFrameRateMin { get; set; }
        public bool? OcrEnableCreditStructureDetection { get; set; }
        public int? OcrMinimumStructureLines { get; set; }
    }

    public class PluginConfiguration : BasePluginConfiguration
    {
        public bool EnableAutoDetection { get; set; } = false;
        
        public double TimestampOffsetSeconds { get; set; } = 0.0;

        public bool EnableVideoPatternDetection { get; set; } = true;
        public bool EnableBlackScreenDetection { get; set; } = true;
        public bool EnableAudioSilenceDetection { get; set; } = true;
        public bool EnableAudioPatternDetection { get; set; } = true;
        public bool EnableTextDetection { get; set; } = true;
        public bool EnableSceneChangeDetection { get; set; } = true;
        public bool EnableKeywordDetection { get; set; } = true;

        public int VideoPatternSensitivity { get; set; } = 3;
        public int VideoPatternWindowSize { get; set; } = 5;
        public double VideoPatternSearchStart { get; set; } = 0.5;

        public int AudioPatternSensitivity { get; set; } = 3;
        public int AudioPatternWindowSize { get; set; } = 5;
        public double AudioPatternSearchStart { get; set; } = 0.5;

        public int BlackScreenThreshold { get; set; } = 15;
        public int BlackScreenMinDuration { get; set; } = 2;
        public double BlackScreenSearchStart { get; set; } = 0.7;

        public int TextDetectionThreshold { get; set; } = 100;
        public int TextDetectionMinLines { get; set; } = 5;
        public double TextDetectionSearchStart { get; set; } = 0.7;

        public int AudioSilenceThreshold { get; set; } = -30;
        public double AudioSilenceMinDuration { get; set; } = 1.5;
        public double AudioSearchStartPosition { get; set; } = 0.6;

        public int SceneChangeThreshold { get; set; } = 30;
        public double SceneChangeSearchStart { get; set; } = 0.7;
        public double SceneChangeMinDeviation { get; set; } = 0.25;

        public string KeywordDetectionKeywords { get; set; } = "directed by,produced by,executive producer,written by,cast,credits,fin,ende,終,完,fim,fine,producer,music by,music,cinematography,editor,editing,production design,costume design,casting,based on,story by,screenplay,associate producer,co-producer,created by,developed by,series producer,composer,director of photography,visual effects,sound,the end,end credits,starring,guest starring,special thanks,production company";
        public double KeywordDetectionSearchStart { get; set; } = 0.65;
        public int KeywordDetectionMinTextScore { get; set; } = 50;
        public int KeywordDetectionRegionHeight { get; set; } = 120;

        public DetectionMode DetectionMode { get; set; } = DetectionMode.OcrOnly;
        
        public OcrEngine OcrEngine { get; set; } = OcrEngine.Tesseract;
        
        public string OcrEndpoint { get; set; } = "http://localhost:8884";
        public string OcrDetectionKeywords { get; set; } = "associate producer,based on,cast,casting,cinematography,co-producer,composer,costume design,created by,credits,developed by,directed by,director of photography,editing,editor,end credits,ende,executive producer,fim,fin,fine,guest starring,music by,produced by,producer,production company,production design,screenplay,series producer,sound,special thanks,starring,story by,the end,visual effects,written by,끝,終,キャスト,スタッフ,監督,脚本,音楽,製作,制作,プロデューサー,原作,演出,撮影,編集,おわり,提供,協力,出演";
        
        public string OcrLanguages { get; set; } = "eng+jpn";
        public int OcrPageSegmentationMode { get; set; } = 3;
        public int OcrEngineMode { get; set; } = 3;
        public bool OcrPreserveInterwordSpaces { get; set; } = true;

        public bool OcrEnableEpisodeComparison { get; set; } = true;
        public double OcrEpisodeComparisonTolerance { get; set; } = 20.0;
        public int OcrEpisodeComparisonMinimumEpisodes { get; set; } = 4;
        public string OcrSearchStartUnit { get; set; } = "minutes";
        public double OcrSearchStartValue { get; set; } = 3.0;

        public double OcrDetectionSearchStart { get; set; } = 0.65;
        public double OcrMinutesFromEnd { get; set; } = 3.0;
        public double OcrFrameRate { get; set; } = 0.5;
        public int OcrMinimumMatches { get; set; } = 1;
        public int OcrMaxFramesToProcess { get; set; } = 0;
        public double OcrMaxAnalysisDuration { get; set; } = 600.0;
        public double OcrStopSecondsFromEnd { get; set; } = 20.0;
        public int OcrJpegQuality { get; set; } = 92;
        public int OcrMaxResolutionHeight { get; set; } = 1080;
        public int OcrDelayBetweenFramesMs { get; set; } = 0;

        public bool OcrEnableParallelProcessing { get; set; } = false;
        public int OcrParallelBatchSize { get; set; } = 4;
        public int OcrDelayBetweenBatchesMs { get; set; } = 200;
        public bool OcrEnableSmartFrameSkipping { get; set; } = true;
        public int OcrConsecutiveMatchesForEarlyStop { get; set; } = 3;
        public double OcrMinimumConfidence { get; set; } = 0.0;

        public bool OcrEnableCharacterDensityDetection { get; set; } = true;
        public int OcrCharacterDensityThreshold { get; set; } = 20;
        public int OcrCharacterDensityConsecutiveFrames { get; set; } = 3;
        public bool OcrCharacterDensityPrimaryMethod { get; set; } = true;
        
        public bool OcrDensityRequireKeyword { get; set; } = true;
        public double OcrDensityKeywordWindowSeconds { get; set; } = 10.0;
        
        public bool OcrDensityRequireTemporalConsistency { get; set; } = true;
        public double OcrDensityMinimumDurationSeconds { get; set; } = 15.0;
        
        public bool OcrDensityRequireStyleConsistency { get; set; } = true;
        public double OcrDensityStyleConsistencyThreshold { get; set; } = 0.7;

        public int OcrRetryAttempts { get; set; } = 5;
        public int OcrRetryDelayMs { get; set; } = 2000;

        public string OcrFfmpegPreInputArgs { get; set; } = "";
        public int OcrFfmpegThreads { get; set; } = 0;
        public int OcrFfmpegFilterThreads { get; set; } = 0;
        public bool OcrEnableHardwareAcceleration { get; set; } = false;
        public string OcrHardwareAccelerationType { get; set; } = "none";
        public string OcrHardwareDevice { get; set; } = "";
        public bool OcrUseHardwareOutputFormat { get; set; } = true;
        public bool OcrUseHardwareFilters { get; set; } = true;
        public bool OcrUseDirectMemoryPipeline { get; set; } = true;

        public bool OcrEnableImagePreprocessing { get; set; } = false;
        public double OcrContrastEnhancement { get; set; } = 1.5;
        public double OcrBrightnessAdjustment { get; set; } = 0.05;
        public bool OcrEnableSharpening { get; set; } = false;
        public double OcrSharpenAmount { get; set; } = 1.0;

        public bool OcrEnableRoiDetection { get; set; } = false;
        public string OcrRoiRegion { get; set; } = "full";

        public bool OcrEnableFuzzyMatching { get; set; } = false;
        public int OcrFuzzyMatchMaxDistance { get; set; } = 2;

        public bool OcrEnableScrollingDetection { get; set; } = false;
        public int OcrScrollingMinFrames { get; set; } = 5;
        public double OcrScrollingOverlapThreshold { get; set; } = 0.3;

        public bool OcrEnableAdaptiveFrameRate { get; set; } = false;
        public double OcrAdaptiveFrameRateMin { get; set; } = 0.25;

        public bool OcrEnableCreditStructureDetection { get; set; } = false;
        public int OcrMinimumStructureLines { get; set; } = 4;

        public bool UseCorrelationScoring { get; set; } = true;
        public int CorrelationWindowSeconds { get; set; } = 5;

        public string DetectionResultSelection { get; set; } = "CorrelationScoring";

        public int VideoPatternPriority { get; set; } = 1;
        public int AudioPatternPriority { get; set; } = 2;
        public int BlackScreenPriority { get; set; } = 3;
        public int AudioSilencePriority { get; set; } = 4;
        public int TextDetectionPriority { get; set; } = 2;
        public int SceneChangePriority { get; set; } = 2;
        public int KeywordDetectionPriority { get; set; } = 1;
        public int OcrDetectionPriority { get; set; } = 1;

        public bool EnableCombinedHeuristic { get; set; } = false;
        public int CombinedHeuristicPriority { get; set; } = 1;
        public double CombinedMinutesFromEnd { get; set; } = 0.0;
        public double CombinedSearchStart { get; set; } = 0.70;
        public double CombinedFrameRate { get; set; } = 1.0;
        public bool CombinedUseKeywords { get; set; } = true;
        public bool CombinedUseTextDensity { get; set; } = true;
        public bool CombinedUseDarkness { get; set; } = true;
        public double CombinedKeywordWeight { get; set; } = 0.4;
        public double CombinedTextDensityWeight { get; set; } = 0.3;
        public double CombinedDarknessWeight { get; set; } = 0.3;
        public double CombinedScoreThreshold { get; set; } = 0.6;
        public double CombinedMinSustainedSeconds { get; set; } = 3.0;

        public int CpuUsageLimit { get; set; } = 100;
        public int CpuThrottleDelayMs { get; set; } = 100;
        public int DelayBetweenEpisodesMs { get; set; } = 0;
        public bool LowerThreadPriority { get; set; } = false;
        public bool LowerProcessPriority { get; set; } = false;
        public string TempFolderPath { get; set; } = "";

        public bool EnableDetailedLogging { get; set; } = false;
        public bool EnableLogToFile { get; set; } = false;
        public string LogFileFolderPath { get; set; } = "";

        public string[] LibraryIds { get; set; } = Array.Empty<string>();
        public bool ScheduledTaskOnlyProcessMissing { get; set; } = true;

        public bool BackupImportOverwriteExisting { get; set; } = false;

        public bool ManualSkipExistingMarkers { get; set; } = false;

        public bool SkipPreviouslyFailedEpisodes { get; set; } = true;
        public bool IgnoreFailureMarkers { get; set; } = false;

        public bool EnableScheduledTaskNotifications { get; set; } = false;
        public bool EnableAutoDetectionNotifications { get; set; } = false;
        public bool NotifyOnSuccessOnly { get; set; } = false;
        public int MinimumEpisodesForNotification { get; set; } = 1;

        public bool PreventConcurrentPluginProcessing { get; set; } = true;

        public int MaxScheduledBackups { get; set; } = 10;
        public string BackupFolderPath { get; set; } = "";

        public bool EnableAutoBackupAfterDetection { get; set; } = false;
        public bool EnableAutoRestoreAfterScan { get; set; } = false;
        public bool SkipDetectionIfFileUnchanged { get; set; } = false;
        public bool EnableTracerMode { get; set; } = false;
        public bool OnlyProcessNewEpisodes { get; set; } = false;

        public bool EnableThumbnailGeneration { get; set; } = false;

        public string[] AutoSkipExcludedSeriesIds { get; set; } = Array.Empty<string>();
        public int ThumbnailWidth { get; set; } = 320;
        public int ThumbnailQuality { get; set; } = 75;

        public bool EnableAnimeDetection { get; set; } = true;
        public AnimeDetectionMethod AnimeDetectionMethod { get; set; } = AnimeDetectionMethod.BlackFrame;
        public int BlackFrameMinimumPercentage { get; set; } = 85;
        public int BlackFrameThreshold { get; set; } = 28;
        public double BlackFrameMinDuration { get; set; } = 0.5;
        public double BlackFrameMinCreditsDuration { get; set; } = 15.0;
        public double BlackFrameMinimumDensity { get; set; } = 0.50;
        public double BlackFrameMaxCreditsDuration { get; set; } = 450.0;
        public double BlackFrameMaxSceneMergeGap { get; set; } = 20.0;
        public bool BlackFrameScanAllFrames { get; set; } = false;
        public bool BlackFrameAutoFallbackToAllFrames { get; set; } = true;
        public bool BlackFrameRefineCreditsBoundary { get; set; } = true;
        public int BlackFrameParallelSessions { get; set; } = 1;
        public int BlackFrameFfmpegThreads { get; set; } = 2;

        public int ChromaprintDetectionPriority { get; set; } = 1;
        public bool ChromaprintUseAudioFingerprinting { get; set; } = false;
        public bool ChromaprintEnableBlackFrameFallback { get; set; } = true;
        public int ChromaprintFingerprintDuration { get; set; } = 360;
        public double ChromaprintFingerprintSimilarityThreshold { get; set; } = 0.90;
        public bool ChromaprintEnableEpisodeComparison { get; set; } = true;
        public double ChromaprintEpisodeComparisonTolerance { get; set; } = 15.0;
        public int ChromaprintEpisodeComparisonMinimumEpisodes { get; set; } = 4;
        public int ChromaprintMinDuration { get; set; } = 10;
        public int ChromaprintMaxDuration { get; set; } = 300;
        public double ChromaprintSimilarityThreshold { get; set; } = 0.85;
        public int ChromaprintMinEpisodeCount { get; set; } = 4;
        public double ChromaprintAnalysisPercent { get; set; } = 10.0;
        public double ChromaprintBlackFrameThreshold { get; set; } = 0.05;
        public double ChromaprintBlackFrameMinDuration { get; set; } = 0.5;
        public bool ChromaprintUseSilenceDetection { get; set; } = true;
        public int ChromaprintSilenceThreshold { get; set; } = -50;
        public double ChromaprintSilenceMinDuration { get; set; } = 0.5;
        public double ChromaprintSilenceSearchWindow { get; set; } = 30.0;
        public double ChromaprintMinConfidence { get; set; } = 0.85;
        public double ChromaprintMinimumScoreFloor { get; set; } = 0.55;
        public double ChromaprintStopSecondsFromEnd { get; set; } = 20.0;
        public bool ChromaprintLowerProcessPriority { get; set; } = false;
        public int ChromaprintFfmpegThreads { get; set; } = 2;
        public int ChromaprintParallelSessions { get; set; } = 2;
        public int ChromaprintDelayBetweenOperationsMs { get; set; } = 0;

        public bool EnableVideoValidation { get; set; } = false;
        public int VideoValidationTimeoutSeconds { get; set; } = 10;

        public bool DisableDetection { get; set; } = false;
        public List<DetectionRule> DetectionRules { get; set; } = new List<DetectionRule>();

        public PluginConfiguration ShallowClone()
        {
            return (PluginConfiguration)MemberwiseClone();
        }
    }
}
