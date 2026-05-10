namespace EmbyCredits.Api
{
    internal static class ApiRoutes
    {

        public const string TriggerDetection = "/CreditsDetector/TriggerDetection";
        public const string ProcessEpisode = "/CreditsDetector/ProcessEpisode";
        public const string ProcessSeries = "/CreditsDetector/ProcessSeries";
        public const string ProcessSeason = "/CreditsDetector/ProcessSeason";
        public const string ProcessSeasonMissingMarkers = "/CreditsDetector/ProcessSeasonMissingMarkers";
        public const string BatchUpdateSeasonMissingMarkers = "/CreditsDetector/BatchUpdateSeasonMissingMarkers";
        public const string ProcessLibrary = "/CreditsDetector/ProcessLibrary";
        public const string GetAllSeries = "/CreditsDetector/GetAllSeries";
        public const string GetProgress = "/CreditsDetector/GetProgress";
        public const string CancelDetection = "/CreditsDetector/CancelDetection";
        public const string ClearQueue = "/CreditsDetector/ClearQueue";
        public const string GetSeriesMarkers = "/CreditsDetector/GetSeriesMarkers";
        public const string TestOcrConnection = "/CreditsDetector/TestOcrConnection";
        public const string DryRunSeries = "/CreditsDetector/DryRunSeries";
        public const string DryRunSeriesDebug = "/CreditsDetector/DryRunSeriesDebug";
        public const string GetDebugLog = "/CreditsDetector/GetDebugLog";
        public const string ExportCreditsBackup = "/CreditsDetector/ExportCreditsBackup";
        public const string ImportCreditsBackup = "/CreditsDetector/ImportCreditsBackup";
        public const string ExportSeriesCredits = "/CreditsDetector/ExportSeriesCredits";
        public const string BulkExportToFolder = "/CreditsDetector/BulkExportToFolder";
        public const string ImportSeriesCredits = "/CreditsDetector/ImportSeriesCredits";
        public const string GetBackupExportProgress = "/CreditsDetector/GetBackupExportProgress";
        public const string GetBackupImportProgress = "/CreditsDetector/GetBackupImportProgress";
        public const string UpdateCreditsMarker = "/CreditsDetector/UpdateCreditsMarker";
        public const string ApplyToSeason = "/CreditsDetector/ApplyToSeason";
        public const string AddTimestampFromDryRun = "/CreditsDetector/AddTimestampFromDryRun";
        public const string GetImage = "/CreditsDetector/Images/{ImageName}";
        public const string GetSeasonValidation = "/CreditsDetector/GetSeasonValidation";
        public const string GetThumbnail = "/CreditsDetector/Thumbnail/{ThumbnailId}";
        public const string GetMemoryUsage = "/CreditsDetector/GetMemoryUsage";
        public const string StartDetection = "/CreditsDetector/StartDetection";
        public const string GetDetectionMethods = "/CreditsDetector/GetDetectionMethods";
        public const string GetDetectionResults = "/CreditsDetector/GetDetectionResults";
        public const string GetDetectionHistory = "/CreditsDetector/GetDetectionHistory";
        public const string GetEpisodeDetectionResult = "/CreditsDetector/GetEpisodeDetectionResult";
        public const string GetFailedEpisodes = "/CreditsDetector/GetFailedEpisodes";
        public const string ClearFailureMarkers = "/CreditsDetector/ClearFailureMarkers";
        public const string ClearAllFailureMarkers = "/CreditsDetector/ClearAllFailureMarkers";
        public const string GetTracerEpisodes = "/CreditsDetector/GetTracerEpisodes";
        public const string DismissTracerEpisode = "/CreditsDetector/DismissTracerEpisode";
        public const string ClearTracerList = "/CreditsDetector/ClearTracerList";
        public const string ClearDetectedTracerList = "/CreditsDetector/ClearDetectedTracerList";
        public const string ClearFailedTracerList = "/CreditsDetector/ClearFailedTracerList";
    }
}
