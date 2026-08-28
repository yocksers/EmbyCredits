using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using EmbyCredits.Api;
using EmbyCredits.Services;
using EmbyCredits.Services.Utilities;

namespace EmbyCredits.Services
{
    public partial class CreditsDetectionApiService
    {
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
    }
}
