using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;

namespace EmbyCredits.Services
{
    public class CreditsBackupService
    {
        private readonly ILogger _logger;
        private readonly ILibraryManager _libraryManager;
        private readonly IItemRepository _itemRepository;

        public CreditsBackupService(ILogger logger, ILibraryManager libraryManager, IItemRepository itemRepository)
        {
            _logger = logger;
            _libraryManager = libraryManager;
            _itemRepository = itemRepository;
        }

        public Task<CreditsBackupResult> ExportCreditsMarkers(
            List<string>? libraryIds,
            List<string>? seriesIds,
            CancellationToken cancellationToken = default)
        {
            var result = new CreditsBackupResult { Success = true };
            var backupData = new List<CreditsBackupEntry>();

            try
            {
                _logger.Info("Starting credits markers export");

                var query = new InternalItemsQuery
                {
                    IncludeItemTypes = new[] { typeof(Episode).Name },
                    Recursive = true,
                    IsVirtualItem = false
                };

                var allEpisodes = _libraryManager.GetItemList(query).Cast<Episode>();

                if (libraryIds != null && libraryIds.Count > 0)
                {
                    allEpisodes = allEpisodes.Where(e => libraryIds.Contains(e.GetTopParent()?.Id.ToString() ?? ""));
                }

                if (seriesIds != null && seriesIds.Count > 0)
                {
                    allEpisodes = allEpisodes.Where(e => seriesIds.Contains(e.Series?.Id.ToString() ?? ""));
                }

                var episodesList = allEpisodes.ToList();
                _logger.Info($"Scanning {episodesList.Count} episodes for credits markers");

                foreach (var episode in episodesList)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var chapters = _itemRepository.GetChapters(episode);
                    if (chapters == null || chapters.Count == 0) continue;

                    var creditsMarker = chapters.FirstOrDefault(c => GetMarkerType(c) == "CreditsStart");

                    if (creditsMarker != null)
                    {
                        var series = episode.Series;
                        var entry = new CreditsBackupEntry
                        {
                            SeriesName = series?.Name ?? "Unknown",
                            SeriesId = series?.Id.ToString() ?? "",
                            TvdbId = series?.ProviderIds?.TryGetValue("Tvdb", out var tvdbId) == true ? tvdbId : null,
                            TmdbId = series?.ProviderIds?.TryGetValue("Tmdb", out var tmdbId) == true ? tmdbId : null,
                            ImdbId = series?.ProviderIds?.TryGetValue("Imdb", out var imdbId) == true ? imdbId : null,
                            TvdbEpisodeId = episode.ProviderIds?.TryGetValue("Tvdb", out var epTvdbId) == true ? epTvdbId : null,
                            SeasonNumber = episode.ParentIndexNumber ?? 0,
                            EpisodeNumber = episode.IndexNumber ?? 0,
                            EpisodeName = episode.Name,
                            EpisodeId = episode.Id.ToString(),
                            FilePath = episode.Path,
                            CreditsStartTicks = creditsMarker.StartPositionTicks
                        };

                        backupData.Add(entry);
                    }
                }

                var backup = new CreditsBackup
                {
                    Version = "1.0",
                    BackupDate = DateTime.UtcNow,
                    TotalEpisodes = episodesList.Count,
                    EpisodesWithCredits = backupData.Count,
                    Entries = backupData
                };

                var jsonOptions = new JsonSerializerOptions
                {
                    WriteIndented = true,
                    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
                };

                var json = JsonSerializer.Serialize(backup, jsonOptions);

                result.Success = true;
                result.TotalEpisodes = episodesList.Count;
                result.EpisodesWithCredits = backupData.Count;
                result.JsonData = json;
                result.Message = $"Successfully exported {backupData.Count} episodes with credits markers from {backup.TotalSeries} series";

                _logger.Info(result.Message);

                return Task.FromResult(result);
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Message = $"Export failed: {ex.Message}";
                _logger.ErrorException("Error during credits markers export", ex);
                return Task.FromResult(result);
            }
        }

        public Task<CreditsRestoreResult> ImportCreditsMarkers(
            string jsonData,
            bool overwriteExisting,
            CancellationToken cancellationToken = default)
        {
            var result = new CreditsRestoreResult { Success = true };
            int imported = 0;
            int skipped = 0;
            int notFound = 0;

            try
            {
                _logger.Info("Starting credits markers import");

                var backup = JsonSerializer.Deserialize<CreditsBackup>(jsonData);

                if (backup == null || backup.Entries == null || backup.Entries.Count == 0)
                {
                    result.Success = false;
                    result.Message = "Invalid backup file format or no entries found";
                    return Task.FromResult(result);
                }

                _logger.Info($"Importing {backup.Entries.Count} entries from backup dated {backup.BackupDate:yyyy-MM-dd HH:mm}");

                foreach (var entry in backup.Entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    Episode? episode = null;

                    if (!string.IsNullOrEmpty(entry.TvdbEpisodeId))
                    {
                        var allEpisodes = _libraryManager.GetItemList(new InternalItemsQuery
                        {
                            IncludeItemTypes = new[] { typeof(Episode).Name },
                            Recursive = true
                        }).Cast<Episode>();

                        foreach (var ep in allEpisodes)
                        {
                            if (ep.ProviderIds?.TryGetValue("Tvdb", out var epTvdbId) == true && epTvdbId == entry.TvdbEpisodeId)
                            {
                                episode = ep;
                                break;
                            }
                        }
                    }

                    if (episode == null && Guid.TryParse(entry.EpisodeId, out Guid episodeGuid))
                    {
                        episode = _libraryManager.GetItemById(episodeGuid) as Episode;
                    }

                    if (episode == null && !string.IsNullOrEmpty(entry.FilePath))
                    {
                        episode = _libraryManager.FindByPath(entry.FilePath, false) as Episode;
                    }

                    if (episode == null)
                    {
                        var episodeQuery = new InternalItemsQuery
                        {
                            IncludeItemTypes = new[] { typeof(Episode).Name },
                            ParentIndexNumber = entry.SeasonNumber,
                            IndexNumber = entry.EpisodeNumber,
                            Recursive = true
                        };

                        var matchingEpisodes = _libraryManager.GetItemList(episodeQuery).Cast<Episode>();

                        foreach (var ep in matchingEpisodes)
                        {
                            var series = ep.Series;
                            if (series?.ProviderIds != null)
                            {
                                var tvdbMatch = !string.IsNullOrEmpty(entry.TvdbId) && 
                                    series.ProviderIds.TryGetValue("Tvdb", out var seriesTvdbId) && 
                                    seriesTvdbId == entry.TvdbId;
                                var tmdbMatch = !string.IsNullOrEmpty(entry.TmdbId) && 
                                    series.ProviderIds.TryGetValue("Tmdb", out var seriesTmdbId) && 
                                    seriesTmdbId == entry.TmdbId;
                                var imdbMatch = !string.IsNullOrEmpty(entry.ImdbId) && 
                                    series.ProviderIds.TryGetValue("Imdb", out var seriesImdbId) && 
                                    seriesImdbId == entry.ImdbId;

                                if (tvdbMatch || tmdbMatch || imdbMatch)
                                {
                                    episode = ep;
                                    break;
                                }
                            }
                        }
                    }

                    if (episode == null)
                    {
                        _logger.Debug($"Episode not found: {entry.SeriesName} S{entry.SeasonNumber:D2}E{entry.EpisodeNumber:D2}");
                        notFound++;
                        continue;
                    }

                    if (!overwriteExisting)
                    {
                        var existingChapters = _itemRepository.GetChapters(episode);
                        if (existingChapters?.Any(c => GetMarkerType(c) == "CreditsStart") == true)
                        {
                            _logger.Debug($"Skipping {episode.Name} - already has credits marker");
                            skipped++;
                            continue;
                        }
                    }

                    var chapters = _itemRepository.GetChapters(episode)?.ToList() ?? new List<ChapterInfo>();

                    if (overwriteExisting)
                    {
                        chapters.RemoveAll(c => GetMarkerType(c) == "CreditsStart");
                    }

                    var creditsChapter = new ChapterInfo
                    {
                        Name = "Credits Start",
                        StartPositionTicks = entry.CreditsStartTicks
                    };

                    if (SetMarkerType(creditsChapter, CreditsMarkerType.CreditsStart))
                    {
                        chapters.Add(creditsChapter);
                        chapters = chapters.OrderBy(c => c.StartPositionTicks).ToList();
                        _itemRepository.SaveChapters(episode.InternalId, chapters);
                        imported++;
                        _logger.Info($"Restored credits marker for: {episode.Series?.Name} - S{episode.ParentIndexNumber:D2}E{episode.IndexNumber:D2} - {episode.Name}");
                    }
                    else
                    {
                        _logger.Warn($"Failed to set marker type for {episode.Name}");
                        notFound++;
                    }
                }

                result.ItemsImported = imported;
                result.ItemsSkipped = skipped;
                result.ItemsNotFound = notFound;
                result.Message = $"Import complete: {imported} imported, {skipped} skipped, {notFound} not found";

                _logger.Info(result.Message);

                return Task.FromResult(result);
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Message = $"Import failed: {ex.Message}";
                _logger.ErrorException("Error during credits markers import", ex);
                return Task.FromResult(result);
            }
        }

        private string? GetMarkerType(ChapterInfo chapter)
        {
            try
            {
                var markerTypeProp = chapter.GetType().GetProperty("MarkerType");
                if (markerTypeProp != null && markerTypeProp.CanRead)
                {
                    var value = markerTypeProp.GetValue(chapter);
                    return value?.ToString();
                }
            }
            catch (Exception ex)
            {
                _logger.Debug($"Error reading MarkerType property: {ex.Message}");
            }
            return null;
        }

        private bool SetMarkerType(ChapterInfo chapter, CreditsMarkerType markerType)
        {
            try
            {
                var markerTypeProp = chapter.GetType().GetProperty("MarkerType");
                if (markerTypeProp != null && markerTypeProp.CanWrite)
                {
                    markerTypeProp.SetValue(chapter, markerType);
                    return true;
                }
                else
                {
                    _logger.Warn("MarkerType property not found or not writable on ChapterInfo");
                }
            }
            catch (Exception ex)
            {
                _logger.ErrorException("Error setting MarkerType property", ex);
            }
            return false;
        }
    }

    public class CreditsBackup
    {
        public string Version { get; set; } = "1.0";
        public DateTime BackupDate { get; set; }
        public int TotalEpisodes { get; set; }
        public int EpisodesWithCredits { get; set; }

        [JsonIgnore]
        public int TotalSeries => Entries?.GroupBy(e => e.TvdbId ?? e.SeriesId).Count() ?? 0;

        public List<CreditsBackupEntry> Entries { get; set; } = new List<CreditsBackupEntry>();
    }

    public class CreditsBackupEntry
    {
        public string SeriesName { get; set; } = string.Empty;
        public string SeriesId { get; set; } = string.Empty;
        public string? TvdbId { get; set; }
        public string? TmdbId { get; set; }
        public string? ImdbId { get; set; }
        public string? TvdbEpisodeId { get; set; }
        public int SeasonNumber { get; set; }
        public int EpisodeNumber { get; set; }
        public string EpisodeName { get; set; } = string.Empty;
        public string EpisodeId { get; set; } = string.Empty;
        public string FilePath { get; set; } = string.Empty;
        public long CreditsStartTicks { get; set; }
    }

    public class CreditsBackupResult
    {
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;
        public int TotalEpisodes { get; set; }
        public int EpisodesWithCredits { get; set; }
        public string JsonData { get; set; } = string.Empty;
    }

    public class CreditsRestoreResult
    {
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;
        public int ItemsImported { get; set; }
        public int ItemsSkipped { get; set; }
        public int ItemsNotFound { get; set; }
    }

    public enum CreditsMarkerType
    {
        None = 0,
        IntroStart = 1,
        IntroEnd = 2,
        CreditsStart = 3
    }
}
