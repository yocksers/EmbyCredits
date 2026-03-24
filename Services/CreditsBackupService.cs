using System;
using System.Collections.Generic;
using System.IO;
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

        private static readonly JsonSerializerOptions _jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        public CreditsBackupService(ILogger logger, ILibraryManager libraryManager, IItemRepository itemRepository)
        {
            _logger = logger;
            _libraryManager = libraryManager;
            _itemRepository = itemRepository;
        }

        private static string SanitizeFileName(string name)
        {
            var invalidChars = Path.GetInvalidFileNameChars();
            var sanitized = new System.Text.StringBuilder();
            foreach (var c in name)
            {
                sanitized.Append(Array.IndexOf(invalidChars, c) >= 0 ? '_' : c);
            }
            var result = sanitized.ToString().Trim().TrimEnd('.');
            return string.IsNullOrEmpty(result) ? "Unknown" : result;
        }

        private static string GetSeriesBackupPattern(string seriesName)
        {
            return $"{SanitizeFileName(seriesName)}_*.json";
        }

        private string? FindLatestSeriesBackupFile(string seriesName, string backupFolder)
        {
            if (!Directory.Exists(backupFolder))
                return null;

            var pattern = GetSeriesBackupPattern(seriesName);
            var files = Directory.GetFiles(backupFolder, pattern);
            if (files.Length == 0)
                return null;

            return files
                .Select(f => new FileInfo(f))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .First()
                .FullName;
        }

        public void SaveSeriesBackupToFile(Series series, List<Episode> episodes, string backupFolder, int maxBackups)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(backupFolder))
                {
                    _logger.Warn("Backup folder not configured — skipping per-series backup");
                    return;
                }

                if (!Directory.Exists(backupFolder))
                    Directory.CreateDirectory(backupFolder);

                var backupEntries = new List<CreditsBackupEntry>();
                foreach (var episode in episodes)
                {
                    var chapters = _itemRepository.GetChapters(episode);
                    var creditsMarker = chapters?.FirstOrDefault(c => GetMarkerType(c) == "CreditsStart");
                    if (creditsMarker == null)
                        continue;

                    long? epFileSize = null;
                    DateTime? epFileModified = null;
                    if (!string.IsNullOrEmpty(episode.Path) && File.Exists(episode.Path))
                    {
                        var fi = new FileInfo(episode.Path);
                        epFileSize = fi.Length;
                        epFileModified = fi.LastWriteTimeUtc;
                    }

                    backupEntries.Add(new CreditsBackupEntry
                    {
                        SeriesName = series.Name,
                        SeriesId = series.Id.ToString(),
                        TvdbId = series.ProviderIds?.TryGetValue("Tvdb", out var tvdbId) == true ? tvdbId : null,
                        TmdbId = series.ProviderIds?.TryGetValue("Tmdb", out var tmdbId) == true ? tmdbId : null,
                        ImdbId = series.ProviderIds?.TryGetValue("Imdb", out var imdbId) == true ? imdbId : null,
                        TvdbEpisodeId = episode.ProviderIds?.TryGetValue("Tvdb", out var epTvdbId) == true ? epTvdbId : null,
                        TmdbEpisodeId = episode.ProviderIds?.TryGetValue("Tmdb", out var epTmdbId) == true ? epTmdbId : null,
                        ImdbEpisodeId = episode.ProviderIds?.TryGetValue("Imdb", out var epImdbId) == true ? epImdbId : null,
                        SeasonNumber = episode.ParentIndexNumber ?? 0,
                        EpisodeNumber = episode.IndexNumber ?? 0,
                        EpisodeName = episode.Name,
                        FilePath = episode.Path,
                        CreditsStartTicks = creditsMarker.StartPositionTicks,
                        LastDetectedFileSize = epFileSize,
                        LastDetectedModified = epFileModified
                    });
                }

                var backup = new CreditsBackup
                {
                    Version = "1.0",
                    BackupDate = DateTime.UtcNow,
                    TotalEpisodes = episodes.Count,
                    EpisodesWithCredits = backupEntries.Count,
                    Entries = backupEntries
                };

                var json = JsonSerializer.Serialize(backup, _jsonOptions);
                var safeName = SanitizeFileName(series.Name);
                // Overwrite the existing backup file so auto-restore always reads current data.
                // Only create a new timestamped file when no backup exists yet.
                var existingBackupFile = FindLatestSeriesBackupFile(series.Name, backupFolder);
                var filePath = existingBackupFile ?? Path.Combine(backupFolder, $"{safeName}_{DateTime.Now:yyyy-MM-dd_HHmmss}.json");

                File.WriteAllText(filePath, json);
                _logger.Info($"Per-series backup saved: {filePath} ({backupEntries.Count} episodes with credits)");

                RotateSeriesBackups(backupFolder, series.Name, maxBackups > 0 ? maxBackups : 10);
            }
            catch (Exception ex)
            {
                _logger.ErrorException($"Error saving per-series backup for {series.Name}", ex);
            }
        }

        /// <summary>
        /// Returns true when the episode's media file has changed (size or modification time differs)
        /// since the last time a fingerprint was recorded in the backup, or when no fingerprint exists.
        /// Returns true on any error so detection is never silently skipped.
        /// </summary>
        public bool HasFileChanged(Episode episode, string backupFolder)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(backupFolder) || string.IsNullOrEmpty(episode.Path))
                    return true;

                var series = episode.Series;
                if (series == null) return true;

                var backupFile = FindLatestSeriesBackupFile(series.Name, backupFolder);
                if (backupFile == null) return true;

                CreditsBackup? backup;
                try { backup = JsonSerializer.Deserialize<CreditsBackup>(File.ReadAllText(backupFile)); }
                catch { return true; }

                if (backup?.Entries == null) return true;

                CreditsBackupEntry? entry = null;
                if (episode.ProviderIds?.TryGetValue("Tvdb", out var epTvdb) == true && !string.IsNullOrEmpty(epTvdb))
                    entry = backup.Entries.FirstOrDefault(e => e.TvdbEpisodeId == epTvdb);
                if (entry == null && !string.IsNullOrEmpty(episode.Path))
                    entry = backup.Entries.FirstOrDefault(e => string.Equals(e.FilePath, episode.Path, StringComparison.OrdinalIgnoreCase));
                if (entry == null && episode.ParentIndexNumber.HasValue && episode.IndexNumber.HasValue)
                    entry = backup.Entries.FirstOrDefault(e => e.SeasonNumber == episode.ParentIndexNumber.Value && e.EpisodeNumber == episode.IndexNumber.Value);

                if (entry == null || !entry.LastDetectedFileSize.HasValue || !entry.LastDetectedModified.HasValue)
                    return true; // no fingerprint yet — run detection

                if (!File.Exists(episode.Path)) return true;

                var fi = new FileInfo(episode.Path);
                return fi.Length != entry.LastDetectedFileSize.Value ||
                       Math.Abs((fi.LastWriteTimeUtc - entry.LastDetectedModified.Value).TotalSeconds) > 2;
            }
            catch (Exception ex)
            {
                _logger.Warn($"Error checking file fingerprint for '{episode.Name}': {ex.Message}");
                return true;
            }
        }

        /// <summary>
        /// Updates (or creates) the series backup file with the new credits timestamp for a single episode.
        /// Call this after any single-episode marker save so the backup stays in sync.
        /// </summary>
        public void UpsertEpisodeInSeriesBackup(Episode episode, long creditsStartTicks, string backupFolder)
        {
            try
            {
                var series = episode.Series;
                if (series == null || string.IsNullOrWhiteSpace(backupFolder))
                    return;

                if (!Directory.Exists(backupFolder))
                    Directory.CreateDirectory(backupFolder);

                CreditsBackup backup;
                string filePath;
                var existingFile = FindLatestSeriesBackupFile(series.Name, backupFolder);

                if (existingFile != null)
                {
                    filePath = existingFile;
                    try
                    {
                        backup = JsonSerializer.Deserialize<CreditsBackup>(File.ReadAllText(filePath))
                            ?? new CreditsBackup { Version = "1.0", BackupDate = DateTime.UtcNow, Entries = new List<CreditsBackupEntry>() };
                        backup.Entries ??= new List<CreditsBackupEntry>();
                    }
                    catch
                    {
                        backup = new CreditsBackup { Version = "1.0", BackupDate = DateTime.UtcNow, Entries = new List<CreditsBackupEntry>() };
                    }
                }
                else
                {
                    var safeName = SanitizeFileName(series.Name);
                    filePath = Path.Combine(backupFolder, $"{safeName}_{DateTime.Now:yyyy-MM-dd_HHmmss}.json");
                    backup = new CreditsBackup { Version = "1.0", BackupDate = DateTime.UtcNow, Entries = new List<CreditsBackupEntry>() };
                }

                // Resolve episode provider IDs upfront for use in both Remove and Add
                string? epTvdb = episode.ProviderIds?.TryGetValue("Tvdb", out var t1) == true ? t1 : null;
                string? epTmdb = episode.ProviderIds?.TryGetValue("Tmdb", out var t2) == true ? t2 : null;
                string? epImdb = episode.ProviderIds?.TryGetValue("Imdb", out var t3) == true ? t3 : null;

                // Remove any existing entry for this episode (match on provider ID, path, or S/E numbers)
                backup.Entries.RemoveAll(e =>
                    (!string.IsNullOrEmpty(epTvdb) && e.TvdbEpisodeId == epTvdb) ||
                    (!string.IsNullOrEmpty(episode.Path) && string.Equals(e.FilePath, episode.Path, StringComparison.OrdinalIgnoreCase)) ||
                    (episode.ParentIndexNumber.HasValue && episode.IndexNumber.HasValue &&
                     e.SeasonNumber == episode.ParentIndexNumber.Value && e.EpisodeNumber == episode.IndexNumber.Value));

                // Read current file fingerprint
                long? fileSize = null;
                DateTime? fileModified = null;
                if (!string.IsNullOrEmpty(episode.Path) && File.Exists(episode.Path))
                {
                    var fi = new FileInfo(episode.Path);
                    fileSize = fi.Length;
                    fileModified = fi.LastWriteTimeUtc;
                }

                // Add the updated entry
                backup.Entries.Add(new CreditsBackupEntry
                {
                    SeriesName = series.Name,
                    SeriesId = series.Id.ToString(),
                    TvdbId = series.ProviderIds?.TryGetValue("Tvdb", out var sTvdb) == true ? sTvdb : null,
                    TmdbId = series.ProviderIds?.TryGetValue("Tmdb", out var sTmdb) == true ? sTmdb : null,
                    ImdbId = series.ProviderIds?.TryGetValue("Imdb", out var sImdb) == true ? sImdb : null,
                    TvdbEpisodeId = epTvdb,
                    TmdbEpisodeId = epTmdb,
                    ImdbEpisodeId = epImdb,
                    SeasonNumber = episode.ParentIndexNumber ?? 0,
                    EpisodeNumber = episode.IndexNumber ?? 0,
                    EpisodeName = episode.Name,
                    FilePath = episode.Path,
                    CreditsStartTicks = creditsStartTicks,
                    LastDetectedFileSize = fileSize,
                    LastDetectedModified = fileModified
                });

                backup.BackupDate = DateTime.UtcNow;
                backup.EpisodesWithCredits = backup.Entries.Count;

                File.WriteAllText(filePath, JsonSerializer.Serialize(backup, _jsonOptions));
                _logger.Info($"Auto-backup: updated entry for '{episode.Name}' in '{Path.GetFileName(filePath)}'");
            }
            catch (Exception ex)
            {
                _logger.ErrorException($"Error updating backup entry for '{episode.Name}'", ex);
            }
        }

        private void RotateSeriesBackups(string backupFolder, string seriesName, int maxBackups)
        {
            try
            {
                var pattern = GetSeriesBackupPattern(seriesName);
                var files = Directory.GetFiles(backupFolder, pattern)
                    .Select(f => new FileInfo(f))
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .ToList();

                for (int i = maxBackups; i < files.Count; i++)
                {
                    try
                    {
                        files[i].Delete();
                        _logger.Debug($"Deleted old backup: {files[i].Name}");
                    }
                    catch (Exception ex)
                    {
                        _logger.Warn($"Failed to delete old backup {files[i].Name}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Warn($"Error rotating backups for {seriesName}: {ex.Message}");
            }
        }

        public bool RestoreEpisodeMarkerFromBackup(Episode episode, string backupFolder)
        {
            try
            {
                var series = episode.Series;
                if (series == null || string.IsNullOrWhiteSpace(backupFolder))
                    return false;

                var backupFile = FindLatestSeriesBackupFile(series.Name, backupFolder);
                if (backupFile == null)
                {
                    _logger.Debug($"No backup file found for series '{series.Name}' in {backupFolder}");
                    return false;
                }

                var json = File.ReadAllText(backupFile);
                var backup = JsonSerializer.Deserialize<CreditsBackup>(json);
                if (backup?.Entries == null || backup.Entries.Count == 0)
                    return false;

                CreditsBackupEntry? entry = null;

                if (entry == null && episode.ProviderIds?.TryGetValue("Tvdb", out var epTvdb) == true && !string.IsNullOrEmpty(epTvdb))
                    entry = backup.Entries.FirstOrDefault(e => e.TvdbEpisodeId == epTvdb);

                if (entry == null && episode.ProviderIds?.TryGetValue("Tmdb", out var epTmdb) == true && !string.IsNullOrEmpty(epTmdb))
                    entry = backup.Entries.FirstOrDefault(e => e.TmdbEpisodeId == epTmdb);

                if (entry == null && !string.IsNullOrEmpty(episode.Path))
                    entry = backup.Entries.FirstOrDefault(e => string.Equals(e.FilePath, episode.Path, StringComparison.OrdinalIgnoreCase));

                if (entry == null && episode.ParentIndexNumber.HasValue && episode.IndexNumber.HasValue)
                    entry = backup.Entries.FirstOrDefault(e => e.SeasonNumber == episode.ParentIndexNumber.Value && e.EpisodeNumber == episode.IndexNumber.Value);

                if (entry == null)
                {
                    _logger.Debug($"Episode '{episode.Name}' not found in backup for series '{series.Name}'");
                    return false;
                }

                if (!episode.RunTimeTicks.HasValue || entry.CreditsStartTicks >= episode.RunTimeTicks.Value)
                {
                    _logger.Warn($"Backed-up timestamp for '{episode.Name}' exceeds episode duration — skipping restore");
                    return false;
                }

                var chapters = _itemRepository.GetChapters(episode)?.ToList() ?? new List<ChapterInfo>();
                chapters.RemoveAll(c => GetMarkerType(c) == "CreditsStart");

                var creditsChapter = new ChapterInfo
                {
                    Name = "Credits Start",
                    StartPositionTicks = entry.CreditsStartTicks
                };

                var markerTypeProp = creditsChapter.GetType().GetProperty("MarkerType");
                if (markerTypeProp != null && markerTypeProp.CanWrite)
                {
                    markerTypeProp.SetValue(creditsChapter, CreditsMarkerType.CreditsStart);
                    chapters.Add(creditsChapter);
                    chapters = chapters.OrderBy(c => c.StartPositionTicks).ToList();
                    _itemRepository.SaveChapters(episode.InternalId, chapters);
                    _logger.Info($"Restored credits marker for '{series.Name} S{episode.ParentIndexNumber:D2}E{episode.IndexNumber:D2}' from backup");
                    return true;
                }

                _logger.Warn($"Could not set MarkerType on ChapterInfo for '{episode.Name}'");
                return false;
            }
            catch (Exception ex)
            {
                _logger.ErrorException($"Error restoring marker from backup for episode '{episode.Name}'", ex);
                return false;
            }
        }

        public Task<CreditsBackupResult> ExportCreditsMarkers(
            List<string>? libraryIds,
            List<string>? seriesIds,
            CancellationToken cancellationToken = default,
            string? backupFolder = null,
            int maxBackupsPerSeries = 10)
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

                string json;

                if (!string.IsNullOrWhiteSpace(backupFolder))
                {
                    if (!Directory.Exists(backupFolder))
                        Directory.CreateDirectory(backupFolder);

                    var bySeriesName = backupData.GroupBy(e => e.SeriesName).ToList();
                    var timestamp = DateTime.Now.ToString("yyyy-MM-dd_HHmmss");

                    foreach (var seriesGroup in bySeriesName)
                    {
                        var seriesEntries = seriesGroup.ToList();
                        var seriesBackup = new CreditsBackup
                        {
                            Version = "1.0",
                            BackupDate = DateTime.UtcNow,
                            TotalEpisodes = seriesEntries.Count,
                            EpisodesWithCredits = seriesEntries.Count,
                            Entries = seriesEntries
                        };

                        var seriesJson = JsonSerializer.Serialize(seriesBackup, jsonOptions);
                        var safeName = SanitizeFileName(seriesGroup.Key);
                        var filePath = Path.Combine(backupFolder, $"{safeName}_{timestamp}.json");
                        File.WriteAllText(filePath, seriesJson);
                        _logger.Debug($"Saved series backup: {filePath}");

                        RotateSeriesBackups(backupFolder, seriesGroup.Key, maxBackupsPerSeries);
                    }

                    _logger.Info($"Saved {bySeriesName.Count} per-series backup files to: {backupFolder}");

                    json = JsonSerializer.Serialize(backup, jsonOptions);
                }
                else
                {
                    json = JsonSerializer.Serialize(backup, jsonOptions);
                }

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
                
                backupData.Clear();
                GC.Collect(1, GCCollectionMode.Optimized, false);

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
                const int MaxJsonSizeBytes = 50 * 1024 * 1024;
                if (jsonData.Length > MaxJsonSizeBytes)
                {
                    result.Success = false;
                    result.Message = $"Import file too large. Maximum size is {MaxJsonSizeBytes / (1024 * 1024)} MB.";
                    _logger.Warn($"Import rejected: JSON data exceeds maximum size ({jsonData.Length} bytes)");
                    return Task.FromResult(result);
                }

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
                
                episodesByTvdbId.Clear();
                episodesByTmdbId.Clear();
                episodesByImdbId.Clear();
                episodesBySeriesAndNumber.Clear();
                episodesByPath.Clear();
                allEpisodes.Clear();
                GC.Collect(1, GCCollectionMode.Optimized, false);

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
        public long? LastDetectedFileSize { get; set; }
        public DateTime? LastDetectedModified { get; set; }
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
