using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;
using System.Collections.Generic;

namespace EmbyCredits.Api
{
    [Authenticated]
    [Route(ApiRoutes.TriggerDetection, "POST", Summary = "Triggers credits detection for all episodes.")]
    public class TriggerDetectionRequest : IReturn<object>
    {
        public int Limit { get; set; }
    }
    [Authenticated]
    [Route(ApiRoutes.ProcessEpisode, "POST", Summary = "Process a specific episode for credits detection.")]
    public class ProcessEpisodeRequest : IReturn<object>
    {
        public string ItemId { get; set; } = string.Empty;
        public bool SkipExistingMarkers { get; set; } = false;
    }
    [Authenticated]
    [Route(ApiRoutes.ProcessSeries, "POST", Summary = "Process all episodes in a TV series for credits detection.")]
    public class ProcessSeriesRequest : IReturn<object>
    {
        public string SeriesId { get; set; } = string.Empty;
        public bool SkipExistingMarkers { get; set; } = false;
    }
    [Authenticated]
    [Route(ApiRoutes.ProcessSeason, "POST", Summary = "Process all episodes in a specific season for credits detection.")]
    public class ProcessSeasonRequest : IReturn<object>
    {
        public string SeriesId { get; set; } = string.Empty;
        public int SeasonNumber { get; set; }
        public bool SkipExistingMarkers { get; set; } = false;
    }
    [Authenticated]
    [Route(ApiRoutes.ProcessSeasonMissingMarkers, "POST", Summary = "Process only episodes missing credits markers in a specific season.")]
    public class ProcessSeasonMissingMarkersRequest : IReturn<object>
    {
        public string SeriesId { get; set; } = string.Empty;
        public int SeasonNumber { get; set; }
    }
    [Authenticated]
    [Route(ApiRoutes.BatchUpdateSeasonMissingMarkers, "POST", Summary = "Batch update credits timestamps for all episodes missing markers in a specific season.")]
    public class BatchUpdateSeasonMissingMarkersRequest : IReturn<object>
    {
        public string SeriesId { get; set; } = string.Empty;
        public int SeasonNumber { get; set; }
        public double CreditsStartSeconds { get; set; }
        public bool IsRelativeFromEnd { get; set; } = false;
    }
    [Authenticated]
    [Route(ApiRoutes.ProcessLibrary, "POST", Summary = "Process all TV shows in a library for credits detection.")]
    public class ProcessLibraryRequest : IReturn<object>
    {
        public string LibraryId { get; set; } = string.Empty;
        public bool SkipExistingMarkers { get; set; } = false;
    }
    [Authenticated]
    [Route(ApiRoutes.GetAllSeries, "GET", Summary = "Gets a list of all TV series in the library.")]
    public class GetAllSeriesRequest : IReturn<object> 
    {
        public string LibraryId { get; set; } = string.Empty;
    }
    [Authenticated]
    [Route(ApiRoutes.GetProgress, "GET", Summary = "Gets the current progress of credits detection.")]
    public class GetProgressRequest : IReturn<object> { }
    [Authenticated]
    [Route(ApiRoutes.GetBackupExportProgress, "GET", Summary = "Gets the current progress of a credits backup export.")]
    public class GetBackupExportProgressRequest : IReturn<object> { }
    [Authenticated]
    [Route(ApiRoutes.GetBackupImportProgress, "GET", Summary = "Gets the current progress of a credits backup import.")]
    public class GetBackupImportProgressRequest : IReturn<object> { }
    [Authenticated]
    [Route(ApiRoutes.CancelDetection, "POST", Summary = "Cancels the currently running credits detection.")]
    public class CancelDetectionRequest : IReturn<object> { }
    [Authenticated]
    [Route(ApiRoutes.ClearQueue, "POST", Summary = "Clears the processing queue.")]
    public class ClearQueueRequest : IReturn<object> { }
    [Authenticated]
    [Route(ApiRoutes.GetSeriesMarkers, "GET", Summary = "Gets chapter markers for all episodes in a TV series.")]
    public class GetSeriesMarkersRequest : IReturn<object>
    {
        public string SeriesId { get; set; } = string.Empty;
    }
    [Authenticated]
    [Route(ApiRoutes.TestOcrConnection, "POST", Summary = "Tests the OCR server connection.")]
    public class TestOcrConnectionRequest : IReturn<object>
    {
        public string OcrEndpoint { get; set; } = string.Empty;
        public string OcrEngine { get; set; } = "Tesseract";
    }
    [Authenticated]
    [Route(ApiRoutes.DryRunSeries, "POST", Summary = "Dry run - detect credits without saving markers.")]
    public class DryRunSeriesRequest : IReturn<object>
    {
        public string SeriesId { get; set; } = string.Empty;
        public string EpisodeId { get; set; } = string.Empty;
        public string LibraryId { get; set; } = string.Empty;
        public int? SeasonNumber { get; set; }
        public bool SkipExistingMarkers { get; set; } = false;
    }
    [Authenticated]
    [Route(ApiRoutes.DryRunSeriesDebug, "POST", Summary = "Dry run with debug logging - detect credits and capture debug log.")]
    public class DryRunSeriesDebugRequest : IReturn<object>
    {
        public string SeriesId { get; set; } = string.Empty;
        public string EpisodeId { get; set; } = string.Empty;
        public string LibraryId { get; set; } = string.Empty;
        public int? SeasonNumber { get; set; }
        public bool SkipExistingMarkers { get; set; } = false;
    }
    [Authenticated]
    [Route(ApiRoutes.GetDebugLog, "GET", Summary = "Downloads the debug log from the last debug dry run.")]
    public class GetDebugLogRequest : IReturn<System.IO.Stream> { }
    [Authenticated]
    [Route(ApiRoutes.AddTimestampFromDryRun, "POST", Summary = "Manually adds a timestamp from a dry run detection.")]
    public class AddTimestampFromDryRunRequest : IReturn<object>
    {
        public string EpisodeId { get; set; } = string.Empty;
        public double TimestampSeconds { get; set; }
    }
    [Authenticated]
    [Route(ApiRoutes.ExportCreditsBackup, "POST", Summary = "Exports credits markers to JSON for download")]
    public class ExportCreditsBackupRequest : IReturn<System.IO.Stream>
    {
        public List<string>? LibraryIds { get; set; }
        public List<string>? SeriesIds { get; set; }
    }
    [Authenticated]
    [Route(ApiRoutes.ImportCreditsBackup, "POST", Summary = "Imports credits markers from JSON backup")]
    public class ImportCreditsBackupRequest : IReturn<object>
    {
        public string JsonData { get; set; } = string.Empty;
        public bool OverwriteExisting { get; set; }
    }
    [Authenticated]
    [Route(ApiRoutes.ExportSeriesCredits, "GET", Summary = "Exports credits markers for a single TV series")]
    public class ExportSeriesCreditsRequest : IReturn<System.IO.Stream>
    {
        public string SeriesId { get; set; } = string.Empty;
    }
    [Authenticated]
    [Route(ApiRoutes.BulkExportToFolder, "POST", Summary = "Exports credits markers for selected series to the configured backup folder")]
    public class BulkExportToFolderRequest : IReturn<object>
    {
        public List<string>? SeriesIds { get; set; }
    }
    [Authenticated]
    [Route(ApiRoutes.ImportSeriesCredits, "POST", Summary = "Imports credits markers for a single TV series")]
    public class ImportSeriesCreditsRequest : IReturn<object>
    {
        public string SeriesId { get; set; } = string.Empty;
        public string JsonData { get; set; } = string.Empty;
        public bool OverwriteExisting { get; set; } = true;
    }
    [Authenticated]
    [Route(ApiRoutes.GetImage, "GET", Summary = "Gets a plugin image resource.")]
    public class GetImageRequest : IReturn<System.IO.Stream>
    {
        public string ImageName { get; set; } = string.Empty;
    }
    [Authenticated]
    [Route(ApiRoutes.UpdateCreditsMarker, "POST", Summary = "Updates the credits marker timestamp for an episode.")]
    public class UpdateCreditsMarkerRequest : IReturn<object>
    {
        public string EpisodeId { get; set; } = string.Empty;
        public double CreditsStartSeconds { get; set; }
        public bool IsRelativeFromEnd { get; set; } = false;
    }
    [Authenticated]
    [Route(ApiRoutes.ApplyToSeason, "POST", Summary = "Copies one episode's credits timestamp to all episodes in the season that don't have markers.")]
    public class ApplyToSeasonRequest : IReturn<object>
    {
        public string EpisodeId { get; set; } = string.Empty;
        public string SeriesId { get; set; } = string.Empty;
        public int SeasonNumber { get; set; }
    }
    [Authenticated]
    [Route(ApiRoutes.GetSeasonValidation, "GET", Summary = "Gets validation data for all episodes in a season.")]
    public class GetSeasonValidationRequest : IReturn<object>
    {
        public string SeriesId { get; set; } = string.Empty;
        public int SeasonNumber { get; set; }
    }
    [Authenticated]
    [Route(ApiRoutes.GetThumbnail, "GET", Summary = "Gets a detection thumbnail image.")]
    public class GetThumbnailRequest : IReturn<System.IO.Stream>
    {
        public string ThumbnailId { get; set; } = string.Empty;
    }
    [Authenticated]
    [Route(ApiRoutes.GetMemoryUsage, "GET", Summary = "Gets current plugin memory usage.")]
    public class GetMemoryUsageRequest : IReturn<object>
    {
    }
    [Authenticated]
    [Route(ApiRoutes.StartDetection, "POST", Summary = "Starts credits detection with custom settings.")]
    public class StartDetectionRequest : IReturn<object>
    {
        public string? EpisodeId { get; set; }
        public string? SeriesId { get; set; }
        public int? SeasonNumber { get; set; }
        public string? LibraryId { get; set; }
        public bool SkipExistingMarkers { get; set; } = false;
        public bool DryRun { get; set; } = false;
        public bool EnableDebugLogging { get; set; } = false;
        public DetectionSettingsOverride? SettingsOverride { get; set; }
    }

    public class DetectionSettingsOverride
    {
        public string? DetectionMode { get; set; }
        public string? OcrEngine { get; set; }
        public string? OcrEndpoint { get; set; }
        public string? OcrLanguages { get; set; }
        public string? OcrDetectionKeywords { get; set; }
        public double? OcrSearchStartValue { get; set; }
        public string? OcrSearchStartUnit { get; set; }
        public double? OcrMinutesFromEnd { get; set; }
        public double? OcrFrameRate { get; set; }
        public int? OcrMinimumMatches { get; set; }
        public int? OcrMaxFramesToProcess { get; set; }
        public double? OcrMaxAnalysisDuration { get; set; }
        public double? OcrStopSecondsFromEnd { get; set; }
        public int? OcrPageSegmentationMode { get; set; }
        public int? OcrEngineMode { get; set; }
        public int? OcrJpegQuality { get; set; }
        public int? OcrMaxResolutionHeight { get; set; }
        public int? OcrDelayBetweenFramesMs { get; set; }
        public bool? OcrEnableParallelProcessing { get; set; }
        public int? OcrParallelBatchSize { get; set; }
        public int? OcrDelayBetweenBatchesMs { get; set; }
        public bool? OcrEnableSmartFrameSkipping { get; set; }
        public int? OcrConsecutiveMatchesForEarlyStop { get; set; }
        public double? OcrMinimumConfidence { get; set; }
        public bool? OcrEnableEpisodeComparison { get; set; }
        public double? OcrEpisodeComparisonTolerance { get; set; }
        public int? OcrEpisodeComparisonMinimumEpisodes { get; set; }
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
        public bool? OcrEnableHardwareAcceleration { get; set; }
        public string? OcrHardwareAccelerationType { get; set; }
        public string? OcrHardwareDevice { get; set; }
        public bool? OcrUseHardwareOutputFormat { get; set; }
        public bool? OcrUseHardwareFilters { get; set; }
        public bool? OcrUseDirectMemoryPipeline { get; set; }
        public bool? EnableAnimeDetection { get; set; }
        public string? AnimeDetectionMethod { get; set; }
        public int? BlackFrameMinimumPercentage { get; set; }
        public int? BlackFrameThreshold { get; set; }
        public double? ChromaprintFingerprintSimilarityThreshold { get; set; }
        public bool? ChromaprintEnableEpisodeComparison { get; set; }
        public double? ChromaprintEpisodeComparisonTolerance { get; set; }
        public int? ChromaprintEpisodeComparisonMinimumEpisodes { get; set; }
        public double? ChromaprintStopSecondsFromEnd { get; set; }
        public int? CpuUsageLimit { get; set; }
        public int? CpuThrottleDelayMs { get; set; }
        public bool? LowerThreadPriority { get; set; }
        public bool? LowerProcessPriority { get; set; }
        public bool? EnableVideoValidation { get; set; }
        public int? VideoValidationTimeoutSeconds { get; set; }
    }
    [Authenticated]
    [Route(ApiRoutes.GetDetectionMethods, "GET", Summary = "Gets available detection methods and their configurations.")]
    public class GetDetectionMethodsRequest : IReturn<object> { }
    [Authenticated]
    [Route(ApiRoutes.GetDetectionResults, "GET", Summary = "Gets detection results for episodes.")]
    public class GetDetectionResultsRequest : IReturn<object>
    {
        public string? SeriesId { get; set; }
        public int? SeasonNumber { get; set; }
        public string? EpisodeId { get; set; }
        public bool IncludeAllEpisodes { get; set; } = false;
    }
    [Authenticated]
    [Route(ApiRoutes.GetDetectionHistory, "GET", Summary = "Gets detection history for a series or episode.")]
    public class GetDetectionHistoryRequest : IReturn<object>
    {
        public string? SeriesId { get; set; }
        public string? EpisodeId { get; set; }
        public int Limit { get; set; } = 50;
    }
    [Authenticated]
    [Route(ApiRoutes.GetEpisodeDetectionResult, "GET", Summary = "Gets detailed detection result for a specific episode.")]
    public class GetEpisodeDetectionResultRequest : IReturn<object>
    {
        public string EpisodeId { get; set; } = string.Empty;
    }
    [Authenticated]
    [Route(ApiRoutes.GetFailedEpisodes, "GET", Summary = "Gets all episodes marked as failed.")]
    public class GetFailedEpisodesRequest : IReturn<object>
    {
        public string? LibraryId { get; set; }
        public string? SeriesId { get; set; }
    }
    [Authenticated]
    [Route(ApiRoutes.ClearFailureMarkers, "POST", Summary = "Clears failure markers for specific episodes.")]
    public class ClearFailureMarkersRequest : IReturn<object>
    {
        public List<string> EpisodeIds { get; set; } = new List<string>();
    }
    [Authenticated]
    [Route(ApiRoutes.ClearAllFailureMarkers, "POST", Summary = "Clears all failure markers.")]
    public class ClearAllFailureMarkersRequest : IReturn<object>
    {
        public string? LibraryId { get; set; }
        public string? SeriesId { get; set; }
    }
    [Authenticated]
    [Route(ApiRoutes.GetTracerEpisodes, "GET", Summary = "Returns the list of episodes pending detection.")]
    public class GetTracerEpisodesRequest : IReturn<object> { }
    [Authenticated]
    [Route(ApiRoutes.DismissTracerEpisode, "POST", Summary = "Removes one episode from the tracer list.")]
    public class DismissTracerEpisodeRequest : IReturn<object>
    {
        public string EpisodeId { get; set; } = string.Empty;
    }
    [Authenticated]
    [Route(ApiRoutes.ClearTracerList, "POST", Summary = "Clears the entire tracer list.")]
    public class ClearTracerListRequest : IReturn<object> { }
    [Authenticated]
    [Route(ApiRoutes.ClearDetectedTracerList, "POST", Summary = "Clears the detected history tracer list.")]
    public class ClearDetectedTracerListRequest : IReturn<object> { }
}
