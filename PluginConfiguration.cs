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
        PaddleOCR,
        LocalTesseract
    }

    // Matcher fields; per-method overrides live in DetectionRule.Ocr/Chromaprint/BlackFrame.cs
    public partial class DetectionRule
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Name { get; set; } = "";
        public List<string> Tags { get; set; } = new List<string>();
        public List<string> Studios { get; set; } = new List<string>();
        public List<string> SeriesNames { get; set; } = new List<string>();
        public List<string> LibraryIds { get; set; } = new List<string>();
        public DetectionMode? DetectionMode { get; set; }
        public bool? DisableDetection { get; set; }
        public double? TimestampOffsetSeconds { get; set; }
    }

    public partial class PluginConfiguration : BasePluginConfiguration
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
        public bool UseEmbeddedChapterMarkersScheduled { get; set; } = false;
        public bool UseEmbeddedChapterMarkersManual { get; set; } = false;
        public bool EnableTracerMode { get; set; } = false;
        public bool OnlyProcessNewEpisodes { get; set; } = false;

        public bool EnableThumbnailGeneration { get; set; } = false;

        public string[] AutoSkipExcludedSeriesIds { get; set; } = Array.Empty<string>();
        public int ThumbnailWidth { get; set; } = 320;
        public int ThumbnailQuality { get; set; } = 75;


        public bool EnableVideoValidation { get; set; } = false;
        public int VideoValidationTimeoutSeconds { get; set; } = 10;

        public bool DisableDetection { get; set; } = false;
        public List<DetectionRule> DetectionRules { get; set; } = new List<DetectionRule>();

        public bool EnableTheIntroDB { get; set; } = false;

        public PluginConfiguration ShallowClone()
        {
            return (PluginConfiguration)MemberwiseClone();
        }
    }
}
