using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EmbyCredits.Api;
using EmbyCredits.Services;
using EmbyCredits.Services.Utilities;

namespace EmbyCredits.Services
{
    public class CreditsDetectionApiService : IService
    {
        private const int MaxImportBytes = 50 * 1024 * 1024;
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
                if (Plugin.Instance?.Configuration?.EnableDetailedLogging == true)
                {
                    _logger.Info("Manual credits detection triggered");
                }

                var allEpisodes = _libraryManager.GetItemList(new InternalItemsQuery
                {
                    IncludeItemTypes = new[] { "Episode" },
                    IsVirtualItem = false,
                    HasPath = true,
                    Limit = request.Limit > 0 ? request.Limit : null
                }).OfType<Episode>().ToList();

                var episodes = allEpisodes.Where(e => e.ParentIndexNumber != null && e.ParentIndexNumber != 0).ToList();
                var specialCount = allEpisodes.Count - episodes.Count;

                CreditsDetectionService.QueueSeriesManual(episodes, request.SkipExistingMarkers, request.IgnoreFailureMarkers);

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
            if (Plugin.Instance?.Configuration?.EnableDetailedLogging == true)
            {
                _logger?.Info("=== ProcessSeriesRequest received ===");
            }

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
            if (Plugin.Instance?.Configuration?.EnableDetailedLogging == true)
            {
                _logger?.Info($"=== ProcessSeasonRequest received for Season {request.SeasonNumber} ===");
            }

            try
            {
                if (string.IsNullOrEmpty(request.SeriesId))
                {
                    return new { Success = false, Message = "SeriesId is required" };
                }

                if (request.SeasonNumber < 0)
                {
                    return new { Success = false, Message = "Invalid season number. Must be 0 or greater." };
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
            if (Plugin.Instance?.Configuration?.EnableDetailedLogging == true)
            {
                _logger?.Info($"=== ProcessSeasonMissingMarkersRequest received for Season {request.SeasonNumber} ===");
            }

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
                    if (marker.HasCreditsMarker && marker.EpisodeId != null)
                    {
                        episodesWithMarkers.Add(marker.EpisodeId);
                        if (Plugin.Instance?.Configuration?.EnableDetailedLogging == true)
                        {
                            _logger?.Info($"Episode {marker.EpisodeId} already has credits marker");
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
            if (Plugin.Instance?.Configuration?.EnableDetailedLogging == true)
            {
                _logger?.Info($"=== BatchUpdateSeasonMissingMarkersRequest received for Season {request.SeasonNumber} ===");
            }

            try
            {
                if (string.IsNullOrEmpty(request.SeriesId))
                {
                    return new { Success = false, Message = "SeriesId is required" };
                }

                if (request.SeasonNumber < 0)
                {
                    return new { Success = false, Message = "Invalid season number. Must be 0 or greater." };
                }

                if (Math.Abs(request.CreditsStartSeconds) > 86400)
                {
                    return new { Success = false, Message = "Invalid timestamp. Must be within 24 hours." };
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
                    if (marker.HasCreditsMarker && marker.EpisodeId != null)
                    {
                        episodesWithMarkers.Add(marker.EpisodeId);
                    }
                }

                                var episodesWithoutMarkers = seasonEpisodes
                    .Where(ep => !episodesWithMarkers.Contains(ep.Id.ToString()))
                    .ToList();
                
                if (Plugin.Instance?.Configuration?.EnableDetailedLogging == true)
                {
                    _logger?.Info($"Found {episodesWithoutMarkers.Count} episodes without markers (out of {seasonEpisodes.Count} total)");
                }

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
                        TryAutoBackupEpisode(episode, (long)(actualCreditsStartSeconds * TimeSpan.TicksPerSecond));
                        successCount++;
                        if (Plugin.Instance?.Configuration?.EnableDetailedLogging == true)
                        {
                            _logger?.Info($"Set credits marker at {FormatTime(actualCreditsStartSeconds)} for {episode.Name}");
                        }
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

        private string FormatTime(double seconds) => ItemLookupHelper.FormatTime(seconds);

        private object ToStaticResult(MemoryStream stream)
        {
            try
            {
                var bytes = stream.ToArray();
                stream.Dispose();
                return new MemoryStream(bytes, false);
            }
            catch
            {
                stream?.Dispose();
                throw;
            }
        }

        public object Post(ProcessLibraryRequest request)
        {
            if (Plugin.Instance?.Configuration?.EnableDetailedLogging == true)
            {
                _logger?.Info("=== ProcessLibraryRequest received ===");
            }

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
                    SkippedItems = progress.SkippedItems,
                    CurrentItem = progress.CurrentItem,
                    CurrentItemProgress = progress.CurrentItemProgress,
                    PercentComplete = progress.PercentComplete,
                    EstimatedTimeRemainingSeconds = progress.EstimatedTimeRemaining?.TotalSeconds,
                    StartTime = progress.StartTime,
                    EndTime = progress.EndTime,
                    FailureReasons = progress.FailureReasons,
                    SkipReasons = progress.SkipReasons,
                    SuccessDetails = progress.SuccessDetails,
                    ConfidenceScores = progress.ConfidenceScores,
                    ThumbnailPaths = progress.ThumbnailPaths,
                    EpisodeIds = progress.EpisodeIds,
                    AppliedRules = progress.AppliedRules,
                    ActiveFfmpegProcesses = FFmpegHelper.GetActiveProcesses()
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
                    var marker = episodeMarkers.FirstOrDefault(m => m.EpisodeId == episode.Id.ToString());

                    object markerData;
                    if (marker != null && marker.HasCreditsMarker)
                    {
                        var firstMarker = marker.Markers.FirstOrDefault();
                        markerData = new
                        {
                            HasMarker = true,
                            StartTime = firstMarker?.StartTime
                        };
                    }
                    else
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
            if (Plugin.Instance?.Configuration?.EnableDetailedLogging == true)
            {
                _logger?.Info("=== DryRunSeriesRequest START ===");
            }

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

                if (!IsValidOcrEndpointUrl(request.OcrEndpoint))
                {
                    return new { Success = false, Message = "Invalid OCR endpoint URL. Only localhost and local network addresses are allowed." };
                }

                var endpoint = request.OcrEndpoint.TrimEnd('/');

                using (var httpClient = new System.Net.Http.HttpClient())
                {
                    httpClient.Timeout = TimeSpan.FromSeconds(15);

                    if (request.OcrEngine != "PaddleOCR")
                    {
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
                        using (var imageContent = new System.Net.Http.ByteArrayContent(imageBytes))
                        {
                            imageContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");
                            
                            string ocrEndpoint;
                            if (request.OcrEngine == "PaddleOCR")
                            {
                                content.Add(imageContent, "file", "logo.jpg");
                                ocrEndpoint = endpoint.TrimEnd('/') + "/ocr/file";
                            }
                            else
                            {
                                content.Add(imageContent, "file", "logo.jpg");
                                var optionsContent = new System.Net.Http.StringContent("{\"languages\":[\"eng\"]}");
                                content.Add(optionsContent, "options");
                                ocrEndpoint = endpoint.TrimEnd('/') + "/tesseract";
                            }

                            using (var ocrResponse = await httpClient.PostAsync(ocrEndpoint, content).ConfigureAwait(false))
                            {
                                if (!ocrResponse.IsSuccessStatusCode)
                                {
                                    var errorContent = await ocrResponse.Content.ReadAsStringAsync().ConfigureAwait(false);
                                    return new { Success = false, Message = $"OCR processing failed with status: {ocrResponse.StatusCode}. Details: {errorContent.Substring(0, Math.Min(200, errorContent.Length))}" };
                                }

                                var ocrResult = await ocrResponse.Content.ReadAsStringAsync().ConfigureAwait(false);

                                if (request.OcrEngine == "PaddleOCR")
                                {
                                    if (ocrResult.Contains("\"data\"") && ocrResult.Contains("\"stdout\"") && ocrResult.Length > 20)
                                    {
                                        return new { Success = true, Message = "✓ Connection successful! PaddleOCR server is responding correctly." };
                                    }
                                    else
                                    {
                                        return new { Success = false, Message = $"PaddleOCR server responded but returned unexpected format: {ocrResult.Substring(0, Math.Min(150, ocrResult.Length))}..." };
                                    }
                                }
                                else
                                {
                                    if (ocrResult.Contains("\"data\"") && ocrResult.Contains("\"stdout\"") && ocrResult.Length > 20)
                                    {
                                        return new { Success = true, Message = "✓ Connection successful! Tesseract OCR server is responding correctly." };
                                    }
                                    else
                                    {
                                        return new { Success = false, Message = $"Tesseract server responded but returned unexpected format: {ocrResult.Substring(0, Math.Min(150, ocrResult.Length))}..." };
                                    }
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

                var assembly = typeof(Plugin).Assembly;
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
            if (Plugin.Instance?.Configuration?.EnableDetailedLogging == true)
            {
                _logger?.Info("=== DryRunSeriesDebugRequest START ===");
            }

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
                if (Plugin.Instance?.Configuration?.EnableDetailedLogging == true)
                {
                    _logger?.Info("Debug log requested");
                }
                var debugLog = CreditsDetectionService.GetDebugLog();

                var bytes = System.Text.Encoding.UTF8.GetBytes(debugLog);
                var stream = new MemoryStream(bytes);
                stream.Position = 0;
                
                CreditsDetectionService.CleanupDebugLog();
                
                return ToStaticResult(stream);
            }
            catch (Exception ex)
            {
                _logger?.ErrorException("Error getting debug log", ex);
                var errorMessage = $"Error retrieving debug log: {ex.Message}\n\nStack Trace:\n{ex.StackTrace}";
                var errorBytes = System.Text.Encoding.UTF8.GetBytes(errorMessage);
                var errorStream = new MemoryStream(errorBytes);
                errorStream.Position = 0;
                return ToStaticResult(errorStream);
            }
        }

        public async Task<object> Post(ExportCreditsBackupRequest request)
        {
            try
            {
                if (Plugin.Instance?.Configuration?.EnableDetailedLogging == true)
                {
                    _logger?.Info("Credits backup export requested");
                }

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
                if (Plugin.Instance?.Configuration?.EnableDetailedLogging == true)
                {
                    _logger?.Info("Credits backup import requested");
                }

                if (string.IsNullOrEmpty(request.JsonData))
                {
                    return new { Success = false, Message = "No backup data provided" };
                }

                if (request.JsonData.Length > MaxImportBytes)
                {
                    return new { Success = false, Message = $"Import data exceeds maximum allowed size of {MaxImportBytes / (1024 * 1024)} MB" };
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

        public async Task<object> Post(BulkExportToFolderRequest request)
        {
            try
            {
                var config = Plugin.Instance?.Configuration;
                if (config == null)
                    return new { Success = false, Message = "Plugin configuration not available" };

                if (string.IsNullOrWhiteSpace(config.BackupFolderPath))
                    return new { Success = false, Message = "Backup folder path is not configured. Please set it in Settings before using bulk export." };

                var backupService = Plugin.CreditsBackupService;
                if (backupService == null)
                    return new { Success = false, Message = "Backup service not initialized" };

                var maxBackups = config.MaxScheduledBackups > 0 ? config.MaxScheduledBackups : 10;

                var result = await backupService.ExportCreditsMarkers(
                    null,
                    request.SeriesIds,
                    CancellationToken.None,
                    config.BackupFolderPath,
                    maxBackups);

                return new
                {
                    Success = result.Success,
                    Message = result.Message,
                    TotalEpisodes = result.TotalEpisodes,
                    EpisodesWithCredits = result.EpisodesWithCredits,
                    FolderPath = config.BackupFolderPath
                };
            }
            catch (Exception ex)
            {
                _logger?.ErrorException("Error during bulk export to folder", ex);
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

                if (Plugin.Instance?.Configuration?.EnableDetailedLogging == true)
                {
                    _logger?.Info($"Single series credits export requested for SeriesId: {request.SeriesId}");
                }

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
                return ToStaticResult(stream);
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

                if (request.JsonData.Length > MaxImportBytes)
                {
                    return new { Success = false, Message = $"Import data exceeds maximum allowed size of {MaxImportBytes / (1024 * 1024)} MB" };
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

                if (Math.Abs(request.CreditsStartSeconds) > 86400)
                {
                    return new { Success = false, Message = "Invalid timestamp. Must be within 24 hours." };
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
                TryAutoBackupEpisode(episode, (long)(actualCreditsStartSeconds * TimeSpan.TicksPerSecond));

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
                    if (marker.HasCreditsMarker && marker.EpisodeId != null)
                    {
                        episodesWithMarkers.Add(marker.EpisodeId);
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
                        TryAutoBackupEpisode(episode, (long)(episodeCreditsStartSeconds * TimeSpan.TicksPerSecond));
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

        private void TryAutoBackupEpisode(Episode episode, long creditsStartTicks)
        {
            if (Plugin.CreditsBackupService == null) return;
            var config = Plugin.Instance?.Configuration;
            if (config == null || !config.EnableAutoBackupAfterDetection || string.IsNullOrWhiteSpace(config.BackupFolderPath))
                return;
            var svc = Plugin.CreditsBackupService;
            var folder = config.BackupFolderPath;
            _ = Task.Run(async () =>
            {
                try
                {
                    await svc.UpsertEpisodeInSeriesBackup(episode, creditsStartTicks, folder).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger?.ErrorException("Auto-backup after marker save failed", ex);
                }
            });
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

        public object Post(AddTimestampFromDryRunRequest request)
        {
            try
            {
                if (string.IsNullOrEmpty(request.EpisodeId))
                {
                    return new { Success = false, Message = "EpisodeId is required" };
                }

                if (request.TimestampSeconds <= 0 || request.TimestampSeconds > 86400)
                {
                    return new { Success = false, Message = "Timestamp must be between 0 and 86400 seconds (24 hours)" };
                }

                var episode = _libraryManager.GetItemById(request.EpisodeId) as Episode;
                if (episode == null)
                {
                    return new { Success = false, Message = "Episode not found" };
                }

                var chapterMarkerService = Plugin.ChapterMarkerService;
                if (chapterMarkerService == null)
                {
                    return new { Success = false, Message = "Chapter marker service not initialized" };
                }

                var episodeInfo = $"{episode.Series?.Name} S{episode.ParentIndexNumber:00}E{episode.IndexNumber:00} - {episode.Name}";
                _logger?.Info($"Manually adding timestamp from dry run: {episodeInfo} at {FormatTime(request.TimestampSeconds)}");

                chapterMarkerService.SaveCreditsMarker(episode, request.TimestampSeconds);
                TryAutoBackupEpisode(episode, (long)(request.TimestampSeconds * TimeSpan.TicksPerSecond));

                return new { 
                    Success = true, 
                    Message = $"Credits marker added at {FormatTime(request.TimestampSeconds)} for {episodeInfo}" 
                };
            }
            catch (Exception ex)
            {
                _logger?.ErrorException("Error adding timestamp from dry run", ex);
                return new { Success = false, Message = ex.Message };
            }
        }

        public object Get(GetMemoryUsageRequest request)
        {
            try
            {
                var currentProcess = System.Diagnostics.Process.GetCurrentProcess();
                var workingSet = currentProcess.WorkingSet64;
                var privateMemory = currentProcess.PrivateMemorySize64;
                var gcTotalMemory = GC.GetTotalMemory(false);

                return new
                {
                    Success = true,
                    WorkingSetBytes = workingSet,
                    WorkingSetMB = Math.Round(workingSet / (1024.0 * 1024.0), 2),
                    PrivateMemoryBytes = privateMemory,
                    PrivateMemoryMB = Math.Round(privateMemory / (1024.0 * 1024.0), 2),
                    GCTotalMemoryBytes = gcTotalMemory,
                    GCTotalMemoryMB = Math.Round(gcTotalMemory / (1024.0 * 1024.0), 2),
                    Timestamp = DateTime.UtcNow
                };
            }
            catch (Exception ex)
            {
                _logger?.ErrorException("Error getting memory usage", ex);
                return new { Success = false, Message = ex.Message };
            }
        }

        public object Post(StartDetectionRequest request)
        {
            try
            {
                var config = Plugin.Instance?.Configuration;
                if (config == null)
                {
                    return new { Success = false, Message = "Plugin configuration not available" };
                }

                PluginConfiguration? effectiveConfig = null;
                if (request.SettingsOverride != null)
                {
                    effectiveConfig = CloneConfigurationWithOverrides(config, request.SettingsOverride);
                }
                else
                {
                    effectiveConfig = config;
                }

                if (request.DryRun)
                {
                    var dryRunRequest = new DryRunSeriesRequest
                    {
                        SeriesId = request.SeriesId ?? string.Empty,
                        EpisodeId = request.EpisodeId ?? string.Empty,
                        LibraryId = request.LibraryId ?? string.Empty,
                        SeasonNumber = request.SeasonNumber,
                        SkipExistingMarkers = request.SkipExistingMarkers
                    };

                    if (request.EnableDebugLogging)
                    {
                        var debugRequest = new DryRunSeriesDebugRequest
                        {
                            SeriesId = dryRunRequest.SeriesId,
                            EpisodeId = dryRunRequest.EpisodeId,
                            LibraryId = dryRunRequest.LibraryId,
                            SeasonNumber = dryRunRequest.SeasonNumber,
                            SkipExistingMarkers = dryRunRequest.SkipExistingMarkers
                        };
                        return Post(debugRequest);
                    }

                    return Post(dryRunRequest);
                }

                if (!string.IsNullOrEmpty(request.EpisodeId))
                {
                    var episodeRequest = new ProcessEpisodeRequest
                    {
                        ItemId = request.EpisodeId,
                        SkipExistingMarkers = request.SkipExistingMarkers
                    };
                    return Post(episodeRequest);
                }
                else if (!string.IsNullOrEmpty(request.SeriesId) && request.SeasonNumber.HasValue)
                {
                    var seasonRequest = new ProcessSeasonRequest
                    {
                        SeriesId = request.SeriesId,
                        SeasonNumber = request.SeasonNumber.Value,
                        SkipExistingMarkers = request.SkipExistingMarkers
                    };
                    return Post(seasonRequest);
                }
                else if (!string.IsNullOrEmpty(request.SeriesId))
                {
                    var seriesRequest = new ProcessSeriesRequest
                    {
                        SeriesId = request.SeriesId,
                        SkipExistingMarkers = request.SkipExistingMarkers
                    };
                    return Post(seriesRequest);
                }
                else if (!string.IsNullOrEmpty(request.LibraryId))
                {
                    var libraryRequest = new ProcessLibraryRequest
                    {
                        LibraryId = request.LibraryId,
                        SkipExistingMarkers = request.SkipExistingMarkers
                    };
                    return Post(libraryRequest);
                }

                return new { Success = false, Message = "Please specify EpisodeId, SeriesId, or LibraryId" };
            }
            catch (Exception ex)
            {
                _logger?.ErrorException("Error starting detection", ex);
                return new { Success = false, Message = ex.Message };
            }
        }

        private PluginConfiguration CloneConfigurationWithOverrides(PluginConfiguration config, DetectionSettingsOverride overrides)
        {
            var clone = new PluginConfiguration();
            var configType = typeof(PluginConfiguration);
            
            foreach (var prop in configType.GetProperties())
            {
                if (prop.CanRead && prop.CanWrite)
                {
                    var value = prop.GetValue(config);
                    prop.SetValue(clone, value);
                }
            }

            if (overrides.DetectionMode != null && Enum.TryParse<DetectionMode>(overrides.DetectionMode, true, out var detectionMode))
                clone.DetectionMode = detectionMode;
            if (overrides.OcrEngine != null && Enum.TryParse<OcrEngine>(overrides.OcrEngine, true, out var ocrEngine))
                clone.OcrEngine = ocrEngine;
            if (overrides.OcrEndpoint != null) clone.OcrEndpoint = overrides.OcrEndpoint;
            if (overrides.OcrLanguages != null) clone.OcrLanguages = overrides.OcrLanguages;
            if (overrides.OcrDetectionKeywords != null) clone.OcrDetectionKeywords = overrides.OcrDetectionKeywords;
            if (overrides.OcrSearchStartValue.HasValue) clone.OcrSearchStartValue = overrides.OcrSearchStartValue.Value;
            if (overrides.OcrSearchStartUnit != null) clone.OcrSearchStartUnit = overrides.OcrSearchStartUnit;
            if (overrides.OcrMinutesFromEnd.HasValue) clone.OcrMinutesFromEnd = overrides.OcrMinutesFromEnd.Value;
            if (overrides.OcrFrameRate.HasValue) clone.OcrFrameRate = overrides.OcrFrameRate.Value;
            if (overrides.OcrMinimumMatches.HasValue) clone.OcrMinimumMatches = overrides.OcrMinimumMatches.Value;
            if (overrides.OcrMaxFramesToProcess.HasValue) clone.OcrMaxFramesToProcess = overrides.OcrMaxFramesToProcess.Value;
            if (overrides.OcrMaxAnalysisDuration.HasValue) clone.OcrMaxAnalysisDuration = overrides.OcrMaxAnalysisDuration.Value;
            if (overrides.OcrStopSecondsFromEnd.HasValue) clone.OcrStopSecondsFromEnd = overrides.OcrStopSecondsFromEnd.Value;
            if (overrides.OcrPageSegmentationMode.HasValue) clone.OcrPageSegmentationMode = overrides.OcrPageSegmentationMode.Value;
            if (overrides.OcrEngineMode.HasValue) clone.OcrEngineMode = overrides.OcrEngineMode.Value;
            if (overrides.OcrJpegQuality.HasValue) clone.OcrJpegQuality = overrides.OcrJpegQuality.Value;
            if (overrides.OcrMaxResolutionHeight.HasValue) clone.OcrMaxResolutionHeight = overrides.OcrMaxResolutionHeight.Value;
            if (overrides.OcrDelayBetweenFramesMs.HasValue) clone.OcrDelayBetweenFramesMs = overrides.OcrDelayBetweenFramesMs.Value;
            if (overrides.OcrEnableParallelProcessing.HasValue) clone.OcrEnableParallelProcessing = overrides.OcrEnableParallelProcessing.Value;
            if (overrides.OcrParallelBatchSize.HasValue) clone.OcrParallelBatchSize = overrides.OcrParallelBatchSize.Value;
            if (overrides.OcrDelayBetweenBatchesMs.HasValue) clone.OcrDelayBetweenBatchesMs = overrides.OcrDelayBetweenBatchesMs.Value;
            if (overrides.OcrEnableSmartFrameSkipping.HasValue) clone.OcrEnableSmartFrameSkipping = overrides.OcrEnableSmartFrameSkipping.Value;
            if (overrides.OcrConsecutiveMatchesForEarlyStop.HasValue) clone.OcrConsecutiveMatchesForEarlyStop = overrides.OcrConsecutiveMatchesForEarlyStop.Value;
            if (overrides.OcrMinimumConfidence.HasValue) clone.OcrMinimumConfidence = overrides.OcrMinimumConfidence.Value;
            if (overrides.OcrEnableEpisodeComparison.HasValue) clone.OcrEnableEpisodeComparison = overrides.OcrEnableEpisodeComparison.Value;
            if (overrides.OcrEpisodeComparisonTolerance.HasValue) clone.OcrEpisodeComparisonTolerance = overrides.OcrEpisodeComparisonTolerance.Value;
            if (overrides.OcrEpisodeComparisonMinimumEpisodes.HasValue) clone.OcrEpisodeComparisonMinimumEpisodes = overrides.OcrEpisodeComparisonMinimumEpisodes.Value;
            if (overrides.OcrEnableCharacterDensityDetection.HasValue) clone.OcrEnableCharacterDensityDetection = overrides.OcrEnableCharacterDensityDetection.Value;
            if (overrides.OcrCharacterDensityThreshold.HasValue) clone.OcrCharacterDensityThreshold = overrides.OcrCharacterDensityThreshold.Value;
            if (overrides.OcrCharacterDensityConsecutiveFrames.HasValue) clone.OcrCharacterDensityConsecutiveFrames = overrides.OcrCharacterDensityConsecutiveFrames.Value;
            if (overrides.OcrCharacterDensityPrimaryMethod.HasValue) clone.OcrCharacterDensityPrimaryMethod = overrides.OcrCharacterDensityPrimaryMethod.Value;
            if (overrides.OcrDensityRequireKeyword.HasValue) clone.OcrDensityRequireKeyword = overrides.OcrDensityRequireKeyword.Value;
            if (overrides.OcrDensityKeywordWindowSeconds.HasValue) clone.OcrDensityKeywordWindowSeconds = overrides.OcrDensityKeywordWindowSeconds.Value;
            if (overrides.OcrDensityRequireTemporalConsistency.HasValue) clone.OcrDensityRequireTemporalConsistency = overrides.OcrDensityRequireTemporalConsistency.Value;
            if (overrides.OcrDensityMinimumDurationSeconds.HasValue) clone.OcrDensityMinimumDurationSeconds = overrides.OcrDensityMinimumDurationSeconds.Value;
            if (overrides.OcrDensityRequireStyleConsistency.HasValue) clone.OcrDensityRequireStyleConsistency = overrides.OcrDensityRequireStyleConsistency.Value;
            if (overrides.OcrDensityStyleConsistencyThreshold.HasValue) clone.OcrDensityStyleConsistencyThreshold = overrides.OcrDensityStyleConsistencyThreshold.Value;
            if (overrides.OcrEnableHardwareAcceleration.HasValue) clone.OcrEnableHardwareAcceleration = overrides.OcrEnableHardwareAcceleration.Value;
            if (overrides.OcrHardwareAccelerationType != null) clone.OcrHardwareAccelerationType = overrides.OcrHardwareAccelerationType;
            if (overrides.OcrHardwareDevice != null) clone.OcrHardwareDevice = overrides.OcrHardwareDevice;
            if (overrides.OcrUseHardwareOutputFormat.HasValue) clone.OcrUseHardwareOutputFormat = overrides.OcrUseHardwareOutputFormat.Value;
            if (overrides.OcrUseHardwareFilters.HasValue) clone.OcrUseHardwareFilters = overrides.OcrUseHardwareFilters.Value;
            if (overrides.OcrUseDirectMemoryPipeline.HasValue) clone.OcrUseDirectMemoryPipeline = overrides.OcrUseDirectMemoryPipeline.Value;
            if (overrides.EnableAnimeDetection.HasValue) clone.EnableAnimeDetection = overrides.EnableAnimeDetection.Value;
            if (overrides.AnimeDetectionMethod != null && Enum.TryParse<AnimeDetectionMethod>(overrides.AnimeDetectionMethod, true, out var animeMethod))
                clone.AnimeDetectionMethod = animeMethod;
            if (overrides.BlackFrameMinimumPercentage.HasValue) clone.BlackFrameMinimumPercentage = overrides.BlackFrameMinimumPercentage.Value;
            if (overrides.BlackFrameThreshold.HasValue) clone.BlackFrameThreshold = overrides.BlackFrameThreshold.Value;
            if (overrides.ChromaprintFingerprintSimilarityThreshold.HasValue) clone.ChromaprintFingerprintSimilarityThreshold = overrides.ChromaprintFingerprintSimilarityThreshold.Value;
            if (overrides.ChromaprintEnableEpisodeComparison.HasValue) clone.ChromaprintEnableEpisodeComparison = overrides.ChromaprintEnableEpisodeComparison.Value;
            if (overrides.ChromaprintEpisodeComparisonTolerance.HasValue) clone.ChromaprintEpisodeComparisonTolerance = overrides.ChromaprintEpisodeComparisonTolerance.Value;
            if (overrides.ChromaprintEpisodeComparisonMinimumEpisodes.HasValue) clone.ChromaprintEpisodeComparisonMinimumEpisodes = overrides.ChromaprintEpisodeComparisonMinimumEpisodes.Value;
            if (overrides.ChromaprintStopSecondsFromEnd.HasValue) clone.ChromaprintStopSecondsFromEnd = overrides.ChromaprintStopSecondsFromEnd.Value;
            if (overrides.CpuUsageLimit.HasValue) clone.CpuUsageLimit = overrides.CpuUsageLimit.Value;
            if (overrides.CpuThrottleDelayMs.HasValue) clone.CpuThrottleDelayMs = overrides.CpuThrottleDelayMs.Value;
            if (overrides.LowerThreadPriority.HasValue) clone.LowerThreadPriority = overrides.LowerThreadPriority.Value;
            if (overrides.LowerProcessPriority.HasValue) clone.LowerProcessPriority = overrides.LowerProcessPriority.Value;
            if (overrides.EnableVideoValidation.HasValue) clone.EnableVideoValidation = overrides.EnableVideoValidation.Value;
            if (overrides.VideoValidationTimeoutSeconds.HasValue) clone.VideoValidationTimeoutSeconds = overrides.VideoValidationTimeoutSeconds.Value;

            return clone;
        }

        public object Get(GetDetectionMethodsRequest request)
        {
            try
            {
                var config = Plugin.Instance?.Configuration;
                if (config == null)
                {
                    return new { Success = false, Message = "Plugin configuration not available" };
                }

                var methods = new List<object>();

                methods.Add(new
                {
                    Method = "OCR",
                    Enabled = config.DetectionMode == DetectionMode.OcrOnly || 
                              config.DetectionMode == DetectionMode.OcrWithHashFallback || 
                              config.DetectionMode == DetectionMode.HashWithOcrFallback,
                    Primary = config.DetectionMode == DetectionMode.OcrOnly || 
                              config.DetectionMode == DetectionMode.OcrWithHashFallback,
                    Engine = config.OcrEngine.ToString(),
                    Endpoint = config.OcrEndpoint,
                    Languages = config.OcrLanguages,
                    Keywords = config.OcrDetectionKeywords,
                    Settings = new
                    {
                        config.OcrSearchStartValue,
                        config.OcrSearchStartUnit,
                        config.OcrMinutesFromEnd,
                        config.OcrFrameRate,
                        config.OcrMinimumMatches,
                        config.OcrMaxFramesToProcess,
                        config.OcrMaxAnalysisDuration,
                        config.OcrStopSecondsFromEnd,
                        config.OcrEnableEpisodeComparison,
                        config.OcrEpisodeComparisonTolerance,
                        config.OcrEpisodeComparisonMinimumEpisodes,
                        config.OcrEnableCharacterDensityDetection,
                        config.OcrCharacterDensityThreshold,
                        config.OcrCharacterDensityPrimaryMethod
                    }
                });

                methods.Add(new
                {
                    Method = "Chromaprint",
                    Enabled = config.DetectionMode == DetectionMode.HashOnly || 
                              config.DetectionMode == DetectionMode.OcrWithHashFallback || 
                              config.DetectionMode == DetectionMode.HashWithOcrFallback,
                    Primary = config.DetectionMode == DetectionMode.HashOnly || 
                              config.DetectionMode == DetectionMode.HashWithOcrFallback,
                    Settings = new
                    {
                        config.ChromaprintFingerprintSimilarityThreshold,
                        config.ChromaprintEnableEpisodeComparison,
                        config.ChromaprintEpisodeComparisonTolerance,
                        config.ChromaprintEpisodeComparisonMinimumEpisodes,
                        config.ChromaprintStopSecondsFromEnd
                    }
                });

                if (config.EnableAnimeDetection)
                {
                    methods.Add(new
                    {
                        Method = "AnimeBlackFrame",
                        Enabled = config.EnableAnimeDetection && config.AnimeDetectionMethod == AnimeDetectionMethod.BlackFrame,
                        Primary = config.AnimeDetectionMethod == AnimeDetectionMethod.BlackFrame,
                        Settings = new
                        {
                            config.BlackFrameMinimumPercentage,
                            config.BlackFrameThreshold
                        }
                    });
                }

                return new
                {
                    Success = true,
                    DetectionMode = config.DetectionMode.ToString(),
                    Methods = methods
                };
            }
            catch (Exception ex)
            {
                _logger?.ErrorException("Error getting detection methods", ex);
                return new { Success = false, Message = ex.Message };
            }
        }

        public object Get(GetDetectionResultsRequest request)
        {
            try
            {
                List<Episode> episodes = new List<Episode>();

                if (!string.IsNullOrEmpty(request.EpisodeId))
                {
                    var episode = _libraryManager.GetItemById(request.EpisodeId) as Episode;
                    if (episode != null)
                    {
                        episodes.Add(episode);
                    }
                }
                else if (!string.IsNullOrEmpty(request.SeriesId))
                {
                    var series = ItemLookupHelper.ResolveSeries(_libraryManager, request.SeriesId, _logger);
                    if (series != null)
                    {
                        var allEpisodes = ItemLookupHelper.GetSeriesEpisodes(_libraryManager, series.InternalId, _logger);
                        
                        if (request.SeasonNumber.HasValue)
                        {
                            episodes = allEpisodes.Where(e => e.ParentIndexNumber == request.SeasonNumber.Value).ToList();
                        }
                        else
                        {
                            episodes = allEpisodes;
                        }
                    }
                }
                else if (request.IncludeAllEpisodes)
                {
                    episodes = _libraryManager.GetItemList(new InternalItemsQuery
                    {
                        IncludeItemTypes = new[] { "Episode" },
                        IsVirtualItem = false,
                        HasPath = true
                    }).OfType<Episode>().ToList();
                }

                var markers = CreditsDetectionService.GetSeriesMarkers(episodes);
                var results = new List<object>();

                foreach (var marker in markers)
                {
                    var firstCreditsMarker = marker.HasCreditsMarker ? marker.Markers.FirstOrDefault() : null;
                    results.Add(new
                    {
                        EpisodeId = marker.EpisodeId,
                        SeriesName = (string?)null,
                        SeasonNumber = marker.Season,
                        EpisodeNumber = marker.Episode,
                        EpisodeName = marker.EpisodeName,
                        HasCreditsMarker = marker.HasCreditsMarker,
                        CreditsStartSeconds = firstCreditsMarker != null ? (object)(firstCreditsMarker.StartPositionTicks / (double)TimeSpan.TicksPerSecond) : null,
                        CreditsStartFormatted = firstCreditsMarker?.StartTime,
                        Duration = marker.Duration
                    });
                }

                return new
                {
                    Success = true,
                    TotalEpisodes = results.Count,
                    EpisodesWithMarkers = markers.Count(m => m.HasCreditsMarker),
                    Results = results
                };
            }
            catch (Exception ex)
            {
                _logger?.ErrorException("Error getting detection results", ex);
                return new { Success = false, Message = ex.Message };
            }
        }

        public object Get(GetDetectionHistoryRequest request)
        {
            try
            {
                return new
                {
                    Success = true,
                    Message = "Detection history is tracked via chapter markers. Use GetDetectionResults or GetSeriesMarkers to view current markers.",
                    History = new List<object>()
                };
            }
            catch (Exception ex)
            {
                _logger?.ErrorException("Error getting detection history", ex);
                return new { Success = false, Message = ex.Message };
            }
        }

        public object Get(GetEpisodeDetectionResultRequest request)
        {
            try
            {
                if (string.IsNullOrEmpty(request.EpisodeId))
                {
                    return new { Success = false, Message = "EpisodeId is required" };
                }

                var episode = _libraryManager.GetItemById(request.EpisodeId) as Episode;
                if (episode == null)
                {
                    return new { Success = false, Message = "Episode not found" };
                }

                var markers = CreditsDetectionService.GetSeriesMarkers(new List<Episode> { episode });
                if (markers == null || markers.Count == 0)
                {
                    return new
                    {
                        Success = true,
                        EpisodeId = request.EpisodeId,
                        EpisodeName = episode.Name,
                        SeriesName = episode.Series?.Name,
                        SeasonNumber = episode.ParentIndexNumber,
                        EpisodeNumber = episode.IndexNumber,
                        HasCreditsMarker = false,
                        Duration = episode.RunTimeTicks.HasValue ? TimeSpan.FromTicks(episode.RunTimeTicks.Value).ToString(@"hh\:mm\:ss") : null
                    };
                }

                var marker = markers[0];
                var firstCreditsMarker = marker.HasCreditsMarker ? marker.Markers.FirstOrDefault() : null;

                return new
                {
                    Success = true,
                    EpisodeId = request.EpisodeId,
                    EpisodeName = episode.Name,
                    SeriesName = episode.Series?.Name,
                    SeasonNumber = episode.ParentIndexNumber,
                    EpisodeNumber = episode.IndexNumber,
                    HasCreditsMarker = marker.HasCreditsMarker,
                    CreditsStartSeconds = firstCreditsMarker != null ? (object)(firstCreditsMarker.StartPositionTicks / (double)TimeSpan.TicksPerSecond) : null,
                    CreditsStartFormatted = firstCreditsMarker?.StartTime,
                    Duration = episode.RunTimeTicks.HasValue ? TimeSpan.FromTicks(episode.RunTimeTicks.Value).ToString(@"hh\:mm\:ss") : null
                };
            }
            catch (Exception ex)
            {
                _logger?.ErrorException("Error getting episode detection result", ex);
                return new { Success = false, Message = ex.Message };
            }
        }

        public object Get(GetFailedEpisodesRequest request)
        {
            try
            {
                var query = new InternalItemsQuery
                {
                    IncludeItemTypes = new[] { "Episode" },
                    IsVirtualItem = false,
                    HasPath = true
                };

                if (!string.IsNullOrEmpty(request.LibraryId))
                {
                    if (long.TryParse(request.LibraryId, out var libraryIdLong))
                    {
                        query.AncestorIds = new[] { libraryIdLong };
                    }
                }

                var allEpisodes = _libraryManager.GetItemList(query).OfType<Episode>().ToList();
                
                if (!string.IsNullOrEmpty(request.SeriesId))
                {
                    allEpisodes = allEpisodes.Where(e => e.Series?.Id.ToString() == request.SeriesId).ToList();
                }

                var failedEpisodes = allEpisodes
                    .Where(e => e.ProviderIds?.TryGetValue("EmbyCredits.Fail", out var failValue) == true && failValue == "true")
                    .Select(e => new
                    {
                        EpisodeId = e.Id.ToString(),
                        EpisodeName = e.Name,
                        SeriesName = e.Series?.Name,
                        SeriesId = e.Series?.Id.ToString(),
                        SeasonNumber = e.ParentIndexNumber,
                        EpisodeNumber = e.IndexNumber,
                        Path = e.Path
                    })
                    .OrderBy(e => e.SeriesName)
                    .ThenBy(e => e.SeasonNumber)
                    .ThenBy(e => e.EpisodeNumber)
                    .ToList();

                return new
                {
                    Success = true,
                    FailedEpisodes = failedEpisodes,
                    Count = failedEpisodes.Count
                };
            }
            catch (Exception ex)
            {
                _logger?.ErrorException("Error getting failed episodes", ex);
                return new { Success = false, Message = ex.Message };
            }
        }

        public object Post(ClearFailureMarkersRequest request)
        {
            try
            {
                if (request.EpisodeIds == null || request.EpisodeIds.Count == 0)
                {
                    return new { Success = false, Message = "No episode IDs provided" };
                }

                int clearedCount = 0;
                foreach (var episodeId in request.EpisodeIds)
                {
                    var episode = _libraryManager.GetItemById(episodeId) as Episode;
                    if (episode != null && episode.ProviderIds != null && episode.ProviderIds.ContainsKey("EmbyCredits.Fail"))
                    {
                        episode.ProviderIds.Remove("EmbyCredits.Fail");
                        _libraryManager.UpdateItem(episode, episode.Parent, ItemUpdateType.MetadataEdit, null!);
                        clearedCount++;
                    }
                }

                return new
                {
                    Success = true,
                    Message = $"Cleared failure markers for {clearedCount} episode(s)",
                    ClearedCount = clearedCount
                };
            }
            catch (Exception ex)
            {
                _logger?.ErrorException("Error clearing failure markers", ex);
                return new { Success = false, Message = ex.Message };
            }
        }

        public object Post(ClearAllFailureMarkersRequest request)
        {
            try
            {
                var query = new InternalItemsQuery
                {
                    IncludeItemTypes = new[] { "Episode" },
                    IsVirtualItem = false,
                    HasPath = true
                };

                if (!string.IsNullOrEmpty(request.LibraryId))
                {
                    if (long.TryParse(request.LibraryId, out var libraryIdLong))
                    {
                        query.AncestorIds = new[] { libraryIdLong };
                    }
                }

                var allEpisodes = _libraryManager.GetItemList(query).OfType<Episode>().ToList();
                
                if (!string.IsNullOrEmpty(request.SeriesId))
                {
                    allEpisodes = allEpisodes.Where(e => e.Series?.Id.ToString() == request.SeriesId).ToList();
                }

                int clearedCount = 0;
                foreach (var episode in allEpisodes)
                {
                    if (episode.ProviderIds != null && episode.ProviderIds.ContainsKey("EmbyCredits.Fail"))
                    {
                        episode.ProviderIds.Remove("EmbyCredits.Fail");
                        _libraryManager.UpdateItem(episode, episode.Parent, ItemUpdateType.MetadataEdit, null!);
                        clearedCount++;
                    }
                }

                return new
                {
                    Success = true,
                    Message = $"Cleared failure markers for {clearedCount} episode(s)",
                    ClearedCount = clearedCount
                };
            }
            catch (Exception ex)
            {
                _logger?.ErrorException("Error clearing all failure markers", ex);
                return new { Success = false, Message = ex.Message };
            }
        }

        private bool IsValidOcrEndpointUrl(string endpoint)
        {
            if (string.IsNullOrWhiteSpace(endpoint))
                return false;

            try
            {
                var uri = new Uri(endpoint);
                
                if (uri.Scheme != "http" && uri.Scheme != "https")
                    return false;

                var host = uri.Host.ToLowerInvariant();
                if (host == "localhost" || host == "127.0.0.1" || host == "::1")
                    return true;

                if (System.Net.IPAddress.TryParse(host, out var ipAddress))
                {
                    var bytes = ipAddress.GetAddressBytes();
                    if (bytes.Length == 4)
                    {
                        if (bytes[0] == 10) return true;
                        if (bytes[0] == 192 && bytes[1] == 168) return true;
                        if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) return true;
                    }
                }

                _logger?.Warn($"OCR endpoint is a public/non-local address: {endpoint}. Ensure this is intentional.");
                return true;
            }
            catch (Exception ex)
            {
                _logger?.Error($"Invalid OCR endpoint URI: {endpoint} - {ex.Message}");
                return false;
            }
        }

        private string SanitizeErrorMessage(string message)
        {
            if (string.IsNullOrEmpty(message))
                return "An error occurred";

            var sanitized = System.Text.RegularExpressions.Regex.Replace(
                message,
                @"[A-Za-z]:\\\S+|/(?:[a-zA-Z0-9_./\-])+",
                "[path]");
            
            return sanitized;
        }

        public object Get(GetTracerEpisodesRequest request)
        {
            try
            {
                var tracer = Plugin.TracerService;
                if (tracer == null)
                    return new { Success = false, Message = "Tracer service not available" };

                var entries = tracer.GetAll().Select(e => new
                {
                    e.EpisodeId,
                    e.SeriesName,
                    e.SeasonNumber,
                    e.EpisodeNumber,
                    e.EpisodeName,
                    AddedUtc = e.AddedUtc.ToString("o")
                }).ToList();

                var detected = tracer.GetAllDetected().Select(e => new
                {
                    e.EpisodeId,
                    e.SeriesName,
                    e.SeasonNumber,
                    e.EpisodeNumber,
                    e.EpisodeName,
                    AddedUtc = e.AddedUtc.ToString("o"),
                    DetectedUtc = e.DetectedUtc?.ToString("o")
                }).ToList();

                var failed = tracer.GetAllFailed().Select(e => new
                {
                    e.EpisodeId,
                    e.SeriesName,
                    e.SeasonNumber,
                    e.EpisodeNumber,
                    e.EpisodeName,
                    AddedUtc = e.AddedUtc.ToString("o"),
                    FailedUtc = e.FailedUtc?.ToString("o"),
                    e.FailureReason
                }).ToList();

                return new { Success = true, Count = entries.Count, Episodes = entries, Detected = detected, Failed = failed };
            }
            catch (Exception ex)
            {
                _logger?.ErrorException("Error getting tracer episodes", ex);
                return new { Success = false, Message = ex.Message };
            }
        }

        public object Post(DismissTracerEpisodeRequest request)
        {
            try
            {
                if (string.IsNullOrEmpty(request.EpisodeId))
                    return new { Success = false, Message = "EpisodeId is required" };

                Plugin.TracerService?.Remove(request.EpisodeId);
                return new { Success = true };
            }
            catch (Exception ex)
            {
                _logger?.ErrorException("Error dismissing tracer episode", ex);
                return new { Success = false, Message = ex.Message };
            }
        }

        public object Post(ClearTracerListRequest request)
        {
            try
            {
                Plugin.TracerService?.Clear();
                return new { Success = true };
            }
            catch (Exception ex)
            {
                _logger?.ErrorException("Error clearing tracer list", ex);
                return new { Success = false, Message = ex.Message };
            }
        }

        public object Post(ClearDetectedTracerListRequest request)
        {
            try
            {
                Plugin.TracerService?.ClearDetected();
                return new { Success = true };
            }
            catch (Exception ex)
            {
                _logger?.ErrorException("Error clearing detected tracer list", ex);
                return new { Success = false, Message = ex.Message };
            }
        }

        public object Post(ClearFailedTracerListRequest request)
        {
            try
            {
                Plugin.TracerService?.ClearFailed();
                return new { Success = true };
            }
            catch (Exception ex)
            {
                _logger?.ErrorException("Error clearing failed tracer list", ex);
                return new { Success = false, Message = ex.Message };
            }
        }
    }
}
