using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
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
    public partial class CreditsDetectionApiService
    {
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
                var errorMessage = "Error retrieving debug log. See server logs for details.";
                var errorBytes = System.Text.Encoding.UTF8.GetBytes(errorMessage);
                var errorStream = new MemoryStream(errorBytes);
                errorStream.Position = 0;
                return ToStaticResult(errorStream);
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
            if (overrides.LocalTesseractPath != null) clone.LocalTesseractPath = overrides.LocalTesseractPath;
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
            if (overrides.OcrAdaptiveSamplingEnabled.HasValue) clone.OcrAdaptiveSamplingEnabled = overrides.OcrAdaptiveSamplingEnabled.Value;
            if (overrides.OcrAdaptiveCoarseIntervalSeconds.HasValue) clone.OcrAdaptiveCoarseIntervalSeconds = overrides.OcrAdaptiveCoarseIntervalSeconds.Value;
            if (overrides.OcrAdaptiveRefinementRadiusSeconds.HasValue) clone.OcrAdaptiveRefinementRadiusSeconds = overrides.OcrAdaptiveRefinementRadiusSeconds.Value;
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
    }
}
