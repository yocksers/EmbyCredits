using MediaBrowser.Model.Services;
using System.Collections.Generic;

namespace EmbyCredits.Api
{
    [Route(ApiRoutes.TriggerDetection, "POST", Summary = "Triggers credits detection for all episodes.")]
    public class TriggerDetectionRequest : IReturn<object>
    {
        public int Limit { get; set; }
    }

    [Route(ApiRoutes.ProcessEpisode, "POST", Summary = "Process a specific episode for credits detection.")]
    public class ProcessEpisodeRequest : IReturn<object>
    {
        public string ItemId { get; set; } = string.Empty;
        public bool SkipExistingMarkers { get; set; } = false;
    }

    [Route(ApiRoutes.ProcessSeries, "POST", Summary = "Process all episodes in a TV series for credits detection.")]
    public class ProcessSeriesRequest : IReturn<object>
    {
        public string SeriesId { get; set; } = string.Empty;
        public bool SkipExistingMarkers { get; set; } = false;
    }

    [Route(ApiRoutes.ProcessSeason, "POST", Summary = "Process all episodes in a specific season for credits detection.")]
    public class ProcessSeasonRequest : IReturn<object>
    {
        public string SeriesId { get; set; } = string.Empty;
        public int SeasonNumber { get; set; }
        public bool SkipExistingMarkers { get; set; } = false;
    }

    [Route(ApiRoutes.ProcessSeasonMissingMarkers, "POST", Summary = "Process only episodes missing credits markers in a specific season.")]
    public class ProcessSeasonMissingMarkersRequest : IReturn<object>
    {
        public string SeriesId { get; set; } = string.Empty;
        public int SeasonNumber { get; set; }
    }

    [Route(ApiRoutes.BatchUpdateSeasonMissingMarkers, "POST", Summary = "Batch update credits timestamps for all episodes missing markers in a specific season.")]
    public class BatchUpdateSeasonMissingMarkersRequest : IReturn<object>
    {
        public string SeriesId { get; set; } = string.Empty;
        public int SeasonNumber { get; set; }
        public double CreditsStartSeconds { get; set; }
        public bool IsRelativeFromEnd { get; set; } = false;
    }

    [Route(ApiRoutes.ProcessLibrary, "POST", Summary = "Process all TV shows in a library for credits detection.")]
    public class ProcessLibraryRequest : IReturn<object>
    {
        public string LibraryId { get; set; } = string.Empty;
        public bool SkipExistingMarkers { get; set; } = false;
    }

    [Route(ApiRoutes.GetAllSeries, "GET", Summary = "Gets a list of all TV series in the library.")]
    public class GetAllSeriesRequest : IReturn<object> 
    {
        public string LibraryId { get; set; } = string.Empty;
    }

    [Route(ApiRoutes.GetProgress, "GET", Summary = "Gets the current progress of credits detection.")]
    public class GetProgressRequest : IReturn<object> { }

    public class GetBackupExportProgressRequest : IReturn<object> { }

    public class GetBackupImportProgressRequest : IReturn<object> { }

    [Route(ApiRoutes.CancelDetection, "POST", Summary = "Cancels the currently running credits detection.")]
    public class CancelDetectionRequest : IReturn<object> { }

    [Route(ApiRoutes.ClearQueue, "POST", Summary = "Clears the processing queue.")]
    public class ClearQueueRequest : IReturn<object> { }

    [Route(ApiRoutes.GetSeriesMarkers, "GET", Summary = "Gets chapter markers for all episodes in a TV series.")]
    public class GetSeriesMarkersRequest : IReturn<object>
    {
        public string SeriesId { get; set; } = string.Empty;
    }

    [Route(ApiRoutes.TestOcrConnection, "POST", Summary = "Tests the OCR server connection.")]
    public class TestOcrConnectionRequest : IReturn<object>
    {
        public string OcrEndpoint { get; set; } = string.Empty;
        public string OcrEngine { get; set; } = "Tesseract";
    }

    [Route(ApiRoutes.DryRunSeries, "POST", Summary = "Dry run - detect credits without saving markers.")]
    public class DryRunSeriesRequest : IReturn<object>
    {
        public string SeriesId { get; set; } = string.Empty;
        public string EpisodeId { get; set; } = string.Empty;
        public string LibraryId { get; set; } = string.Empty;
        public int? SeasonNumber { get; set; }
        public bool SkipExistingMarkers { get; set; } = false;
    }

    [Route(ApiRoutes.DryRunSeriesDebug, "POST", Summary = "Dry run with debug logging - detect credits and capture debug log.")]
    public class DryRunSeriesDebugRequest : IReturn<object>
    {
        public string SeriesId { get; set; } = string.Empty;
        public string EpisodeId { get; set; } = string.Empty;
        public string LibraryId { get; set; } = string.Empty;
        public int? SeasonNumber { get; set; }
        public bool SkipExistingMarkers { get; set; } = false;
    }

    [Route(ApiRoutes.GetDebugLog, "GET", Summary = "Downloads the debug log from the last debug dry run.")]
    public class GetDebugLogRequest : IReturn<System.IO.Stream> { }

    [Route(ApiRoutes.AddTimestampFromDryRun, "POST", Summary = "Manually adds a timestamp from a dry run detection.")]
    public class AddTimestampFromDryRunRequest : IReturn<object>
    {
        public string EpisodeId { get; set; } = string.Empty;
        public double TimestampSeconds { get; set; }
    }

    [Route(ApiRoutes.ExportCreditsBackup, "POST", Summary = "Exports credits markers to JSON for download")]
    public class ExportCreditsBackupRequest : IReturn<System.IO.Stream>
    {
        public List<string>? LibraryIds { get; set; }
        public List<string>? SeriesIds { get; set; }
    }

    [Route(ApiRoutes.ImportCreditsBackup, "POST", Summary = "Imports credits markers from JSON backup")]
    public class ImportCreditsBackupRequest : IReturn<object>
    {
        public string JsonData { get; set; } = string.Empty;
        public bool OverwriteExisting { get; set; }
    }

    [Route(ApiRoutes.ExportSeriesCredits, "GET", Summary = "Exports credits markers for a single TV series")]
    public class ExportSeriesCreditsRequest : IReturn<System.IO.Stream>
    {
        public string SeriesId { get; set; } = string.Empty;
    }

    [Route(ApiRoutes.ImportSeriesCredits, "POST", Summary = "Imports credits markers for a single TV series")]
    public class ImportSeriesCreditsRequest : IReturn<object>
    {
        public string SeriesId { get; set; } = string.Empty;
        public string JsonData { get; set; } = string.Empty;
        public bool OverwriteExisting { get; set; } = true;
    }

    [Route(ApiRoutes.GetImage, "GET", Summary = "Gets a plugin image resource.")]
    public class GetImageRequest : IReturn<System.IO.Stream>
    {
        public string ImageName { get; set; } = string.Empty;
    }

    [Route(ApiRoutes.UpdateCreditsMarker, "POST", Summary = "Updates the credits marker timestamp for an episode.")]
    public class UpdateCreditsMarkerRequest : IReturn<object>
    {
        public string EpisodeId { get; set; } = string.Empty;
        public double CreditsStartSeconds { get; set; }
        public bool IsRelativeFromEnd { get; set; } = false;
    }

    [Route(ApiRoutes.ApplyToSeason, "POST", Summary = "Copies one episode's credits timestamp to all episodes in the season that don't have markers.")]
    public class ApplyToSeasonRequest : IReturn<object>
    {
        public string EpisodeId { get; set; } = string.Empty;
        public string SeriesId { get; set; } = string.Empty;
        public int SeasonNumber { get; set; }
    }

    [Route(ApiRoutes.GetSeasonValidation, "GET", Summary = "Gets validation data for all episodes in a season.")]
    public class GetSeasonValidationRequest : IReturn<object>
    {
        public string SeriesId { get; set; } = string.Empty;
        public int SeasonNumber { get; set; }
    }

    [Route(ApiRoutes.GetThumbnail, "GET", Summary = "Gets a detection thumbnail image.")]
    public class GetThumbnailRequest : IReturn<System.IO.Stream>
    {
        public string ThumbnailId { get; set; } = string.Empty;
    }

    [Route(ApiRoutes.GetMemoryUsage, "GET", Summary = "Gets current plugin memory usage.")]
    public class GetMemoryUsageRequest : IReturn<object>
    {
    }
}
