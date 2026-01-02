namespace EmbyCredits.Api
{
    internal static class ApiRoutes
    {

        public const string TriggerDetection = "/CreditsDetector/TriggerDetection";
        public const string ProcessEpisode = "/CreditsDetector/ProcessEpisode";
        public const string ProcessSeries = "/CreditsDetector/ProcessSeries";
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
        public const string UpdateCreditsMarker = "/CreditsDetector/UpdateCreditsMarker";
        public const string GetImage = "/CreditsDetector/Images/{ImageName}";
    }
}
