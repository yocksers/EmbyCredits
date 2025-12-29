using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
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
                _logger?.ErrorException("Error triggering credits detection", ex);
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

                BaseItem? item = null;
                
                if (Guid.TryParse(request.ItemId, out Guid itemGuid))
                {
                    item = _libraryManager.GetItemById(itemGuid);
                }
                else if (long.TryParse(request.ItemId, out long internalId))
                {
                    item = _libraryManager.GetItemById(internalId);
                }
                else
                {
                    return new { Success = false, Message = "Invalid ItemId format - must be GUID or InternalId" };
                }

                if (item == null)
                {
                    return new { Success = false, Message = "Item not found" };
                }

                if (item is Episode episode)
                {
                    CreditsDetectionService.QueueEpisode(episode);
                    return new { Success = true, Message = $"Queued episode for processing: {episode.Name}" };
                }

                return new { Success = false, Message = "Item is not an episode" };
            }
            catch (Exception ex)
            {
                _logger?.ErrorException("Error processing episode", ex);
                return new { Success = false, Message = ex.Message };
            }
        }

        public object Post(ProcessSeriesRequest request)
        {
            _logger?.Info("=== ProcessSeriesRequest received ===");
            
            try
            {
                _logger?.Info($"SeriesId: {request?.SeriesId ?? "NULL"}");
                
                if (request == null || string.IsNullOrEmpty(request.SeriesId))
                {
                    _logger?.Info("Returning error: SeriesId is required");
                    return new { Success = false, Message = "SeriesId is required" };
                }

                Guid seriesGuid;
                if (!Guid.TryParse(request.SeriesId, out seriesGuid))
                {
                    _logger?.Info($"SeriesId '{request.SeriesId}' is not a GUID, attempting to parse as InternalId");
                    
                    if (long.TryParse(request.SeriesId, out long internalId))
                    {
                        var seriesByInternalId = _libraryManager.GetItemById(internalId);
                        if (seriesByInternalId != null)
                        {
                            seriesGuid = seriesByInternalId.Id;
                            _logger?.Info($"Found series by InternalId: {seriesByInternalId.Name} (Guid: {seriesGuid})");
                        }
                        else
                        {
                            _logger?.Info($"No series found with InternalId: {internalId}");
                            return new { Success = false, Message = $"Series not found with InternalId: {internalId}" };
                        }
                    }
                    else
                    {
                        _logger?.Info($"SeriesId '{request.SeriesId}' is neither a GUID nor a valid InternalId");
                        return new { Success = false, Message = "Invalid SeriesId format - must be GUID or InternalId" };
                    }
                }

                var series = _libraryManager.GetItemById(seriesGuid);
                if (series == null)
                {
                    return new { Success = false, Message = "Series not found" };
                }

                _logger?.Info($"Processing series: {series.Name} (Id: {seriesGuid}, InternalId: {series.InternalId})");

                var allEpisodes = _libraryManager.GetItemList(new InternalItemsQuery
                {
                    IncludeItemTypes = new[] { "Episode" },
                    IsVirtualItem = false,
                    AncestorIds = new[] { series.InternalId }
                }).OfType<Episode>().ToList();

                _logger?.Info($"Found {allEpisodes.Count} total episodes (before path filter)");

                var episodes = allEpisodes.Where(e => !string.IsNullOrEmpty(e.Path) && File.Exists(e.Path)).ToList();

                _logger?.Info($"Found {episodes.Count} episodes with valid paths for series: {series.Name}");

                if (episodes.Count > 0)
                {
                    CreditsDetectionService.QueueSeries(episodes);
                }
                else if (allEpisodes.Count > 0)
                {
                    _logger?.Warn($"Series {series.Name} has {allEpisodes.Count} episodes but none have valid file paths");
                }

                return new { Success = true, Message = $"Queued {episodes.Count} episodes from {series.Name} for processing", EpisodeCount = episodes.Count };
            }
            catch (Exception ex)
            {
                _logger?.ErrorException("Error processing series", ex);
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
                _logger?.ErrorException("Error getting series list", ex);
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
                _logger?.ErrorException("Error getting progress", ex);
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
                _logger?.ErrorException("Error cancelling detection", ex);
                return new { Success = false, Message = ex.Message };
            }
        }

        public object Post(ClearQueueRequest request)
        {
            try
            {
                var clearedCount = CreditsDetectionService.ClearQueue();
                return new { Success = true, Message = $"Queue cleared: {clearedCount} items removed", ClearedCount = clearedCount };
            }
            catch (Exception ex)
            {
                _logger?.ErrorException("Error clearing queue", ex);
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
                    if (long.TryParse(request.SeriesId, out long internalId))
                    {
                        var seriesByInternalId = _libraryManager.GetItemById(internalId);
                        if (seriesByInternalId != null)
                        {
                            seriesGuid = seriesByInternalId.Id;
                        }
                        else
                        {
                            return new { Success = false, Message = $"Series not found with InternalId: {internalId}" };
                        }
                    }
                    else
                    {
                        return new { Success = false, Message = "Invalid SeriesId format - must be GUID or InternalId" };
                    }
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
                    HasPath = true,
                    AncestorIds = new[] { seriesInternalId }
                }).OfType<Episode>()
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
                _logger?.ErrorException("Error getting series markers", ex);
                return new { Success = false, Message = ex.Message };
            }
        }

        public object Post(DryRunSeriesRequest request)
        {
            _logger?.Info($"=== DryRunSeriesRequest START ===");
            _logger?.Info($"Request: EpisodeId={request?.EpisodeId ?? "NULL"}, SeriesId={request?.SeriesId ?? "NULL"}");
            
            try
            {
                if (request == null)
                {
                    _logger?.Info("Request is NULL");
                    return new { Success = false, Message = "Request is null" };
                }

                if (!string.IsNullOrEmpty(request.EpisodeId))
                {
                    _logger?.Info($"Processing single episode: {request.EpisodeId}");
                    var item = _libraryManager.GetItemById(request.EpisodeId);
                    if (item is Episode episode)
                    {
                        _logger?.Info($"Episode found: {episode.Name}");
                        CreditsDetectionService.QueueEpisodeDryRun(episode);
                        return new { Success = true, Message = $"Dry run queued for episode: {episode.Name}" };
                    }
                    _logger?.Info("Item is not an episode");
                    return new { Success = false, Message = "Item is not an episode" };
                }
                else if (!string.IsNullOrEmpty(request.SeriesId))
                {
                    _logger?.Info($"Processing series: {request.SeriesId}");
                    
                    Guid seriesGuid;
                    if (!Guid.TryParse(request.SeriesId, out seriesGuid))
                    {
                        _logger?.Info($"SeriesId '{request.SeriesId}' is not a GUID, attempting to parse as InternalId");
                        
                        if (long.TryParse(request.SeriesId, out long internalId))
                        {
                            var seriesByInternalId = _libraryManager.GetItemById(internalId);
                            if (seriesByInternalId != null)
                            {
                                seriesGuid = seriesByInternalId.Id;
                                _logger?.Info($"Found series via InternalId {internalId}: {seriesByInternalId.Name}");
                            }
                            else
                            {
                                _logger?.Info($"Series not found with InternalId: {internalId}");
                                return new { Success = false, Message = $"Series not found with InternalId: {internalId}" };
                            }
                        }
                        else
                        {
                            _logger?.Info($"Invalid SeriesId format - must be GUID or InternalId: {request.SeriesId}");
                            return new { Success = false, Message = "Invalid SeriesId format - must be GUID or InternalId" };
                        }
                    }

                    _logger?.Info($"Looking up series with GUID: {seriesGuid}");
                    var series = _libraryManager.GetItemById(seriesGuid);
                    if (series == null)
                    {
                        _logger?.Info($"Series not found for GUID: {seriesGuid}");
                        return new { Success = false, Message = "Series not found" };
                    }

                    _logger?.Info($"Series found: {series.Name}, InternalId: {series.InternalId}");
                    
                    var seriesInternalId = series.InternalId;
                    var episodes = _libraryManager.GetItemList(new InternalItemsQuery
                    {
                        IncludeItemTypes = new[] { "Episode" },
                        IsVirtualItem = false,
                        HasPath = true,
                        AncestorIds = new[] { seriesInternalId }
                    }).OfType<Episode>()
                    .ToList();

                    _logger?.Info($"Dry run: Found {episodes.Count} episodes for series: {series.Name}");

                    CreditsDetectionService.QueueSeriesDryRun(episodes);

                    _logger?.Info($"QueueSeriesDryRun called successfully");
                    return new { Success = true, Message = $"Dry run queued for {episodes.Count} episodes from {series.Name}", EpisodeCount = episodes.Count };
                }

                _logger?.Info("Neither EpisodeId nor SeriesId provided");
                return new { Success = false, Message = "Either SeriesId or EpisodeId is required" };
            }
            catch (Exception ex)
            {
                _logger?.ErrorException("Error in dry run", ex);
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
                _logger?.ErrorException("Error testing OCR connection", ex);
                return new { Success = false, Message = $"Error: {ex.Message}" };
            }
        }

        public Stream Get(GetImageRequest request)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(request.ImageName))
                {
                    _logger.Warn("Image request with empty ImageName");
                    return Stream.Null;
                }

                var assembly = typeof(Plugin).GetTypeInfo().Assembly;
                var resourceName = $"EmbyCredits.Images.{request.ImageName}";
                
                var stream = assembly.GetManifestResourceStream(resourceName);
                
                if (stream == null)
                {
                    _logger.Warn($"Image not found: {resourceName}");
                    return Stream.Null;
                }

                return stream;
            }
            catch (Exception ex)
            {
                _logger?.ErrorException($"Error getting image: {request.ImageName}", ex);
                return Stream.Null;
            }
        }

        public object Post(DryRunSeriesDebugRequest request)
        {
            _logger?.Info($"=== DryRunSeriesDebugRequest START ===");
            _logger?.Info($"Request: EpisodeId={request?.EpisodeId ?? "NULL"}, SeriesId={request?.SeriesId ?? "NULL"}");
            
            try
            {
                if (request == null)
                {
                    _logger?.Info("Request is NULL");
                    return new { Success = false, Message = "Request is null" };
                }

                if (!string.IsNullOrEmpty(request.EpisodeId))
                {
                    _logger?.Info($"Processing single episode with debug: {request.EpisodeId}");
                    var item = _libraryManager.GetItemById(request.EpisodeId);
                    if (item is Episode episode)
                    {
                        _logger?.Info($"Episode found: {episode.Name}");
                        CreditsDetectionService.QueueEpisodeDryRunDebug(episode);
                        return new { Success = true, Message = $"Debug dry run queued for episode: {episode.Name}" };
                    }
                    _logger?.Info("Item is not an episode");
                    return new { Success = false, Message = "Item is not an episode" };
                }
                else if (!string.IsNullOrEmpty(request.SeriesId))
                {
                    _logger?.Info($"Processing series with debug: {request.SeriesId}");
                    
                    Guid seriesGuid;
                    if (!Guid.TryParse(request.SeriesId, out seriesGuid))
                    {
                        _logger?.Info($"SeriesId '{request.SeriesId}' is not a GUID, attempting to parse as InternalId");
                        
                        if (long.TryParse(request.SeriesId, out long internalId))
                        {
                            var seriesByInternalId = _libraryManager.GetItemById(internalId);
                            if (seriesByInternalId != null)
                            {
                                seriesGuid = seriesByInternalId.Id;
                                _logger?.Info($"Found series via InternalId {internalId}: {seriesByInternalId.Name}");
                            }
                            else
                            {
                                _logger?.Info($"Series not found with InternalId: {internalId}");
                                return new { Success = false, Message = $"Series not found with InternalId: {internalId}" };
                            }
                        }
                        else
                        {
                            _logger?.Info($"Invalid SeriesId format - must be GUID or InternalId: {request.SeriesId}");
                            return new { Success = false, Message = "Invalid SeriesId format - must be GUID or InternalId" };
                        }
                    }

                    _logger?.Info($"Looking up series with GUID: {seriesGuid}");
                    var series = _libraryManager.GetItemById(seriesGuid);
                    if (series == null)
                    {
                        _logger?.Info($"Series not found for GUID: {seriesGuid}");
                        return new { Success = false, Message = "Series not found" };
                    }

                    _logger?.Info($"Series found: {series.Name}, InternalId: {series.InternalId}");
                    
                    var seriesInternalId = series.InternalId;
                    var episodes = _libraryManager.GetItemList(new InternalItemsQuery
                    {
                        IncludeItemTypes = new[] { "Episode" },
                        IsVirtualItem = false,
                        HasPath = true,
                        AncestorIds = new[] { seriesInternalId }
                    }).OfType<Episode>()
                    .ToList();

                    _logger?.Info($"Debug dry run: Found {episodes.Count} episodes for series: {series.Name}");

                    CreditsDetectionService.QueueSeriesDryRunDebug(episodes);

                    _logger?.Info($"QueueSeriesDryRunDebug called successfully");
                    return new { Success = true, Message = $"Debug dry run queued for {episodes.Count} episodes from {series.Name}", EpisodeCount = episodes.Count };
                }

                _logger?.Info("Neither EpisodeId nor SeriesId provided");
                return new { Success = false, Message = "Either SeriesId or EpisodeId is required" };
            }
            catch (Exception ex)
            {
                _logger?.ErrorException("Error in debug dry run", ex);
                return new { Success = false, Message = ex.Message };
            }
        }

        public object Get(GetDebugLogRequest request)
        {
            try
            {
                _logger?.Info("Debug log requested");
                var debugLog = CreditsDetectionService.GetDebugLog();
                
                var bytes = System.Text.Encoding.UTF8.GetBytes(debugLog);
                var stream = new MemoryStream(bytes);
                stream.Position = 0; // Reset position for reading
                return stream;
            }
            catch (Exception ex)
            {
                _logger?.ErrorException("Error getting debug log", ex);
                var errorMessage = $"Error retrieving debug log: {ex.Message}\n\nStack Trace:\n{ex.StackTrace}";
                var errorBytes = System.Text.Encoding.UTF8.GetBytes(errorMessage);
                var errorStream = new MemoryStream(errorBytes);
                errorStream.Position = 0;
                return errorStream;
            }
        }

        public async Task<object> Post(ExportCreditsBackupRequest request)
        {
            try
            {
                _logger?.Info("Credits backup export requested");
                
                var backupService = Plugin.CreditsBackupService;
                if (backupService == null)
                {
                    return new { Success = false, Message = "Backup service not initialized" };
                }

                var result = await backupService.ExportCreditsMarkers(
                    request.LibraryIds,
                    request.SeriesIds
                );

                if (!result.Success || string.IsNullOrEmpty(result.JsonData))
                {
                    return new { Success = false, Message = result.Message };
                }

                var bytes = System.Text.Encoding.UTF8.GetBytes(result.JsonData);
                var stream = new MemoryStream(bytes);
                stream.Position = 0;
                return stream;
            }
            catch (Exception ex)
            {
                _logger?.ErrorException("Error exporting credits backup", ex);
                return new { Success = false, Message = ex.Message };
            }
        }

        public async Task<object> Post(ImportCreditsBackupRequest request)
        {
            try
            {
                _logger?.Info("Credits backup import requested");
                
                if (string.IsNullOrEmpty(request.JsonData))
                {
                    return new { Success = false, Message = "No backup data provided" };
                }

                var backupService = Plugin.CreditsBackupService;
                if (backupService == null)
                {
                    return new { Success = false, Message = "Backup service not initialized" };
                }

                var result = await backupService.ImportCreditsMarkers(
                    request.JsonData,
                    request.OverwriteExisting
                );

                return new
                {
                    Success = result.Success,
                    Message = result.Message,
                    ItemsImported = result.ItemsImported,
                    ItemsSkipped = result.ItemsSkipped,
                    ItemsNotFound = result.ItemsNotFound
                };
            }
            catch (Exception ex)
            {
                _logger?.ErrorException("Error importing credits backup", ex);
                return new { Success = false, Message = ex.Message };
            }
        }
    }
}