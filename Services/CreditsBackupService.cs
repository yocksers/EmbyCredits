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
                    IncludeItemTypes = new[] { "Episode" },
                    Recursive = true,
                    IsVirtualItem = false,
                    HasPath = true
                };

                _logger.Info($"Querying library for episodes with filter - Recursive: true, IsVirtualItem: false, HasPath: true");
                var allItems = _libraryManager.GetItemList(query);
                _logger.Info($"Raw query returned {allItems.Length} items");
                
                var allEpisodes = allItems.OfType<Episode>().ToList();
                _logger.Info($"After filtering to Episode type: {allEpisodes.Count} episodes");

                if (allEpisodes.Count == 0)
                {
                    _logger.Warn("No episodes found in library. Make sure you have TV shows in your Emby library.");
                    result.Success = true;
                    result.TotalEpisodes = 0;
                    result.EpisodesWithCredits = 0;
                    result.Message = "No episodes found in library";
                    
                    var emptyBackup = new CreditsBackup
                    {
                        Version = "1.0",
                        BackupDate = DateTime.UtcNow,
                        TotalEpisodes = 0,
                        EpisodesWithCredits = 0,
                        Entries = new List<CreditsBackupEntry>()
                    };
                    
                    result.JsonData = JsonSerializer.Serialize(emptyBackup, new JsonSerializerOptions 
                    { 
                        WriteIndented = true,
                        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
                    });
                    
                    return Task.FromResult(result);
                }

                if (libraryIds != null && libraryIds.Count > 0)
                {
                    _logger.Info($"Filtering by {libraryIds.Count} library IDs: {string.Join(", ", libraryIds)}");
                    
                    var sampleSize = Math.Min(5, allEpisodes.Count);
                    for (int i = 0; i < sampleSize; i++)
                    {
                        var e = allEpisodes[i];
                        var topParent = e.GetTopParent();
                        _logger.Info($"Sample episode {i+1}: '{e.Name}' - TopParent: {topParent?.Name} (ID: {topParent?.InternalId}, Type: {topParent?.GetType().Name})");
                    }
                    
                    var filteredEpisodes = allEpisodes.Where(e =>
                    {
                        var topParent = e.GetTopParent();
                        var internalIdStr = topParent?.InternalId.ToString();
                        
                        if (string.IsNullOrEmpty(internalIdStr))
                            return false;
                            
                        if (libraryIds.Contains(internalIdStr))
                            return true;
                            
                        if (long.TryParse(internalIdStr, out var id) && id > 0)
                        {
                            var collectionId = (id - 1).ToString();
                            if (libraryIds.Contains(collectionId))
                                return true;
                        }
                        
                        return false;
                    }).ToList();
                    
                    _logger.Info($"After library filtering: {filteredEpisodes.Count} episodes");
                    allEpisodes = filteredEpisodes;
                }

                if (seriesIds != null && seriesIds.Count > 0)
                {
                    _logger.Info($"Filtering by {seriesIds.Count} series IDs");
                    var filteredEpisodes = allEpisodes.Where(e => seriesIds.Contains(e.Series?.Id.ToString() ?? "")).ToList();
                    _logger.Info($"After series filtering: {filteredEpisodes.Count} episodes");
                    allEpisodes = filteredEpisodes;
                }

                var episodesList = allEpisodes;
                _logger.Info($"Scanning {episodesList.Count} episodes for credits markers");

                var progress = Plugin.Instance?.GetType().GetProperty("BackupExportProgress")?.GetValue(null) as CreditsDetectionProgress;
                if (progress != null)
                {
                    progress.Reset();
                    progress.IsRunning = true;
                    progress.TotalItems = episodesList.Count;
                    progress.StartTime = DateTime.Now;
                }

                int processed = 0;
                foreach (var episode in episodesList)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var chapters = _itemRepository.GetChapters(episode);
                    if (chapters == null || chapters.Count == 0)
                    {
                        processed++;
                        if (progress != null && processed % 10 == 0)
                            progress.ProcessedItems = processed;
                        continue;
                    }

                    var creditsMarker = chapters.FirstOrDefault(c => GetMarkerType(c) == "CreditsStart");

                    if (creditsMarker != null)
                    {
                        if (progress != null)
                        {
                            progress.CurrentItem = $"{episode.Series?.Name ?? "Unknown"} - S{episode.ParentIndexNumber:D2}E{episode.IndexNumber:D2}";
                        }

                        var series = episode.Series;
                        var entry = new CreditsBackupEntry
                        {
                            SeriesName = series?.Name ?? "Unknown",
                            SeriesId = series?.Id.ToString() ?? "",
                            TvdbId = series?.ProviderIds?.TryGetValue("Tvdb", out var tvdbId) == true ? tvdbId : null,
                            TmdbId = series?.ProviderIds?.TryGetValue("Tmdb", out var tmdbId) == true ? tmdbId : null,
                            ImdbId = series?.ProviderIds?.TryGetValue("Imdb", out var imdbId) == true ? imdbId : null,
                            TvdbEpisodeId = episode.ProviderIds?.TryGetValue("Tvdb", out var epTvdbId) == true ? epTvdbId : null,
                            TmdbEpisodeId = episode.ProviderIds?.TryGetValue("Tmdb", out var epTmdbId) == true ? epTmdbId : null,
                            ImdbEpisodeId = episode.ProviderIds?.TryGetValue("Imdb", out var epImdbId) == true ? epImdbId : null,
                            SeasonNumber = episode.ParentIndexNumber ?? 0,
                            EpisodeNumber = episode.IndexNumber ?? 0,
                            EpisodeName = episode.Name,
                            FilePath = episode.Path,
                            CreditsStartTicks = creditsMarker.StartPositionTicks
                        };

                        backupData.Add(entry);
                        if (progress != null) progress.SuccessfulItems++;
                    }
                    
                    processed++;
                    if (progress != null && processed % 10 == 0)
                        progress.ProcessedItems = processed;
                }
                
                if (progress != null)
                    progress.ProcessedItems = processed;

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

                if (progress != null)
                {
                    progress.IsRunning = false;
                    progress.EndTime = DateTime.Now;
                    progress.CurrentItem = "Export Complete";
                }

                return Task.FromResult(result);
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Message = $"Export failed: {ex.Message}";
                _logger.ErrorException("Error during credits markers export", ex);
                
                var progress = Plugin.Instance?.GetType().GetProperty("BackupExportProgress")?.GetValue(null) as CreditsDetectionProgress;
                if (progress != null)
                {
                    progress.IsRunning = false;
                    progress.EndTime = DateTime.Now;
                    progress.CurrentItem = "Export Failed";
                }
                
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

                var progress = Plugin.Instance?.GetType().GetProperty("BackupImportProgress")?.GetValue(null) as CreditsDetectionProgress;
                if (progress != null)
                {
                    progress.Reset();
                    progress.IsRunning = true;
                    progress.TotalItems = backup.Entries.Count;
                    progress.StartTime = DateTime.Now;
                    progress.CurrentItem = "Building episode cache...";
                }

                _logger.Info("Building episode lookup caches for fast matching");
                var allEpisodes = _libraryManager.GetItemList(new InternalItemsQuery
                {
                    IncludeItemTypes = new[] { typeof(Episode).Name },
                    Recursive = true
                }).Cast<Episode>().ToList();

                var episodesByTvdbId = new Dictionary<string, Episode>(allEpisodes.Count / 2);
                var episodesByTmdbId = new Dictionary<string, Episode>(allEpisodes.Count / 2);
                var episodesByImdbId = new Dictionary<string, Episode>(allEpisodes.Count / 10);
                var episodesBySeriesAndNumber = new Dictionary<string, List<Episode>>(allEpisodes.Count);
                var episodesByPath = new Dictionary<string, Episode>(allEpisodes.Count, StringComparer.OrdinalIgnoreCase);

                foreach (var ep in allEpisodes)
                {
                    if (ep.ProviderIds?.TryGetValue("Tvdb", out var epTvdbId) == true && !string.IsNullOrEmpty(epTvdbId))
                    {
                        episodesByTvdbId[epTvdbId] = ep;
                    }
                    
                    if (ep.ProviderIds?.TryGetValue("Tmdb", out var epTmdbId) == true && !string.IsNullOrEmpty(epTmdbId))
                    {
                        episodesByTmdbId[epTmdbId] = ep;
                    }
                    
                    if (ep.ProviderIds?.TryGetValue("Imdb", out var epImdbId) == true && !string.IsNullOrEmpty(epImdbId))
                    {
                        episodesByImdbId[epImdbId] = ep;
                    }
                    
                    if (!string.IsNullOrEmpty(ep.Path))
                    {
                        episodesByPath[ep.Path] = ep;
                    }
                    
                    var series = ep.Series;
                    if (series?.ProviderIds != null && ep.ParentIndexNumber.HasValue && ep.IndexNumber.HasValue)
                    {
                        var tvdbId = series.ProviderIds.TryGetValue("Tvdb", out var sTvdbId) ? sTvdbId : null;
                        var tmdbId = series.ProviderIds.TryGetValue("Tmdb", out var sTmdbId) ? sTmdbId : null;
                        var imdbId = series.ProviderIds.TryGetValue("Imdb", out var sImdbId) ? sImdbId : null;
                        
                        if (!string.IsNullOrEmpty(tvdbId))
                        {
                            var key = $"tvdb:{tvdbId}:S{ep.ParentIndexNumber:D2}E{ep.IndexNumber:D2}";
                            if (!episodesBySeriesAndNumber.ContainsKey(key))
                                episodesBySeriesAndNumber[key] = new List<Episode>();
                            episodesBySeriesAndNumber[key].Add(ep);
                        }
                        if (!string.IsNullOrEmpty(tmdbId))
                        {
                            var key = $"tmdb:{tmdbId}:S{ep.ParentIndexNumber:D2}E{ep.IndexNumber:D2}";
                            if (!episodesBySeriesAndNumber.ContainsKey(key))
                                episodesBySeriesAndNumber[key] = new List<Episode>();
                            episodesBySeriesAndNumber[key].Add(ep);
                        }
                        if (!string.IsNullOrEmpty(imdbId))
                        {
                            var key = $"imdb:{imdbId}:S{ep.ParentIndexNumber:D2}E{ep.IndexNumber:D2}";
                            if (!episodesBySeriesAndNumber.ContainsKey(key))
                                episodesBySeriesAndNumber[key] = new List<Episode>();
                            episodesBySeriesAndNumber[key].Add(ep);
                        }
                    }
                }

                _logger.Info($"Episode cache built: {episodesByTvdbId.Count} by TVDB ID, {episodesByTmdbId.Count} by TMDB ID, {episodesByImdbId.Count} by IMDB ID, {episodesByPath.Count} by path");

                int processed = 0;
                foreach (var entry in backup.Entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    Episode? episode = null;

                    if (!string.IsNullOrEmpty(entry.TvdbEpisodeId) && episodesByTvdbId.TryGetValue(entry.TvdbEpisodeId, out var epByTvdb))
                    {
                        episode = epByTvdb;
                    }

                    if (episode == null && !string.IsNullOrEmpty(entry.TmdbEpisodeId) && episodesByTmdbId.TryGetValue(entry.TmdbEpisodeId, out var epByTmdb))
                    {
                        episode = epByTmdb;
                    }

                    if (episode == null && !string.IsNullOrEmpty(entry.ImdbEpisodeId) && episodesByImdbId.TryGetValue(entry.ImdbEpisodeId, out var epByImdb))
                    {
                        episode = epByImdb;
                    }

                    if (episode == null)
                    {
                        if (!string.IsNullOrEmpty(entry.TvdbId))
                        {
                            var key = $"tvdb:{entry.TvdbId}:S{entry.SeasonNumber:D2}E{entry.EpisodeNumber:D2}";
                            if (episodesBySeriesAndNumber.TryGetValue(key, out var matches) && matches.Count > 0)
                            {
                                episode = matches[0];
                            }
                        }
                        
                        if (episode == null && !string.IsNullOrEmpty(entry.TmdbId))
                        {
                            var key = $"tmdb:{entry.TmdbId}:S{entry.SeasonNumber:D2}E{entry.EpisodeNumber:D2}";
                            if (episodesBySeriesAndNumber.TryGetValue(key, out var matches) && matches.Count > 0)
                            {
                                episode = matches[0];
                            }
                        }
                        
                        if (episode == null && !string.IsNullOrEmpty(entry.ImdbId))
                        {
                            var key = $"imdb:{entry.ImdbId}:S{entry.SeasonNumber:D2}E{entry.EpisodeNumber:D2}";
                            if (episodesBySeriesAndNumber.TryGetValue(key, out var matches) && matches.Count > 0)
                            {
                                episode = matches[0];
                            }
                        }
                    }

                    if (episode == null && !string.IsNullOrEmpty(entry.FilePath) && episodesByPath.TryGetValue(entry.FilePath, out var epByPath))
                    {
                        episode = epByPath;
                    }

                    if (episode == null)
                    {
                        _logger.Debug($"Episode not found: {entry.SeriesName} S{entry.SeasonNumber:D2}E{entry.EpisodeNumber:D2}");
                        notFound++;
                        if (progress != null) progress.FailedItems++;
                        processed++;
                        if (progress != null && processed % 5 == 0)
                        {
                            progress.ProcessedItems = processed;
                            progress.CurrentItem = $"{entry.SeriesName} - S{entry.SeasonNumber:D2}E{entry.EpisodeNumber:D2}";
                        }
                        continue;
                    }

                    if (progress != null && processed % 5 == 0)
                    {
                        progress.CurrentItem = $"{entry.SeriesName} - S{entry.SeasonNumber:D2}E{entry.EpisodeNumber:D2}";
                    }

                    if (!overwriteExisting)
                    {
                        var existingChapters = _itemRepository.GetChapters(episode);
                        if (existingChapters?.Any(c => GetMarkerType(c) == "CreditsStart") == true)
                        {
                            _logger.Debug($"Skipping {episode.Name} - already has credits marker");
                            skipped++;
                            processed++;
                            if (progress != null && processed % 5 == 0)
                                progress.ProcessedItems = processed;
                            continue;
                        }
                    }

                    if (!episode.RunTimeTicks.HasValue || episode.RunTimeTicks.Value <= 0)
                    {
                        _logger.Debug($"Skipping {episode.Name} - no valid runtime information");
                        notFound++;
                        if (progress != null) progress.FailedItems++;
                        processed++;
                        if (progress != null && processed % 5 == 0)
                            progress.ProcessedItems = processed;
                        continue;
                    }

                    if (entry.CreditsStartTicks >= episode.RunTimeTicks.Value)
                    {
                        var timestampSeconds = entry.CreditsStartTicks / (double)TimeSpan.TicksPerSecond;
                        var durationSeconds = episode.RunTimeTicks.Value / (double)TimeSpan.TicksPerSecond;
                        _logger.Warn($"Skipping {episode.Name} - timestamp ({timestampSeconds:F1}s) exceeds video duration ({durationSeconds:F1}s)");
                        notFound++;
                        if (progress != null) progress.FailedItems++;
                        processed++;
                        if (progress != null && processed % 5 == 0)
                            progress.ProcessedItems = processed;
                        continue;
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
                        if (progress != null) progress.SuccessfulItems++;
                        _logger.Info($"Restored credits marker for: {episode.Series?.Name} - S{episode.ParentIndexNumber:D2}E{episode.IndexNumber:D2} - {episode.Name}");
                    }
                    else
                    {
                        _logger.Warn($"Failed to set marker type for {episode.Name}");
                        notFound++;
                        if (progress != null) progress.FailedItems++;
                    }
                    
                    processed++;
                    if (progress != null && processed % 5 == 0)
                        progress.ProcessedItems = processed;
                }
                
                if (progress != null)
                    progress.ProcessedItems = processed;

                result.ItemsImported = imported;
                result.ItemsSkipped = skipped;
                result.ItemsNotFound = notFound;
                result.Message = $"Import complete: {imported} imported, {skipped} skipped, {notFound} not found";

                _logger.Info(result.Message);

                if (progress != null)
                {
                    progress.IsRunning = false;
                    progress.EndTime = DateTime.Now;
                    progress.CurrentItem = "Import Complete";
                }

                return Task.FromResult(result);
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Message = $"Import failed: {ex.Message}";
                _logger.ErrorException("Error during credits markers import", ex);
                
                var progress = Plugin.Instance?.GetType().GetProperty("BackupImportProgress")?.GetValue(null) as CreditsDetectionProgress;
                if (progress != null)
                {
                    progress.IsRunning = false;
                    progress.EndTime = DateTime.Now;
                    progress.CurrentItem = "Import Failed";
                }
                
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
        public string? TmdbEpisodeId { get; set; }
        public string? ImdbEpisodeId { get; set; }
        public int SeasonNumber { get; set; }
        public int EpisodeNumber { get; set; }
        public string EpisodeName { get; set; } = string.Empty;
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
