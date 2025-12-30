using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using System;
using System.Collections.Generic;
using System.Linq;

namespace EmbyCredits.Services
{
    /// <summary>
    /// Handles chapter marker operations for credits detection.
    /// </summary>
    public class ChapterMarkerService
    {
        private readonly ILogger _logger;
        private readonly IItemRepository _itemRepository;

        public ChapterMarkerService(ILogger logger, IItemRepository itemRepository)
        {
            _logger = logger;
            _itemRepository = itemRepository;
        }

        public void SaveCreditsMarker(Episode episode, double creditsStartSeconds)
        {
            try
            {
                var chapters = _itemRepository.GetChapters(episode)?.ToList() ?? new List<ChapterInfo>();

                var existingCreditsMarkers = chapters.Where(c =>
                {
                    var markerType = GetMarkerType(c);
                    if (markerType == "CreditsStart" || markerType == "Credits")
                        return true;

                    if (c.Name != null)
                    {
                        var nameLower = c.Name.ToLowerInvariant();
                        if (nameLower.Contains("credit") || 
                            nameLower.Contains("end title") ||
                            nameLower.Contains("ending") ||
                            nameLower == "credits")
                            return true;
                    }

                    var duration = episode.RunTimeTicks ?? 0;
                    if (duration > 0)
                    {
                        var positionRatio = (double)c.StartPositionTicks / duration;
                        if (positionRatio >= 0.80 && (string.IsNullOrEmpty(c.Name) || c.Name.Length < 3))
                            return true;
                    }

                    return false;
                }).ToList();

                if (existingCreditsMarkers.Count > 0)
                {
                    foreach (var marker in existingCreditsMarkers)
                    {
                        chapters.Remove(marker);
                    }
                    _logger.Info($"Removed {existingCreditsMarkers.Count} existing credits marker(s)");
                }

                var creditsMarker = new ChapterInfo
                {
                    Name = "Credits",
                    StartPositionTicks = (long)(creditsStartSeconds * TimeSpan.TicksPerSecond)
                };

                var markerTypeSet = SetMarkerType(creditsMarker, MarkerType.CreditsStart);
                _logger.Info($"MarkerType.CreditsStart set: {markerTypeSet}");

                if (markerTypeSet)
                {
                    var verifyType = GetMarkerType(creditsMarker);
                    _logger.Info($"Verified MarkerType value: {verifyType}");
                }

                chapters.Add(creditsMarker);
                _logger.Info($"Added new CreditsStart marker at {FormatTime(creditsStartSeconds)}");

                try
                {
                    _itemRepository.SaveChapters(episode.InternalId, chapters);
                    _logger.Info($"Saved chapter markers for {episode.Name}");
                }
                catch (Exception saveEx)
                {
                    _logger.ErrorException($"Failed to save chapters to repository for {episode.Name}", saveEx);
                    throw;
                }
            }
            catch (Exception ex)
            {
                _logger.ErrorException($"Error saving credits chapter marker for {episode.Name}", ex);
            }
        }

        public List<object> GetSeriesMarkers(List<Episode> episodes)
        {
            var result = new List<object>();

            foreach (var episode in episodes)
            {
                try
                {
                    var chapters = _itemRepository.GetChapters(episode)?.ToList() ?? new List<ChapterInfo>();

                    var creditsMarkers = chapters.Where(c =>
                    {
                        var markerType = GetMarkerType(c);
                        return markerType == "CreditsStart" || 
                               markerType == "Credits" ||
                               (c.Name != null && c.Name.ToLowerInvariant().Contains("credit"));
                    }).Select(c => new
                    {
                        Name = c.Name,
                        StartPositionTicks = c.StartPositionTicks,
                        StartTime = FormatTime(c.StartPositionTicks / TimeSpan.TicksPerSecond),
                        MarkerType = GetMarkerType(c)
                    }).ToList();

                    result.Add(new
                    {
                        EpisodeId = episode.Id.ToString(),
                        EpisodeName = episode.Name,
                        Season = episode.ParentIndexNumber,
                        Episode = episode.IndexNumber,
                        SeasonEpisode = $"S{episode.ParentIndexNumber:D2}E{episode.IndexNumber:D2}",
                        Duration = episode.RunTimeTicks.HasValue ? FormatTime(episode.RunTimeTicks.Value / TimeSpan.TicksPerSecond) : "Unknown",
                        HasCreditsMarker = creditsMarkers.Count > 0,
                        Markers = creditsMarkers,
                        AllChapters = chapters.Select(c => new
                        {
                            Name = c.Name,
                            StartTime = FormatTime(c.StartPositionTicks / TimeSpan.TicksPerSecond),
                            MarkerType = GetMarkerType(c)
                        }).ToList()
                    });
                }
                catch (Exception ex)
                {
                    _logger.Debug($"Error getting markers for episode {episode.Name}: {ex.Message}");
                }
            }

            return result;
        }

        private string? GetMarkerType(ChapterInfo chapter)
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
                _logger.Debug($"Error reading MarkerType property (Emby version compatibility issue): {ex.Message}");
            }
            return null;
        }

        private bool SetMarkerType(ChapterInfo chapter, MarkerType markerType)
        {
            try
            {
                if (chapter == null)
                    return false;
                    
                var chapterType = chapter.GetType();
                if (chapterType == null)
                    return false;
                    
                var markerTypeProp = chapterType.GetProperty("MarkerType");
                if (markerTypeProp != null && markerTypeProp.CanWrite)
                {
                    markerTypeProp.SetValue(chapter, markerType);
                    return true;
                }
                else
                {
                    _logger.Debug("MarkerType property not found or not writable (Emby version may not support this feature)");
                }
            }
            catch (Exception ex)
            {
                _logger.Debug($"Error setting MarkerType property (Emby version compatibility issue): {ex.Message}");
            }
            return false;
        }

        private string FormatTime(double seconds)
        {
            var ts = TimeSpan.FromSeconds(seconds);
            return $"{(int)ts.TotalMinutes}:{ts.Seconds:D2}";
        }
    }
}
