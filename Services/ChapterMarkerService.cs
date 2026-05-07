using EmbyCredits.Services.Utilities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using System;
using System.Collections.Generic;
using System.Linq;

namespace EmbyCredits.Services
{
    public class CreditMarkerData
    {
        public string? Name { get; set; }
        public long StartPositionTicks { get; set; }
        public string StartTime { get; set; } = string.Empty;
        public string? MarkerType { get; set; }
    }

    public class EpisodeMarkerInfo
    {
        public string EpisodeId { get; set; } = string.Empty;
        public string EpisodeName { get; set; } = string.Empty;
        public int? Season { get; set; }
        public int? Episode { get; set; }
        public string SeasonEpisode { get; set; } = string.Empty;
        public string Duration { get; set; } = string.Empty;
        public double DurationSeconds { get; set; }
        public bool HasCreditsMarker { get; set; }
        public List<CreditMarkerData> Markers { get; set; } = new List<CreditMarkerData>();
    }

    public class ChapterMarkerService
    {
        private readonly ILogger _logger;
        private readonly IItemRepository _itemRepository;

        private static readonly System.Reflection.PropertyInfo? _markerTypeProp =
            typeof(ChapterInfo).GetProperty("MarkerType");

        public ChapterMarkerService(ILogger logger, IItemRepository itemRepository)
        {
            _logger = logger;
            _itemRepository = itemRepository;
        }

        public void SaveCreditsMarker(Episode episode, double creditsStartSeconds)
        {
            try
            {
                if (!episode.RunTimeTicks.HasValue || episode.RunTimeTicks.Value <= 0)
                {
                    _logger.Error($"Cannot save credits marker for {episode.Name} - no valid runtime information");
                    throw new InvalidOperationException("Episode has no valid runtime information");
                }

                var durationSeconds = episode.RunTimeTicks.Value / (double)TimeSpan.TicksPerSecond;
                if (creditsStartSeconds < 0)
                {
                    _logger.Error($"Cannot save credits marker for {episode.Name} - negative timestamp: {creditsStartSeconds}s");
                    throw new ArgumentOutOfRangeException(nameof(creditsStartSeconds), "Timestamp cannot be negative");
                }

                if (creditsStartSeconds >= durationSeconds)
                {
                    _logger.Error($"Cannot save credits marker for {episode.Name} - timestamp ({creditsStartSeconds:F1}s) exceeds video duration ({durationSeconds:F1}s)");
                    throw new ArgumentOutOfRangeException(nameof(creditsStartSeconds), $"Timestamp {creditsStartSeconds:F1}s exceeds video duration {durationSeconds:F1}s");
                }

                // Clamp to 1 second before the stored duration to prevent floating-point
                // precision mismatches with the actual media file duration from causing
                // Emby's ChapterImagesTask to reject the chapter as out-of-range.
                var safeMax = durationSeconds - 1.0;
                if (creditsStartSeconds > safeMax && safeMax > 0)
                {
                    _logger.Info($"Clamping credits marker for {episode.Name} from {creditsStartSeconds:F3}s to {safeMax:F3}s (within 1s of duration)");
                    creditsStartSeconds = safeMax;
                }

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

                int insertIndex = chapters.FindIndex(c => c.StartPositionTicks > creditsMarker.StartPositionTicks);
                if (insertIndex == -1)
                {
                    chapters.Add(creditsMarker);
                }
                else
                {
                    chapters.Insert(insertIndex, creditsMarker);
                }
                _logger.Info($"Added new CreditsStart marker at {FormatTime(creditsStartSeconds)} at index {(insertIndex == -1 ? chapters.Count - 1 : insertIndex)}");

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

        public List<EpisodeMarkerInfo> GetSeriesMarkers(List<Episode> episodes)
        {
            var result = new List<EpisodeMarkerInfo>(episodes.Count);

            foreach (var episode in episodes)
            {
                try
                {
                    var chapters = _itemRepository.GetChapters(episode)?.ToList() ?? new List<ChapterInfo>();

                    var creditsMarkers = chapters.Where(c =>
                    {
                        var markerType = GetMarkerType(c);
                        return markerType == "CreditsStart" || markerType == "Credits";
                    }).Select(c => new CreditMarkerData
                    {
                        Name = c.Name,
                        StartPositionTicks = c.StartPositionTicks,
                        StartTime = FormatTime(c.StartPositionTicks / TimeSpan.TicksPerSecond),
                        MarkerType = GetMarkerType(c)
                    }).ToList();

                    result.Add(new EpisodeMarkerInfo
                    {
                        EpisodeId = episode.Id.ToString(),
                        EpisodeName = episode.Name,
                        Season = episode.ParentIndexNumber,
                        Episode = episode.IndexNumber,
                        SeasonEpisode = $"S{episode.ParentIndexNumber:D2}E{episode.IndexNumber:D2}",
                        Duration = episode.RunTimeTicks.HasValue ? FormatTime(episode.RunTimeTicks.Value / TimeSpan.TicksPerSecond) : "Unknown",
                        DurationSeconds = episode.RunTimeTicks.HasValue ? episode.RunTimeTicks.Value / (double)TimeSpan.TicksPerSecond : 0,
                        HasCreditsMarker = creditsMarkers.Count > 0,
                        Markers = creditsMarkers
                    });
                }
                catch (Exception ex)
                {
                    _logger.Debug($"Error getting markers for episode {episode.Name}: {ex.Message}");
                }
            }

            return result;
        }

        public List<ChapterInfo> GetChapters(BaseItem item)
        {
            return _itemRepository.GetChapters(item)?.ToList() ?? new List<ChapterInfo>();
        }

        private string? GetMarkerType(ChapterInfo chapter)
        {
            try
            {
                if (chapter == null)
                    return null;

                if (_markerTypeProp != null && _markerTypeProp.CanRead)
                {
                    var value = _markerTypeProp.GetValue(chapter);
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

                if (_markerTypeProp != null && _markerTypeProp.CanWrite)
                {
                    _markerTypeProp.SetValue(chapter, markerType);
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

        private string FormatTime(double seconds) => ItemLookupHelper.FormatTime(seconds);

        public string GetChapterMarkerType(ChapterInfo chapter)
        {
            return GetMarkerType(chapter) ?? "Chapter";
        }

    }
}
