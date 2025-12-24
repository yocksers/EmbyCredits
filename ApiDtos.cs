using MediaBrowser.Model.Services;

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
    public class GetAllSeriesRequest : IReturn<object> { }

    [Route(ApiRoutes.GetProgress, "GET", Summary = "Gets the current progress of credits detection.")]
    public class GetProgressRequest : IReturn<object> { }

    [Route(ApiRoutes.CancelDetection, "POST", Summary = "Cancels the currently running credits detection.")]
    public class CancelDetectionRequest : IReturn<object> { }

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
}