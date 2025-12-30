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
    }

    [Route(ApiRoutes.ProcessSeries, "POST", Summary = "Process all episodes in a TV series for credits detection.")]
    public class ProcessSeriesRequest : IReturn<object>
    {
        public string SeriesId { get; set; } = string.Empty;
    }

    [Route(ApiRoutes.GetAllSeries, "GET", Summary = "Gets a list of all TV series in the library.")]
    public class GetAllSeriesRequest : IReturn<object> 
    {
        public string LibraryId { get; set; } = string.Empty;
    }

    [Route(ApiRoutes.GetProgress, "GET", Summary = "Gets the current progress of credits detection.")]
    public class GetProgressRequest : IReturn<object> { }

    [Route(ApiRoutes.CancelDetection, "POST", Summary = "Cancels the currently running credits detection.")]
    public class CancelDetectionRequest : IReturn<object> { }

    [Route(ApiRoutes.ClearQueue, "POST", Summary = "Clears the processing queue.")]
    public class ClearQueueRequest : IReturn<object> { }

    [Route(ApiRoutes.ClearSeriesAveragingData, "POST", Summary = "Clears the series averaging data.")]
    public class ClearSeriesAveragingDataRequest : IReturn<object> { }

    [Route(ApiRoutes.GetSeriesMarkers, "GET", Summary = "Gets chapter markers for all episodes in a TV series.")]
    public class GetSeriesMarkersRequest : IReturn<object>
    {
        public string SeriesId { get; set; } = string.Empty;
    }

    [Route(ApiRoutes.TestOcrConnection, "POST", Summary = "Tests the OCR server connection.")]
    public class TestOcrConnectionRequest : IReturn<object>
    {
        public string OcrEndpoint { get; set; } = string.Empty;
    }

    [Route(ApiRoutes.DryRunSeries, "POST", Summary = "Dry run - detect credits without saving markers.")]
    public class DryRunSeriesRequest : IReturn<object>
    {
        public string SeriesId { get; set; } = string.Empty;
        public string EpisodeId { get; set; } = string.Empty;
    }

    [Route(ApiRoutes.DryRunSeriesDebug, "POST", Summary = "Dry run with debug logging - detect credits and capture debug log.")]
    public class DryRunSeriesDebugRequest : IReturn<object>
    {
        public string SeriesId { get; set; } = string.Empty;
        public string EpisodeId { get; set; } = string.Empty;
    }

    [Route(ApiRoutes.GetDebugLog, "GET", Summary = "Downloads the debug log from the last debug dry run.")]
    public class GetDebugLogRequest : IReturn<System.IO.Stream> { }

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

    [Route(ApiRoutes.GetImage, "GET", Summary = "Gets a plugin image resource.")]
    public class GetImageRequest : IReturn<System.IO.Stream>
    {
        public string ImageName { get; set; } = string.Empty;
    }

    [Route(ApiRoutes.ClearProcessedFiles, "POST", Summary = "Clears the processed files tracking list.")]
    public class ClearProcessedFilesRequest : IReturn<object> { }

    [Route(ApiRoutes.UpdateCreditsMarker, "POST", Summary = "Updates the credits marker timestamp for an episode.")]
    public class UpdateCreditsMarkerRequest : IReturn<object>
    {
        public string EpisodeId { get; set; } = string.Empty;
        public double CreditsStartSeconds { get; set; }
    }
}