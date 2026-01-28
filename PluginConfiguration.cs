using MediaBrowser.Model.Plugins;
using System;

namespace EmbyCredits
{
    public class PluginConfiguration : BasePluginConfiguration
    {
        public string ConfigurationVersion { get; set; } = Guid.NewGuid().ToString();

        public bool EnableAutoDetection { get; set; } = false;
        public bool UseEpisodeComparison { get; set; } = false;
        public int MinimumEpisodesToCompare { get; set; } = 3;
        public double SimilarityThreshold { get; set; } = 0.85;
        public bool EnableFailedEpisodeFallback { get; set; } = false;
        public double MinimumSuccessRateForFallback { get; set; } = 0.5;

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

        public string KeywordDetectionKeywords { get; set; } = "directed by,produced by,executive producer,written by,cast,credits,fin,ende,?,?,fim,fine,producer,music by,music,cinematography,editor,editing,production design,costume design,casting,based on,story by,screenplay,associate producer,co-producer,created by,developed by,series producer,composer,director of photography,visual effects,sound,the end,end credits,starring,guest starring,special thanks,production company";
        public double KeywordDetectionSearchStart { get; set; } = 0.65;
        public int KeywordDetectionMinTextScore { get; set; } = 50;
        public int KeywordDetectionRegionHeight { get; set; } = 120;

        public bool EnableOcrDetection { get; set; } = true;
        public string OcrEndpoint { get; set; } = "http://localhost:8884";
        public string OcrDetectionKeywords { get; set; } = "associate producer,based on,cast,casting,cinematography,co-producer,composer,costume design,created by,credits,developed by,directed by,director of photography,editing,editor,end credits,ende,executive producer,fim,fin,fine,guest starring,music by,produced by,producer,production company,production design,screenplay,series producer,sound,special thanks,starring,story by,the end,visual effects,written by,끝,終";

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
        public int DelayBetweenEpisodesMs { get; set; } = 0;
        public bool LowerThreadPriority { get; set; } = false;
        public string TempFolderPath { get; set; } = "";

        public bool EnableDetailedLogging { get; set; } = false;

        public string[] LibraryIds { get; set; } = Array.Empty<string>();
        public bool ScheduledTaskOnlyProcessMissing { get; set; } = true;

        public bool BackupImportOverwriteExisting { get; set; } = false;

        public bool ManualSkipExistingMarkers { get; set; } = false;

        public bool EnableScheduledTaskNotifications { get; set; } = false;
        public bool EnableAutoDetectionNotifications { get; set; } = false;
        public bool NotifyOnSuccessOnly { get; set; } = false;
        public int MinimumEpisodesForNotification { get; set; } = 1;

        public bool PreventConcurrentPluginProcessing { get; set; } = true;

        public int MaxScheduledBackups { get; set; } = 10;
        public string BackupFolderPath { get; set; } = "";

        public bool EnableThumbnailGeneration { get; set; } = false;
        public int ThumbnailWidth { get; set; } = 320;
        public int ThumbnailQuality { get; set; } = 85;
    }
}
