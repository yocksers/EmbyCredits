using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Tasks;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EmbyCredits.Services;

namespace EmbyCredits.ScheduledTasks
{
    public class CreditsDetectionScheduledTask : IScheduledTask
    {
        private readonly ILogger _logger;
        private readonly ILibraryManager _libraryManager;
        private readonly IApplicationPaths _appPaths;
        private readonly IItemRepository _itemRepository;

        public string Name => "Detect Credits in TV Shows";
        public string Description => "Scans TV shows in selected libraries and detects end credits timestamps";
        public string Category => "Library";
        public string Key => "CreditsDetection";

        public CreditsDetectionScheduledTask(
            ILogManager logManager,
            ILibraryManager libraryManager,
            IApplicationPaths appPaths,
            IItemRepository itemRepository)
        {
            _logger = logManager.GetLogger(GetType().Name);
            _libraryManager = libraryManager;
            _appPaths = appPaths;
            _itemRepository = itemRepository;
        }

        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            if (Plugin.Instance == null)
            {
                _logger.Error("Plugin instance not available");
                return;
            }

            var config = Plugin.Instance.Configuration;
            var libraryIds = config.LibraryIds ?? Array.Empty<string>();

            CreditsDetectionService.SetLibraryManager(_libraryManager);
            CreditsDetectionService.SetItemRepository(_itemRepository);

            var allEpisodes = new List<Episode>();

            if (config.OnlyProcessNewEpisodes && Plugin.PendingEpisodesService != null)
            {
                // Pending-queue mode: only process episodes that have been added since
                // the last detection run, rather than scanning the whole library.
                var pendingIds = Plugin.PendingEpisodesService.GetPendingEpisodeIds();
                _logger.Info($"OnlyProcessNewEpisodes: {pendingIds.Count} pending episode(s) in queue");

                if (pendingIds.Count == 0)
                {
                    _logger.Info("No pending episodes to process");
                    return;
                }

                // Resolve IDs and group by series+season
                var pendingBySeason = new Dictionary<(string seriesId, int season), List<Episode>>();
                foreach (var id in pendingIds)
                {
                    if (!Guid.TryParse(id, out var guid)) continue;
                    var ep = _libraryManager.GetItemById(guid) as Episode;
                    if (ep == null || ep.ParentIndexNumber == null || ep.ParentIndexNumber == 0 || ep.Series == null)
                        continue;

                    var key = (ep.Series.Id.ToString(), ep.ParentIndexNumber.Value);
                    if (!pendingBySeason.ContainsKey(key))
                        pendingBySeason[key] = new List<Episode>();
                    pendingBySeason[key].Add(ep);
                }

                if (pendingBySeason.Count == 0)
                {
                    _logger.Info("No resolvable pending episodes found (items may have been removed from library)");
                    // Clear stale IDs
                    foreach (var id in pendingIds)
                        Plugin.PendingEpisodesService.MarkProcessed(id);
                    return;
                }

                // For each season with pending episodes, fetch the full season so that
                // Chromaprint batch detection has access to all episodes it needs.
                // For non-Chromaprint series the extra episodes are filtered out below
                // by ScheduledTaskOnlyProcessMissing, so there's no wasted work.
                foreach (var kvp in pendingBySeason)
                {
                    var (seriesId, seasonNumber) = kvp.Key;
                    var firstPending = kvp.Value.First();
                    var series = firstPending.Series!;

                    var seasonEps = _libraryManager.GetItemList(new InternalItemsQuery
                    {
                        IncludeItemTypes = new[] { "Episode" },
                        IsVirtualItem = false,
                        HasPath = true,
                        AncestorIds = new[] { series.InternalId }
                    }).OfType<Episode>()
                      .Where(e => e.ParentIndexNumber == seasonNumber && e.ParentIndexNumber != 0)
                      .ToList();

                    allEpisodes.AddRange(seasonEps);
                    _logger.Info($"Pending queue: queued {seasonEps.Count} episode(s) from {series.Name} Season {seasonNumber} (includes full season for Chromaprint)");
                }

                // Deduplicate in case seasons overlapped
                allEpisodes = allEpisodes.GroupBy(e => e.Id).Select(g => g.First()).ToList();
            }
            else
            {
                // Full library scan mode (original behaviour)
                List<Folder> librariesToProcess;

                if (libraryIds.Length == 0)
                {
                    _logger.Info("No specific libraries configured, processing all TV Show and Mixed libraries");

                    var allLibraries = _libraryManager.GetItemList(new InternalItemsQuery
                    {
                        IncludeItemTypes = new[] { "CollectionFolder" }
                    }).ToList();

                    librariesToProcess = allLibraries
                        .Where(lib =>
                        {
                            var collectionType = lib.GetType().GetProperty("CollectionType")?.GetValue(lib) as string;
                            return collectionType == "tvshows" || collectionType == "mixed" || string.IsNullOrEmpty(collectionType);
                        })
                        .OfType<Folder>()
                        .ToList();
                }
                else
                {
                    librariesToProcess = new List<Folder>();
                    foreach (var libraryId in libraryIds)
                    {
                        if (!Guid.TryParse(libraryId, out var libraryGuid))
                        {
                            _logger.Warn($"Invalid library ID (not a valid Guid): {libraryId}");
                            continue;
                        }
                        var library = _libraryManager.GetItemById(libraryGuid) as Folder;
                        if (library != null)
                            librariesToProcess.Add(library);
                        else
                            _logger.Warn($"Library not found: {libraryId}");
                    }
                }

                _logger.Info($"Starting scheduled credits detection for {librariesToProcess.Count} libraries");

                foreach (var library in librariesToProcess)
                {
                    if (cancellationToken.IsCancellationRequested)
                        break;

                    try
                    {
                        _logger.Info($"Scanning library: {library.Name}");

                        var query = new InternalItemsQuery
                        {
                            IncludeItemTypes = new[] { "Episode" },
                            IsVirtualItem = false,
                            HasPath = true,
                            Recursive = true,
                            Parent = library
                        };

                        var allLibraryEpisodes = _libraryManager.GetItemList(query).OfType<Episode>().ToList();
                        var episodes = allLibraryEpisodes.Where(e => e.ParentIndexNumber != null && e.ParentIndexNumber != 0).ToList();
                        var specialCount = allLibraryEpisodes.Count - episodes.Count;
                        allEpisodes.AddRange(episodes);

                        _logger.Info($"Found {episodes.Count} episodes in {library.Name} (excluded {specialCount} specials)");
                    }
                    catch (Exception ex)
                    {
                        _logger.ErrorException($"Error processing library {library.Name}", ex);
                    }
                }
            }

            if (allEpisodes.Count == 0)
            {
                _logger.Info("No episodes found to process");
                return;
            }

            _logger.Info($"Found {allEpisodes.Count} total episodes");

            var episodesToProcess = new List<Episode>();
            var skipCount = 0;

            if (config.ScheduledTaskOnlyProcessMissing)
            {
                foreach (var episode in allEpisodes)
                {
                    if (cancellationToken.IsCancellationRequested)
                        break;

                    if (!HasCreditsMarker(episode))
                    {
                        episodesToProcess.Add(episode);
                    }
                    else
                    {
                        skipCount++;
                    }
                }

                _logger.Info($"Processing {episodesToProcess.Count} episodes (skipping {skipCount} episodes with existing credits)");
            }
            else
            {
                episodesToProcess = allEpisodes;
                _logger.Info($"Processing all {episodesToProcess.Count} episodes (reprocess mode enabled)");
            }

            if (episodesToProcess.Count == 0)
            {
                _logger.Info("All episodes already have credits or were previously processed, nothing to process");
                return;
            }

            Plugin.Progress.Reset();
            Plugin.Progress.TotalItems = episodesToProcess.Count;
            Plugin.Progress.IsRunning = true;
            Plugin.Progress.StartTime = DateTime.Now;

            var processedCount = 0;
            var failedEpisodeNames = new List<string>();
            var successfulSeriesNames = new HashSet<string>();

            // Group episodes by series and season for batch processing
            var episodesBySeason = episodesToProcess
                .Where(e => e.Series != null && e.ParentIndexNumber.HasValue)
                .GroupBy(e => new { SeriesId = e.Series!.Id.ToString(), SeasonNumber = e.ParentIndexNumber!.Value })
                .ToList();

            _logger.Info($"Grouped {episodesToProcess.Count} episodes into {episodesBySeason.Count} seasons for batch processing");

            foreach (var seasonGroup in episodesBySeason)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    _logger.Info("Cancellation requested, stopping credits detection");
                    break;
                }

                var seasonEpisodes = seasonGroup.OrderBy(e => e.IndexNumber).ToList();
                var firstEpisode = seasonEpisodes.First();
                var seriesName = firstEpisode.Series?.Name ?? "Unknown Series";
                
                _logger.Info($"Processing season batch: {seriesName} Season {seasonGroup.Key.SeasonNumber} ({seasonEpisodes.Count} episodes)");

                try
                {
                    await CreditsDetectionService.ProcessSeasonBatch(seasonEpisodes, cancellationToken);
                    
                    processedCount += seasonEpisodes.Count;
                    successfulSeriesNames.Add(seriesName);

                    var percentComplete = (double)processedCount / episodesToProcess.Count * 100;
                    progress.Report(percentComplete);
                }
                catch (Exception ex)
                {
                    _logger.ErrorException($"Error processing season batch {seriesName} S{seasonGroup.Key.SeasonNumber}", ex);
                    Plugin.Progress.FailedItems += seasonEpisodes.Count;
                    
                    foreach (var episode in seasonEpisodes)
                    {
                        var episodeLabel = $"{seriesName} S{episode.ParentIndexNumber:00}E{episode.IndexNumber:00} - {episode.Name}";
                        failedEpisodeNames.Add(episodeLabel);
                    }
                }

                await Task.Delay(1000, cancellationToken);
            }

            Plugin.Progress.IsRunning = false;
            Plugin.Progress.EndTime = DateTime.Now;
            Plugin.Progress.CurrentItem = "Complete";
            
            EmbyCredits.Services.DetectionMethods.OcrDetection.ClearAllCache();
            EmbyCredits.Services.DetectionMethods.ChromaprintDetection.ClearAllCache();
            EmbyCredits.Services.DetectionMethods.BlackFrameDetection.ClearAllCache();

            _logger.Info($"Credits detection complete. Processed: {Plugin.Progress.SuccessfulItems}, Failed: {Plugin.Progress.FailedItems}");

            var duration = DateTime.Now - Plugin.Progress.StartTime;
            SendCompletionNotification(Plugin.Progress.SuccessfulItems, Plugin.Progress.FailedItems, episodesToProcess.Count, failedEpisodeNames, successfulSeriesNames.ToList(), duration ?? TimeSpan.Zero);
        }

        private void SendCompletionNotification(int successCount, int failedCount, int totalProcessed, List<string> failedEpisodes, List<string> successfulSeries, TimeSpan duration)
        {
            try
            {
                if (Plugin.Instance == null)
                {
                    _logger.Debug("Plugin instance not available");
                    return;
                }

                var notificationManager = Plugin.Instance.GetNotificationManager();
                if (notificationManager == null)
                {
                    _logger.Debug("Notification manager not available");
                    return;
                }

                var config = Plugin.Instance.Configuration;
                var notificationService = new NotificationService(_logger, notificationManager, config);
                notificationService.SendScheduledTaskCompletionNotification(successCount, failedCount, totalProcessed, failedEpisodes, successfulSeries, duration);
            }
            catch (Exception ex)
            {
                _logger.ErrorException("Failed to send completion notification", ex);
            }
        }

        private bool HasCreditsMarker(Episode episode)
        {
            try
            {
                var chapters = _itemRepository.GetChapters(episode)?.ToList();
                if (chapters == null || chapters.Count == 0)
                    return false;

                return chapters.Any(c =>
                {
                    var markerType = GetMarkerType(c);
                    return markerType == "CreditsStart";
                });
            }
            catch (Exception ex)
            {
                _logger.ErrorException($"Error checking credits marker for {episode.Name}", ex);
                return false;
            }
        }

        private static string? GetMarkerType(MediaBrowser.Model.Entities.ChapterInfo chapter)
        {
            try
            {
                var markerTypeProperty = chapter.GetType().GetProperty("MarkerType");
                if (markerTypeProperty != null)
                {
                    var value = markerTypeProperty.GetValue(chapter);
                    if (value != null)
                    {
                        return value.ToString();
                    }
                }
            }
            catch
            {
            }

            return null;
        }

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {

            return new[]
            {
                new TaskTriggerInfo
                {
                    Type = TaskTriggerInfo.TriggerWeekly,
                    DayOfWeek = DayOfWeek.Sunday,
                    TimeOfDayTicks = TimeSpan.FromHours(2).Ticks
                }
            };
        }
    }
}
