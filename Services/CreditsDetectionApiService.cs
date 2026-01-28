using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
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
using EmbyCredits.Services.Utilities;

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

                var allEpisodes = _libraryManager.GetItemList(new InternalItemsQuery
                {
                    IncludeItemTypes = new[] { "Episode" },
                    IsVirtualItem = false,
                    HasPath = true,
                    Limit = request.Limit > 0 ? request.Limit : null
                }).OfType<Episode>().ToList();

                var episodes = allEpisodes.Where(e => e.ParentIndexNumber != null && e.ParentIndexNumber != 0).ToList();
                var specialCount = allEpisodes.Count - episodes.Count;

                CreditsDetectionService.QueueSeries(episodes);

                return new { Success = true, Message = $"Queued {episodes.Count} episodes for processing (excluded {specialCount} specials)" };
            }
            catch (Exception ex)
            {
                _logger?.ErrorException("Error triggering credits detection", ex);
                return new { Success = false, Message = ex.Message };
            }
        }

        public object Post(ProcessEpisodeRequest request)
        {
            var result = RequestProcessorHelper.ProcessDetectionRequest(
                _libraryManager,
                episodeId: request.ItemId,
                seriesId: null,
                libraryId: null,
                processEpisode: episode => CreditsDetectionService.QueueEpisodeManual(episode, request.SkipExistingMarkers),
                processSeries: episodes => CreditsDetectionService.QueueSeriesManual(episodes, request.SkipExistingMarkers),
                _logger);

            return new { result.Success, result.Message };
        }

        public object Post(ProcessSeriesRequest request)
        {
            _logger?.Info("=== ProcessSeriesRequest received ===");

            var result = RequestProcessorHelper.ProcessDetectionRequest(
                _libraryManager,
                episodeId: null,
                seriesId: request.SeriesId,
                libraryId: null,
                processEpisode: episode => CreditsDetectionService.QueueEpisodeManual(episode, request.SkipExistingMarkers),
                processSeries: episodes => CreditsDetectionService.QueueSeriesManual(episodes, request.SkipExistingMarkers),
                _logger);

            return new { result.Success, result.Message, EpisodeCount = result.ItemCount };
        }

        public object Post(ProcessSeasonRequest request)
        {
            _logger?.Info($"=== ProcessSeasonRequest received for Season {request.SeasonNumber} ===");

            try
            {
                if (string.IsNullOrEmpty(request.SeriesId))
                {
                    return new { Success = false, Message = "SeriesId is required" };
                }

                var series = ItemLookupHelper.ResolveSeries(_libraryManager, request.SeriesId, _logger);
                if (series == null)
                {
                    return new { Success = false, Message = "Series not found" };
                }

                var allEpisodes = ItemLookupHelper.GetSeriesEpisodes(_libraryManager, series.InternalId, _logger);
                
                if (request.SeasonNumber == 0)
                {
                    return new { Success = false, Message = "Season 0 (specials) are not supported for credits detection" };
                }
                
                var seasonEpisodes = allEpisodes.Where(e => e.ParentIndexNumber == request.SeasonNumber).ToList();

                if (seasonEpisodes.Count == 0)
                {
                    return new
                    {
                        Success = true,
                        Message = $"No episodes found for Season {request.SeasonNumber}",
                        EpisodeCount = 0
                    };
                }

                CreditsDetectionService.QueueSeriesManual(seasonEpisodes, request.SkipExistingMarkers);

                return new
                {
                    Success = true,
                    Message = $"Queued {seasonEpisodes.Count} episodes from Season {request.SeasonNumber}",
                    EpisodeCount = seasonEpisodes.Count
                };
            }
            catch (Exception ex)
            {
                _logger?.ErrorException($"Error processing season {request.SeasonNumber}", ex);
                return new { Success = false, Message = ex.Message };
            }
        }

        public object Post(ProcessSeasonMissingMarkersRequest request)
        {
            _logger?.Info($"=== ProcessSeasonMissingMarkersRequest received for Season {request.SeasonNumber} ===");

            try
            {
                if (string.IsNullOrEmpty(request.SeriesId))
                {
                    return new { Success = false, Message = "SeriesId is required" };
                }

                var series = ItemLookupHelper.ResolveSeries(_libraryManager, request.SeriesId, _logger);
                if (series == null)
                {
                    return new { Success = false, Message = "Series not found" };
                }

                var allEpisodes = ItemLookupHelper.GetSeriesEpisodes(_libraryManager, series.InternalId, _logger);
                
                if (request.SeasonNumber == 0)
                {
                    return new { Success = false, Message = "Season 0 (specials) are not supported for credits detection" };
                }
                
                var seasonEpisodes = allEpisodes.Where(e => e.ParentIndexNumber == request.SeasonNumber).ToList();

                _logger?.Info($"Found {seasonEpisodes.Count} episodes in Season {request.SeasonNumber}");

                if (seasonEpisodes.Count == 0)
                {
                    return new
                    {
                        Success = true,
                        Message = $"No episodes found for Season {request.SeasonNumber}",
                        EpisodeCount = 0
                    };
                }

                                var episodeMarkers = CreditsDetectionService.GetSeriesMarkers(seasonEpisodes);
                
                                var episodesWithMarkers = new HashSet<string>();
                foreach (var marker in episodeMarkers)
                {
                    var markerType = marker.GetType();
                    var hasCreditsMarkerProp = markerType.GetProperty("HasCreditsMarker");
                    var episodeIdProp = markerType.GetProperty("EpisodeId");
                    
                    if (hasCreditsMarkerProp != null && episodeIdProp != null)
                    {
                        var hasMarkerValue = hasCreditsMarkerProp.GetValue(marker);
                        var hasMarker = hasMarkerValue != null && (bool)hasMarkerValue;
                        var episodeId = episodeIdProp.GetValue(marker)?.ToString();
                        
                        if (hasMarker && episodeId != null)
                        {
                            episodesWithMarkers.Add(episodeId);
                            _logger?.Info($"Episode {episodeId} already has credits marker");
                        }
                    }
                }

                                var episodesWithoutMarkers = seasonEpisodes
                    .Where(ep => !episodesWithMarkers.Contains(ep.Id.ToString()))
                    .ToList();
                
                _logger?.Info($"Found {episodesWithoutMarkers.Count} episodes without markers (out of {seasonEpisodes.Count} total)");

                if (episodesWithoutMarkers.Count == 0)
                {
                    return new
                    {
                        Success = true,
                        Message = $"All episodes in Season {request.SeasonNumber} already have credits markers",
                        EpisodeCount = 0,
                        TotalEpisodes = seasonEpisodes.Count
                    };
                }

                CreditsDetectionService.QueueSeriesManual(episodesWithoutMarkers, false);

                return new
                {
                    Success = true,
                    Message = $"Queued {episodesWithoutMarkers.Count} episodes (out of {seasonEpisodes.Count}) missing credits markers from Season {request.SeasonNumber}",
                    EpisodeCount = episodesWithoutMarkers.Count,
                    TotalEpisodes = seasonEpisodes.Count
                };
            }
            catch (Exception ex)
            {
                _logger?.ErrorException($"Error processing season {request.SeasonNumber} for missing markers", ex);
                return new { Success = false, Message = ex.Message };
            }
        }

        public object Post(BatchUpdateSeasonMissingMarkersRequest request)
        {
            _logger?.Info($"=== BatchUpdateSeasonMissingMarkersRequest received for Season {request.SeasonNumber} ===");

            try
            {
                if (string.IsNullOrEmpty(request.SeriesId))
                {
                    return new { Success = false, Message = "SeriesId is required" };
                }

                if (request.CreditsStartSeconds < 0)
                {
                    return new { Success = false, Message = "CreditsStartSeconds must be positive" };
                }

                var series = ItemLookupHelper.ResolveSeries(_libraryManager, request.SeriesId, _logger);
                if (series == null)
                {
                    return new { Success = false, Message = "Series not found" };
                }

                var allEpisodes = ItemLookupHelper.GetSeriesEpisodes(_libraryManager, series.InternalId, _logger);
                
                if (request.SeasonNumber == 0)
                {
                    return new { Success = false, Message = "Season 0 (specials) are not supported for credits detection" };
                }
                
                var seasonEpisodes = allEpisodes.Where(e => e.ParentIndexNumber == request.SeasonNumber).ToList();

                _logger?.Info($"Found {seasonEpisodes.Count} episodes in Season {request.SeasonNumber}");

                if (seasonEpisodes.Count == 0)
                {
                    return new
                    {
                        Success = true,
                        Message = $"No episodes found for Season {request.SeasonNumber}",
                        EpisodeCount = 0
                    };
                }

                                var episodeMarkers = CreditsDetectionService.GetSeriesMarkers(seasonEpisodes);
                
                                var episodesWithMarkers = new HashSet<string>();
                foreach (var marker in episodeMarkers)
                {
                    var markerType = marker.GetType();
                    var hasCreditsMarkerProp = markerType.GetProperty("HasCreditsMarker");
                    var episodeIdProp = markerType.GetProperty("EpisodeId");
                    
                    if (hasCreditsMarkerProp != null && episodeIdProp != null)
                    {
                        var hasMarkerValue = hasCreditsMarkerProp.GetValue(marker);
                        var hasMarker = hasMarkerValue != null && (bool)hasMarkerValue;
                        var episodeId = episodeIdProp.GetValue(marker)?.ToString();
                        
                        if (hasMarker && episodeId != null)
                        {
                            episodesWithMarkers.Add(episodeId);
                        }
                    }
                }

                                var episodesWithoutMarkers = seasonEpisodes
                    .Where(ep => !episodesWithMarkers.Contains(ep.Id.ToString()))
                    .ToList();
                
                _logger?.Info($"Found {episodesWithoutMarkers.Count} episodes without markers (out of {seasonEpisodes.Count} total)");

                if (episodesWithoutMarkers.Count == 0)
                {
                    return new
                    {
                        Success = true,
                        Message = $"All episodes in Season {request.SeasonNumber} already have credits markers",
                        EpisodeCount = 0,
                        TotalEpisodes = seasonEpisodes.Count
                    };
                }

                var chapterMarkerService = CreditsDetectionService.GetChapterMarkerService();
                if (chapterMarkerService == null)
                {
                    return new { Success = false, Message = "Chapter marker service not available" };
                }

                var successCount = 0;
                var failedEpisodes = new List<string>();

                foreach (var episode in episodesWithoutMarkers)
                {
                    try
                    {
                        if (!episode.RunTimeTicks.HasValue || episode.RunTimeTicks.Value <= 0)
                        {
                            failedEpisodes.Add($"{episode.Name} (no runtime)");
                            _logger?.Warn($"Skipping {episode.Name} - no valid runtime information");
                            continue;
                        }

                        var durationSeconds = episode.RunTimeTicks.Value / (double)TimeSpan.TicksPerSecond;
                        double actualCreditsStartSeconds;
                        
                        if (request.IsRelativeFromEnd)
                        {
                            actualCreditsStartSeconds = durationSeconds - Math.Abs(request.CreditsStartSeconds);
                            
                            if (actualCreditsStartSeconds < 0)
                            {
                                failedEpisodes.Add($"{episode.Name} (offset {Math.Abs(request.CreditsStartSeconds):F1}s > duration {durationSeconds:F1}s)");
                                _logger?.Warn($"Skipping {episode.Name} - offset from end exceeds video duration");
                                continue;
                            }
                        }
                        else
                        {
                            actualCreditsStartSeconds = request.CreditsStartSeconds;
                            
                            if (actualCreditsStartSeconds >= durationSeconds)
                            {
                                failedEpisodes.Add($"{episode.Name} (timestamp {request.CreditsStartSeconds:F1}s > duration {durationSeconds:F1}s)");
                                _logger?.Warn($"Skipping {episode.Name} - timestamp exceeds video duration");
                                continue;
                            }
                        }

                        chapterMarkerService.SaveCreditsMarker(episode, actualCreditsStartSeconds);
                        successCount++;
                        _logger?.Info($"Set credits marker at {FormatTime(actualCreditsStartSeconds)} for {episode.Name}");
                    }
                    catch (Exception ex)
                    {
                        failedEpisodes.Add($"{episode.Name} ({ex.Message})");
                        _logger?.ErrorException($"Failed to set marker for {episode.Name}", ex);
                    }
                }

                var timeFormatted = request.IsRelativeFromEnd 
                    ? $"-{FormatTime(Math.Abs(request.CreditsStartSeconds))} (from end)"
                    : FormatTime(request.CreditsStartSeconds);
                
                if (failedEpisodes.Count == 0)
                {
                    return new
                    {
                        Success = true,
                        Message = $"Successfully set credits marker at {timeFormatted} for {successCount} episode(s) in Season {request.SeasonNumber}",
                        EpisodeCount = successCount,
                        TotalEpisodes = seasonEpisodes.Count
                    };
                }
                else
                {
                    return new
                    {
                        Success = true,
                        Message = $"Set credits marker for {successCount} episode(s), {failedEpisodes.Count} failed. Time: {timeFormatted}",
                        EpisodeCount = successCount,
                        FailedCount = failedEpisodes.Count,
                        FailedEpisodes = failedEpisodes
                    };
                }
            }
            catch (Exception ex)
            {
                _logger?.ErrorException($"Error batch updating season {request.SeasonNumber}", ex);
                return new { Success = false, Message = ex.Message };
            }
        }

        private string FormatTime(double seconds)
        {
            var ts = TimeSpan.FromSeconds(seconds);
            return $"{(int)ts.TotalMinutes}:{ts.Seconds:D2}";
        }

        public object Post(ProcessLibraryRequest request)
        {
            _logger?.Info("=== ProcessLibraryRequest received ===");

            var result = RequestProcessorHelper.ProcessDetectionRequest(
                _libraryManager,
                episodeId: null,
                seriesId: null,
                libraryId: request.LibraryId,
                processEpisode: episode => CreditsDetectionService.QueueEpisodeManual(episode, request.SkipExistingMarkers),
                processSeries: episodes => CreditsDetectionService.QueueSeriesManual(episodes, request.SkipExistingMarkers),
                _logger);

            return new { result.Success, result.Message, EpisodeCount = result.ItemCount };
        }

        public object Get(GetAllSeriesRequest request)
        {
            try
            {
                var query = new InternalItemsQuery
                {
                    IncludeItemTypes = new[] { "Series" },
                    IsVirtualItem = false,
                    Recursive = true
                };

                if (!string.IsNullOrEmpty(request.LibraryId) && long.TryParse(request.LibraryId, out long libraryId))
                {
                    query.AncestorIds = new[] { libraryId };
                }

                var series = _libraryManager.GetItemList(query).Select(s => new
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
                    EstimatedTimeRemainingSeconds = progress.EstimatedTimeRemaining?.TotalSeconds,
                    StartTime = progress.StartTime,
                    EndTime = progress.EndTime,
                    FailureReasons = progress.FailureReasons,
                    SuccessDetails = progress.SuccessDetails,
                    ConfidenceScores = progress.ConfidenceScores,
                    ThumbnailPaths = progress.ThumbnailPaths,
                    EpisodeIds = progress.EpisodeIds
                };
            }
            catch (Exception ex)
            {
                _logger?.ErrorException("Error getting progress", ex);
                return new { Success = false, Message = ex.Message };
            }
        }

        public object Get(GetBackupExportProgressRequest request)
        {
            try
            {
                var progress = Plugin.BackupExportProgress;

                return new
                {
                    Success = true,
                    IsRunning = progress.IsRunning,
                    TotalItems = progress.TotalItems,
                    ProcessedItems = progress.ProcessedItems,
                    SuccessfulItems = progress.SuccessfulItems,
                    FailedItems = progress.FailedItems,
                    CurrentItem = progress.CurrentItem,
                    PercentComplete = progress.PercentComplete,
                    EstimatedTimeRemainingSeconds = progress.EstimatedTimeRemaining?.TotalSeconds
                };
            }
            catch (Exception ex)
            {
                _logger?.ErrorException("Error getting backup export progress", ex);
                return new { Success = false, Message = ex.Message };
            }
        }

        public object Get(GetBackupImportProgressRequest request)
        {
            try
            {
                var progress = Plugin.BackupImportProgress;

                return new
                {
                    Success = true,
                    IsRunning = progress.IsRunning,
                    TotalItems = progress.TotalItems,
                    ProcessedItems = progress.ProcessedItems,
                    SuccessfulItems = progress.SuccessfulItems,
                    FailedItems = progress.FailedItems,
                    CurrentItem = progress.CurrentItem,
                    PercentComplete = progress.PercentComplete,
                    EstimatedTimeRemainingSeconds = progress.EstimatedTimeRemaining?.TotalSeconds
                };
            }
            catch (Exception ex)
            {
                _logger?.ErrorException("Error getting backup import progress", ex);
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
                var allEpisodes = _libraryManager.GetItemList(new InternalItemsQuery
                {
                    IncludeItemTypes = new[] { "Episode" },
                    IsVirtualItem = false,
                    HasPath = true,
                    AncestorIds = new[] { seriesInternalId }
                }).OfType<Episode>()
                .Where(e => e.ParentIndexNumber != null && e.ParentIndexNumber != 0)
                .OrderBy(e => e.ParentIndexNumber)
                .ThenBy(e => e.IndexNumber)
                .ToList();

                var episodeMarkers = CreditsDetectionService.GetSeriesMarkers(allEpisodes);

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

        public object Get(GetSeasonValidationRequest request)
        {
            try
            {
                if (string.IsNullOrEmpty(request.SeriesId))
                {
                    return new { Success = false, Message = "SeriesId is required" };
                }

                var series = ItemLookupHelper.ResolveSeries(_libraryManager, request.SeriesId, _logger);
                if (series == null)
                {
                    return new { Success = false, Message = "Series not found" };
                }

                var allEpisodes = ItemLookupHelper.GetSeriesEpisodes(_libraryManager, series.InternalId, _logger);

                if (request.SeasonNumber == 0)
                {
                    return new { Success = false, Message = "Season 0 (specials) are not supported" };
                }

                var seasonEpisodes = allEpisodes
                    .Where(e => e.ParentIndexNumber == request.SeasonNumber)
                    .OrderBy(e => e.IndexNumber)
                    .ToList();

                if (seasonEpisodes.Count == 0)
                {
                    return new { Success = false, Message = $"No episodes found for Season {request.SeasonNumber}" };
                }

                var episodeMarkers = CreditsDetectionService.GetSeriesMarkers(seasonEpisodes);
                var validationData = new List<object>();

                foreach (var episode in seasonEpisodes)
                {
                    var marker = episodeMarkers.FirstOrDefault(m =>
                    {
                        var markerType = m.GetType();
                        var episodeIdProp = markerType.GetProperty("EpisodeId");
                        if (episodeIdProp != null)
                        {
                            var episodeId = episodeIdProp.GetValue(m)?.ToString();
                            return episodeId == episode.Id.ToString();
                        }
                        return false;
                    });

                    object? markerData = null;
                    if (marker != null)
                    {
                        var markerType = marker.GetType();
                        var hasCreditsMarkerProp = markerType.GetProperty("HasCreditsMarker");
                        var markersProp = markerType.GetProperty("Markers");

                        var hasMarker = hasCreditsMarkerProp != null &&
                                       hasCreditsMarkerProp.GetValue(marker) is bool boolVal && boolVal;

                        if (hasMarker && markersProp != null)
                        {
                            var markers = markersProp.GetValue(marker);
                            if (markers is System.Collections.IEnumerable enumerable)
                            {
                                foreach (var m in enumerable)
                                {
                                    var mType = m.GetType();
                                    var startTimeProp = mType.GetProperty("StartTime");
                                    if (startTimeProp != null)
                                    {
                                        markerData = new
                                        {
                                            HasMarker = true,
                                            StartTime = startTimeProp.GetValue(m)?.ToString()
                                        };
                                        break;
                                    }
                                }
                            }
                        }
                    }

                    if (markerData == null)
                    {
                        markerData = new { HasMarker = false, StartTime = (string?)null };
                    }

                    validationData.Add(new
                    {
                        EpisodeId = episode.Id.ToString(),
                        EpisodeNumber = episode.IndexNumber ?? 0,
                        EpisodeName = episode.Name,
                        Duration = episode.RunTimeTicks.HasValue
                            ? FormatTime(episode.RunTimeTicks.Value / TimeSpan.TicksPerSecond)
                            : "Unknown",
                        DurationSeconds = episode.RunTimeTicks.HasValue
                            ? episode.RunTimeTicks.Value / (double)TimeSpan.TicksPerSecond
                            : 0,
                        Marker = markerData
                    });
                }

                return new
                {
                    Success = true,
                    SeriesName = series.Name,
                    SeasonNumber = request.SeasonNumber,
                    Episodes = validationData
                };
            }
            catch (Exception ex)
            {
                _logger?.ErrorException("Error getting season validation data", ex);
                return new { Success = false, Message = ex.Message };
            }
        }

        public object Post(DryRunSeriesRequest request)
        {
            _logger?.Info("=== DryRunSeriesRequest START ===");

            if (request?.SeasonNumber.HasValue == true && !string.IsNullOrEmpty(request.SeriesId))
            {
                try
                {
                    var series = ItemLookupHelper.ResolveSeries(_libraryManager, request.SeriesId, _logger);
                    if (series == null)
                    {
                        return new { Success = false, Message = "Series not found" };
                    }

                    var allEpisodes = ItemLookupHelper.GetSeriesEpisodes(_libraryManager, series.InternalId, _logger);
                    var seasonEpisodes = allEpisodes.Where(e => e.ParentIndexNumber == request.SeasonNumber.Value).ToList();

                    if (seasonEpisodes.Count == 0)
                    {
                        return new { Success = true, Message = $"No episodes found for Season {request.SeasonNumber.Value}", EpisodeCount = 0 };
                    }

                    CreditsDetectionService.QueueSeriesDryRun(seasonEpisodes, request.SkipExistingMarkers);
                    return new { Success = true, Message = $"Dry run started for {seasonEpisodes.Count} episodes from Season {request.SeasonNumber.Value}", EpisodeCount = seasonEpisodes.Count };
                }
                catch (Exception ex)
                {
                    _logger?.ErrorException($"Error starting dry run for season {request.SeasonNumber.Value}", ex);
                    return new { Success = false, Message = ex.Message };
                }
            }

            var result = RequestProcessorHelper.ProcessDetectionRequest(
                _libraryManager,
                episodeId: request?.EpisodeId,
                seriesId: request?.SeriesId,
                libraryId: request?.LibraryId,
                processEpisode: episode => CreditsDetectionService.QueueEpisodeDryRun(episode, request?.SkipExistingMarkers ?? false),
                processSeries: episodes => CreditsDetectionService.QueueSeriesDryRun(episodes, request?.SkipExistingMarkers ?? false),
                _logger);

            return new { result.Success, result.Message, EpisodeCount = result.ItemCount };
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
                        using (var pingResponse = await httpClient.GetAsync(endpoint).ConfigureAwait(false))
                        {
                            if (!pingResponse.IsSuccessStatusCode)
                            {
                                return new { Success = false, Message = $"OCR server returned status: {pingResponse.StatusCode}" };
                            }
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

                        using (var content = new System.Net.Http.MultipartFormDataContent())
                        {
                            var imageContent = new System.Net.Http.ByteArrayContent(imageBytes);
                            imageContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");
                            content.Add(imageContent, "file", "logo.jpg");

                            var options = "{\"languages\":[\"eng\"]}";
                            content.Add(new System.Net.Http.StringContent(options), "options");

                            var ocrEndpoint = endpoint.TrimEnd('/') + "/tesseract";
                            using (var ocrResponse = await httpClient.PostAsync(ocrEndpoint, content).ConfigureAwait(false))
                            {
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

        public Stream Get(GetThumbnailRequest request)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(request.ThumbnailId))
                {
                    _logger?.Warn("Thumbnail request with empty ThumbnailId");
                    return Stream.Null;
                }

                // Get thumbnail directory from plugin data folder
                var pluginDataPath = Plugin.Instance?.AppPaths?.PluginConfigurationsPath;
                if (string.IsNullOrEmpty(pluginDataPath))
                {
                    _logger?.Warn("Plugin data path not available");
                    return Stream.Null;
                }

                var thumbnailDir = Path.Combine(pluginDataPath, "EmbyCredits", "Thumbnails");
                var thumbnailPath = Path.Combine(thumbnailDir, request.ThumbnailId);

                if (!File.Exists(thumbnailPath))
                {
                    _logger?.Warn($"Thumbnail not found: {thumbnailPath}");
                    return Stream.Null;
                }

                // Return file stream
                return new FileStream(thumbnailPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            }
            catch (Exception ex)
            {
                _logger?.ErrorException($"Error getting thumbnail: {request.ThumbnailId}", ex);
                return Stream.Null;
            }
        }

        public object Post(DryRunSeriesDebugRequest request)
        {
            _logger?.Info("=== DryRunSeriesDebugRequest START ===");

            if (request?.SeasonNumber.HasValue == true && !string.IsNullOrEmpty(request.SeriesId))
            {
                try
                {
                    var series = ItemLookupHelper.ResolveSeries(_libraryManager, request.SeriesId, _logger);
                    if (series == null)
                    {
                        return new { Success = false, Message = "Series not found" };
                    }

                    var allEpisodes = ItemLookupHelper.GetSeriesEpisodes(_libraryManager, series.InternalId, _logger);
                    var seasonEpisodes = allEpisodes.Where(e => e.ParentIndexNumber == request.SeasonNumber.Value).ToList();

                    if (seasonEpisodes.Count == 0)
                    {
                        return new { Success = true, Message = $"No episodes found for Season {request.SeasonNumber.Value}", EpisodeCount = 0 };
                    }

                    CreditsDetectionService.QueueSeriesDryRunDebug(seasonEpisodes, request.SkipExistingMarkers);
                    return new { Success = true, Message = $"Debug dry run started for {seasonEpisodes.Count} episodes from Season {request.SeasonNumber.Value}", EpisodeCount = seasonEpisodes.Count };
                }
                catch (Exception ex)
                {
                    _logger?.ErrorException($"Error starting debug dry run for season {request.SeasonNumber.Value}", ex);
                    return new { Success = false, Message = ex.Message };
                }
            }

            var result = RequestProcessorHelper.ProcessDetectionRequest(
                _libraryManager,
                episodeId: request?.EpisodeId,
                seriesId: request?.SeriesId,
                libraryId: request?.LibraryId,
                processEpisode: episode => CreditsDetectionService.QueueEpisodeDryRunDebug(episode, request?.SkipExistingMarkers ?? false),
                processSeries: episodes => CreditsDetectionService.QueueSeriesDryRunDebug(episodes, request?.SkipExistingMarkers ?? false),
                _logger);

            return new { result.Success, result.Message, EpisodeCount = result.ItemCount };
        }

        public object Get(GetDebugLogRequest request)
        {
            try
            {
                _logger?.Info("Debug log requested");
                var debugLog = CreditsDetectionService.GetDebugLog();

                var bytes = System.Text.Encoding.UTF8.GetBytes(debugLog);
                var stream = new MemoryStream(bytes);
                stream.Position = 0;
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

        public async Task<object> Get(ExportSeriesCreditsRequest request)
        {
            try
            {
                if (string.IsNullOrEmpty(request.SeriesId))
                {
                    return new { Success = false, Message = "SeriesId is required" };
                }

                _logger?.Info($"Single series credits export requested for SeriesId: {request.SeriesId}");

                var series = ItemLookupHelper.ResolveSeries(_libraryManager, request.SeriesId, _logger);
                if (series == null)
                {
                    return new { Success = false, Message = "Series not found" };
                }

                var backupService = Plugin.CreditsBackupService;
                if (backupService == null)
                {
                    return new { Success = false, Message = "Backup service not initialized" };
                }

                var result = await backupService.ExportCreditsMarkers(
                    null,
                    new List<string> { request.SeriesId }
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
                _logger?.ErrorException("Error exporting series credits", ex);
                return new { Success = false, Message = ex.Message };
            }
        }

        public async Task<object> Post(ImportSeriesCreditsRequest request)
        {
            try
            {
                if (string.IsNullOrEmpty(request.SeriesId))
                {
                    return new { Success = false, Message = "SeriesId is required" };
                }

                if (string.IsNullOrEmpty(request.JsonData))
                {
                    return new { Success = false, Message = "No backup data provided" };
                }

                _logger?.Info($"Single series credits import requested for SeriesId: {request.SeriesId}");

                var series = ItemLookupHelper.ResolveSeries(_libraryManager, request.SeriesId, _logger);
                if (series == null)
                {
                    return new { Success = false, Message = "Series not found" };
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
                _logger?.ErrorException("Error importing series credits", ex);
                return new { Success = false, Message = ex.Message };
            }
        }

        public object Post(UpdateCreditsMarkerRequest request)
        {
            try
            {
                if (string.IsNullOrEmpty(request.EpisodeId))
                {
                    return new { Success = false, Message = "EpisodeId is required" };
                }

                if (!request.IsRelativeFromEnd && request.CreditsStartSeconds < 0)
                {
                    return new { Success = false, Message = "Credits start time must be positive" };
                }

                Guid episodeGuid;
                if (!Guid.TryParse(request.EpisodeId, out episodeGuid))
                {
                    return new { Success = false, Message = "Invalid EpisodeId format" };
                }

                var episode = _libraryManager.GetItemById(episodeGuid) as Episode;
                if (episode == null)
                {
                    return new { Success = false, Message = "Episode not found" };
                }

                if (!episode.RunTimeTicks.HasValue || episode.RunTimeTicks.Value <= 0)
                {
                    return new { Success = false, Message = "Episode has no valid runtime information" };
                }

                var durationSeconds = episode.RunTimeTicks.Value / (double)TimeSpan.TicksPerSecond;
                double actualCreditsStartSeconds;
                
                if (request.IsRelativeFromEnd)
                {
                    actualCreditsStartSeconds = durationSeconds - Math.Abs(request.CreditsStartSeconds);
                    
                    if (actualCreditsStartSeconds < 0)
                    {
                        return new { 
                            Success = false, 
                            Message = $"Offset from end ({Math.Abs(request.CreditsStartSeconds):F1}s) exceeds video duration ({durationSeconds:F1}s)" 
                        };
                    }
                }
                else
                {
                    actualCreditsStartSeconds = request.CreditsStartSeconds;
                    
                    if (actualCreditsStartSeconds >= durationSeconds)
                    {
                        return new { 
                            Success = false, 
                            Message = $"Timestamp ({request.CreditsStartSeconds:F1}s) exceeds video duration ({durationSeconds:F1}s)" 
                        };
                    }
                }

                var chapterMarkerService = Plugin.ChapterMarkerService;
                if (chapterMarkerService == null)
                {
                    return new { Success = false, Message = "Chapter marker service not available" };
                }

                chapterMarkerService.SaveCreditsMarker(episode, actualCreditsStartSeconds);

                var timeDescription = request.IsRelativeFromEnd 
                    ? $"-{FormatTime(Math.Abs(request.CreditsStartSeconds))} from end (absolute: {FormatTime(actualCreditsStartSeconds)})"
                    : $"{FormatTime(actualCreditsStartSeconds)}";

                _logger?.Info($"Updated credits marker for episode '{episode.Name}' to {timeDescription}");

                return new { 
                    Success = true, 
                    Message = $"Credits marker updated successfully for {episode.Name}",
                    EpisodeName = episode.Name,
                    CreditsStartSeconds = actualCreditsStartSeconds,
                    IsRelativeFromEnd = request.IsRelativeFromEnd,
                    RelativeOffset = request.IsRelativeFromEnd ? request.CreditsStartSeconds : 0
                };
            }
            catch (Exception ex)
            {
                _logger?.ErrorException("Error updating credits marker", ex);
                return new { Success = false, Message = ex.Message };
            }
        }

        public object Post(ApplyToSeasonRequest request)
        {
            try
            {
                if (string.IsNullOrEmpty(request.EpisodeId))
                {
                    return new { Success = false, Message = "EpisodeId is required" };
                }

                if (string.IsNullOrEmpty(request.SeriesId))
                {
                    return new { Success = false, Message = "SeriesId is required" };
                }

                Guid episodeGuid;
                if (!Guid.TryParse(request.EpisodeId, out episodeGuid))
                {
                    return new { Success = false, Message = "Invalid EpisodeId format" };
                }

                var sourceEpisode = _libraryManager.GetItemById(episodeGuid) as Episode;
                if (sourceEpisode == null)
                {
                    return new { Success = false, Message = "Source episode not found" };
                }

                var chapterMarkerService = Plugin.ChapterMarkerService;
                if (chapterMarkerService == null)
                {
                    return new { Success = false, Message = "Chapter marker service not available" };
                }

                var chapters = chapterMarkerService.GetChapters(sourceEpisode);

                var creditsMarker = chapters.FirstOrDefault(c =>
                {
                    var markerType = GetMarkerTypeFromChapter(c);
                    return markerType == "CreditsStart" || markerType == "Credits" ||
                           (c.Name != null && c.Name.ToLowerInvariant().Contains("credit"));
                });

                if (creditsMarker == null)
                {
                    return new { Success = false, Message = "Source episode does not have a credits marker" };
                }

                var creditsStartSeconds = creditsMarker.StartPositionTicks / (double)TimeSpan.TicksPerSecond;
                var sourceDurationSeconds = sourceEpisode.RunTimeTicks.HasValue
                    ? sourceEpisode.RunTimeTicks.Value / (double)TimeSpan.TicksPerSecond
                    : 0;

                if (sourceDurationSeconds <= 0)
                {
                    return new { Success = false, Message = "Source episode has no valid runtime information" };
                }

                var timeFromEnd = sourceDurationSeconds - creditsStartSeconds;

                var series = ItemLookupHelper.ResolveSeries(_libraryManager, request.SeriesId, _logger);
                if (series == null)
                {
                    return new { Success = false, Message = "Series not found" };
                }

                var allEpisodes = ItemLookupHelper.GetSeriesEpisodes(_libraryManager, series.InternalId, _logger);
                var seasonEpisodes = allEpisodes
                    .Where(e => e.ParentIndexNumber == request.SeasonNumber && e.Id != episodeGuid)
                    .ToList();

                if (seasonEpisodes.Count == 0)
                {
                    return new { Success = false, Message = $"No other episodes found in Season {request.SeasonNumber}" };
                }

                var episodeMarkers = chapterMarkerService.GetSeriesMarkers(seasonEpisodes);
                var episodesWithMarkers = new HashSet<string>();

                foreach (var marker in episodeMarkers)
                {
                    var markerType = marker.GetType();
                    var hasCreditsMarkerProp = markerType.GetProperty("HasCreditsMarker");
                    var episodeIdProp = markerType.GetProperty("EpisodeId");

                    if (hasCreditsMarkerProp != null && episodeIdProp != null)
                    {
                        var hasMarkerValue = hasCreditsMarkerProp.GetValue(marker);
                        if (hasMarkerValue is bool boolVal && boolVal)
                        {
                            var episodeId = episodeIdProp.GetValue(marker)?.ToString();
                            if (episodeId != null)
                            {
                                episodesWithMarkers.Add(episodeId);
                            }
                        }
                    }
                }

                var episodesWithoutMarkers = seasonEpisodes
                    .Where(ep => !episodesWithMarkers.Contains(ep.Id.ToString()))
                    .ToList();

                if (episodesWithoutMarkers.Count == 0)
                {
                    return new
                    {
                        Success = true,
                        Message = $"All other episodes in Season {request.SeasonNumber} already have credits markers",
                        EpisodeCount = 0,
                        TotalEpisodes = seasonEpisodes.Count
                    };
                }

                var successCount = 0;
                var failedEpisodes = new List<string>();

                foreach (var episode in episodesWithoutMarkers)
                {
                    try
                    {
                        if (!episode.RunTimeTicks.HasValue || episode.RunTimeTicks.Value <= 0)
                        {
                            failedEpisodes.Add($"{episode.Name} (no runtime)");
                            _logger?.Warn($"Skipping {episode.Name} - no valid runtime information");
                            continue;
                        }

                        var episodeDurationSeconds = episode.RunTimeTicks.Value / (double)TimeSpan.TicksPerSecond;
                        var episodeCreditsStartSeconds = episodeDurationSeconds - timeFromEnd;

                        if (episodeCreditsStartSeconds < 0)
                        {
                            failedEpisodes.Add($"{episode.Name} (offset exceeds duration)");
                            _logger?.Warn($"Skipping {episode.Name} - credits offset from end exceeds video duration");
                            continue;
                        }

                        if (episodeCreditsStartSeconds >= episodeDurationSeconds)
                        {
                            failedEpisodes.Add($"{episode.Name} (timestamp exceeds duration)");
                            _logger?.Warn($"Skipping {episode.Name} - timestamp exceeds video duration");
                            continue;
                        }

                        chapterMarkerService.SaveCreditsMarker(episode, episodeCreditsStartSeconds);
                        successCount++;
                        _logger?.Info($"Applied credits marker at {FormatTime(episodeCreditsStartSeconds)} (-{FormatTime(timeFromEnd)} from end) to {episode.Name}");
                    }
                    catch (Exception ex)
                    {
                        failedEpisodes.Add($"{episode.Name} ({ex.Message})");
                        _logger?.ErrorException($"Failed to apply marker to {episode.Name}", ex);
                    }
                }

                var timeDescription = $"{FormatTime(creditsStartSeconds)} (-{FormatTime(timeFromEnd)} from end)";

                if (failedEpisodes.Count == 0)
                {
                    return new
                    {
                        Success = true,
                        Message = $"Successfully copied credits marker from '{sourceEpisode.Name}' at {timeDescription} to {successCount} episode(s) in Season {request.SeasonNumber}",
                        EpisodeCount = successCount,
                        TotalEpisodes = seasonEpisodes.Count,
                        SourceEpisode = sourceEpisode.Name,
                        CreditsTime = timeDescription
                    };
                }
                else
                {
                    return new
                    {
                        Success = true,
                        Message = $"Copied marker to {successCount} episode(s), {failedEpisodes.Count} failed. Time: {timeDescription}",
                        EpisodeCount = successCount,
                        FailedCount = failedEpisodes.Count,
                        FailedEpisodes = failedEpisodes,
                        SourceEpisode = sourceEpisode.Name,
                        CreditsTime = timeDescription
                    };
                }
            }
            catch (Exception ex)
            {
                _logger?.ErrorException($"Error applying credits marker to season", ex);
                return new { Success = false, Message = ex.Message };
            }
        }

        private string? GetMarkerTypeFromChapter(ChapterInfo chapter)
        {
            try
            {
                if (chapter == null)
                    return null;

                var chapterType = chapter.GetType();
                if (chapterType == null)
                    return null;

                var markerTypeProp = chapterType.GetProperty("MarkerType");
                if (markerTypeProp != null && markerTypeProp.CanRead)
                {
                    var value = markerTypeProp.GetValue(chapter);
                    return value?.ToString();
                }
            }
            catch (Exception ex)
            {
                _logger?.Debug($"Error reading MarkerType property: {ex.Message}");
            }
            return null;
        }
    }
}
