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

        public DetectionMode DetectionMode { get; set; } = DetectionMode.OcrOnly;


        public bool UseCorrelationScoring { get; set; } = true;
        public int CorrelationWindowSeconds { get; set; } = 5;

        public string DetectionResultSelection { get; set; } = "CorrelationScoring";

        public int BlackScreenPriority { get; set; } = 3;
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
