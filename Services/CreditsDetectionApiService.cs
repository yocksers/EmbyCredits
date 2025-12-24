using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using EmbyCredits.Api;
using EmbyCredits.Services;

namespace EmbyCredits.Services
{
    public class CreditsDetectionApiService : IService
    {
        private readonly ILibraryManager _libraryManager;
        private readonly ILogger _logger;

        public CreditsDetectionApiService(ILibraryManager libraryManager, ILogManager logManager)
        {
            _libraryManager = libraryManager;
            _logger = logManager.GetLogger(GetType().Name);
        }

        public object Post(TriggerDetectionRequest request)
        {
            try
            {
                _logger.Info("Manual credits detection triggered");

                var episodes = _libraryManager.GetItemList(new InternalItemsQuery
                {
                    IncludeItemTypes = new[] { "Episode" },
                    IsVirtualItem = false,
                    HasPath = true,
                    Limit = request.Limit > 0 ? request.Limit : null
                }).OfType<Episode>().ToList();

                CreditsDetectionService.QueueSeries(episodes);

                return new { Success = true, Message = $"Queued {episodes.Count} episodes for processing" };
            }
            catch (Exception ex)
            {
                _logger.ErrorException("Error triggering credits detection", ex);
                return new { Success = false, Message = ex.Message };
            }
        }

        public object Post(ProcessEpisodeRequest request)
        {
            try
            {
                if (string.IsNullOrEmpty(request.ItemId))
                {
                    return new { Success = false, Message = "ItemId is required" };
                }

                var item = _libraryManager.GetItemById(request.ItemId);
                if (item is Episode episode)
                {
                    CreditsDetectionService.QueueEpisode(episode);
                    return new { Success = true, Message = $"Queued episode for processing: {episode.Name}" };
                }

                return new { Success = false, Message = "Item is not an episode" };
            }
            catch (Exception ex)
            {
                _logger.ErrorException("Error processing episode", ex);
                return new { Success = false, Message = ex.Message };
            }
        }

        public object Post(ProcessSeriesRequest request)
        {
            try
            {
                if (string.IsNullOrEmpty(request.SeriesId))
                {
                    return new { Success = false, Message = "SeriesId is required" };
                }

                Guid seriesGuid;
                if (!Guid.TryParse(request.SeriesId, out seriesGuid))
                {
                    return new { Success = false, Message = "Invalid SeriesId format" };
                }

                var series = _libraryManager.GetItemById(seriesGuid);
                if (series == null)
                {
                    return new { Success = false, Message = "Series not found" };
                }

                var seriesInternalId = series.InternalId;
                var episodes = _libraryManager.GetItemList(new InternalItemsQuery
                {
                    IncludeItemTypes = new[] { "Episode" },
                    IsVirtualItem = false,
                    HasPath = true
                }).OfType<Episode>()
                .Where(e => e.SeriesId == seriesInternalId)
                .ToList();

                _logger.Info($"Found {episodes.Count} episodes for series: {series.Name} (InternalId: {seriesInternalId})");

                CreditsDetectionService.QueueSeries(episodes);

                return new { Success = true, Message = $"Queued {episodes.Count} episodes from {series.Name} for processing", EpisodeCount = episodes.Count };
            }
            catch (Exception ex)
            {
                _logger.ErrorException("Error processing series", ex);
                return new { Success = false, Message = ex.Message };
            }
        }

        public object Get(GetAllSeriesRequest request)
        {
            try
            {
                var series = _libraryManager.GetItemList(new InternalItemsQuery
                {
                    IncludeItemTypes = new[] { "Series" },
                    IsVirtualItem = false,
                    Recursive = true
                }).Select(s => new
                {
                    Id = s.Id.ToString(),
                    Name = s.Name,
                    SortName = s.SortName,
                    Year = s.ProductionYear
                })
                .OrderBy(s => s.SortName)
                .ToList();

                return new { Success = true, Series = series, Count = series.Count };
            }
            catch (Exception ex)
            {
                _logger.ErrorException("Error getting series list", ex);
                return new { Success = false, Message = ex.Message };
            }
        }

        public object Get(GetProgressRequest request)
        {
            try
            {
                var progress = Plugin.Progress;

                return new
                {
                    Success = true,
                    IsRunning = progress.IsRunning,
                    TotalItems = progress.TotalItems,
                    ProcessedItems = progress.ProcessedItems,
                    SuccessfulItems = progress.SuccessfulItems,
                    FailedItems = progress.FailedItems,
                    CurrentItem = progress.CurrentItem,
                    CurrentItemProgress = progress.CurrentItemProgress,
                    PercentComplete = progress.PercentComplete,
                    EstimatedTimeRemaining = progress.EstimatedTimeRemaining?.ToString(@"hh\:mm\:ss"),
                    StartTime = progress.StartTime,
                    EndTime = progress.EndTime,
                    FailureReasons = progress.FailureReasons,
                    SuccessDetails = progress.SuccessDetails
                };
            }
            catch (Exception ex)
            {
                _logger.ErrorException("Error getting progress", ex);
                return new { Success = false, Message = ex.Message };
            }
        }

        public object Post(CancelDetectionRequest request)
        {
            try
            {
                CreditsDetectionService.CancelProcessing();
                return new { Success = true, Message = "Cancellation requested" };
            }
            catch (Exception ex)
            {
                _logger.ErrorException("Error cancelling detection", ex);
                return new { Success = false, Message = ex.Message };
            }
        }

        public object Get(GetSeriesMarkersRequest request)
        {
            try
            {
                if (string.IsNullOrEmpty(request.SeriesId))
                {
                    return new { Success = false, Message = "SeriesId is required" };
                }

                Guid seriesGuid;
                if (!Guid.TryParse(request.SeriesId, out seriesGuid))
                {
                    return new { Success = false, Message = "Invalid SeriesId format" };
                }

                var series = _libraryManager.GetItemById(seriesGuid);
                if (series == null)
                {
                    return new { Success = false, Message = "Series not found" };
                }

                var seriesInternalId = series.InternalId;
                var episodes = _libraryManager.GetItemList(new InternalItemsQuery
                {
                    IncludeItemTypes = new[] { "Episode" },
                    IsVirtualItem = false,
                    HasPath = true
                }).OfType<Episode>()
                .Where(e => e.SeriesId == seriesInternalId)
                .OrderBy(e => e.ParentIndexNumber)
                .ThenBy(e => e.IndexNumber)
                .ToList();

                var episodeMarkers = CreditsDetectionService.GetSeriesMarkers(episodes);

                return new 
                { 
                    Success = true, 
                    SeriesName = series.Name,
                    Episodes = episodeMarkers,
                    TotalEpisodes = episodeMarkers.Count
                };
            }
            catch (Exception ex)
            {
                _logger.ErrorException("Error getting series markers", ex);
                return new { Success = false, Message = ex.Message };
            }
        }

        public object Post(DryRunSeriesRequest request)
        {
            try
            {
                if (!string.IsNullOrEmpty(request.EpisodeId))
                {
                    var item = _libraryManager.GetItemById(request.EpisodeId);
                    if (item is Episode episode)
                    {
                        CreditsDetectionService.QueueEpisodeDryRun(episode);
                        return new { Success = true, Message = $"Dry run queued for episode: {episode.Name}" };
                    }
                    return new { Success = false, Message = "Item is not an episode" };
                }
                else if (!string.IsNullOrEmpty(request.SeriesId))
                {
                    Guid seriesGuid;
                    if (!Guid.TryParse(request.SeriesId, out seriesGuid))
                    {
                        return new { Success = false, Message = "Invalid SeriesId format" };
                    }

                    var series = _libraryManager.GetItemById(seriesGuid);
                    if (series == null)
                    {
                        return new { Success = false, Message = "Series not found" };
                    }

                    var seriesInternalId = series.InternalId;
                    var episodes = _libraryManager.GetItemList(new InternalItemsQuery
                    {
                        IncludeItemTypes = new[] { "Episode" },
                        IsVirtualItem = false,
                        HasPath = true
                    }).OfType<Episode>()
                    .Where(e => e.SeriesId == seriesInternalId)
                    .ToList();

                    _logger.Info($"Dry run: Found {episodes.Count} episodes for series: {series.Name}");

                    CreditsDetectionService.QueueSeriesDryRun(episodes);

                    return new { Success = true, Message = $"Dry run queued for {episodes.Count} episodes from {series.Name}", EpisodeCount = episodes.Count };
                }

                return new { Success = false, Message = "Either SeriesId or EpisodeId is required" };
            }
            catch (Exception ex)
            {
                _logger.ErrorException("Error in dry run", ex);
                return new { Success = false, Message = ex.Message };
            }
        }

        public async Task<object> Post(TestOcrConnectionRequest request)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(request.OcrEndpoint))
                {
                    return new { Success = false, Message = "OCR endpoint URL is required" };
                }

                var endpoint = request.OcrEndpoint.TrimEnd('/');

                using (var httpClient = new System.Net.Http.HttpClient())
                {
                    httpClient.Timeout = TimeSpan.FromSeconds(15);

                    try
                    {
                        var pingResponse = await httpClient.GetAsync(endpoint).ConfigureAwait(false);
                        if (!pingResponse.IsSuccessStatusCode)
                        {
                            return new { Success = false, Message = $"OCR server returned status: {pingResponse.StatusCode}" };
                        }
                    }
                    catch (System.Net.Http.HttpRequestException ex)
                    {
                        return new { Success = false, Message = $"Cannot connect to OCR server: {ex.Message}" };
                    }

                    try
                    {
                        var assembly = System.Reflection.Assembly.GetExecutingAssembly();
                        var resourceName = "EmbyCredits.Images.logo.jpg";

                        byte[] imageBytes;
                        using (var stream = assembly.GetManifestResourceStream(resourceName))
                        {
                            if (stream == null)
                            {
                                var availableResources = string.Join(", ", assembly.GetManifestResourceNames());
                                return new { Success = false, Message = $"Logo not found in embedded resources. Available: {availableResources}" };
                            }

                            using (var memoryStream = new System.IO.MemoryStream())
                            {
                                stream.CopyTo(memoryStream);
                                imageBytes = memoryStream.ToArray();
                            }
                        }

                        var content = new System.Net.Http.MultipartFormDataContent();
                        var imageContent = new System.Net.Http.ByteArrayContent(imageBytes);
                        imageContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");
                        content.Add(imageContent, "file", "logo.jpg");

                        var options = "{\"languages\":[\"eng\"]}";
                        content.Add(new System.Net.Http.StringContent(options), "options");

                        var ocrEndpoint = endpoint.TrimEnd('/') + "/tesseract";
                        var ocrResponse = await httpClient.PostAsync(ocrEndpoint, content).ConfigureAwait(false);

                        if (!ocrResponse.IsSuccessStatusCode)
                        {
                            return new { Success = false, Message = $"OCR processing failed with status: {ocrResponse.StatusCode}" };
                        }

                        var ocrResult = await ocrResponse.Content.ReadAsStringAsync().ConfigureAwait(false);

                        if (ocrResult.IndexOf("EmbyCredits", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            return new { Success = true, Message = "✓ Connection successful! OCR correctly detected 'EmbyCredits' text from test image." };
                        }
                        else
                        {
                            return new { Success = false, Message = $"OCR server responded but did not detect expected text. OCR returned: {ocrResult.Substring(0, Math.Min(100, ocrResult.Length))}..." };
                        }
                    }
                    catch (Exception ocrEx)
                    {
                        return new { Success = false, Message = $"OCR test failed: {ocrEx.Message}" };
                    }
                }
            }
            catch (TaskCanceledException)
            {
                return new { Success = false, Message = "Connection timed out (15 seconds)" };
            }
            catch (Exception ex)
            {
                _logger.ErrorException("Error testing OCR connection", ex);
                return new { Success = false, Message = $"Error: {ex.Message}" };
            }
        }
    }
}