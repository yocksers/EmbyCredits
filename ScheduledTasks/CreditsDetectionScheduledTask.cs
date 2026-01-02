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
                    var library = _libraryManager.GetItemById(libraryId) as Folder;
                    if (library != null)
                    {
                        librariesToProcess.Add(library);
                    }
                    else
                    {
                        _logger.Warn($"Library not found: {libraryId}");
                    }
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

                    var episodes = _libraryManager.GetItemList(query).OfType<Episode>().ToList();
                    allEpisodes.AddRange(episodes);

                    _logger.Info($"Found {episodes.Count} episodes in {library.Name}");
                }
                catch (Exception ex)
                {
                    _logger.ErrorException($"Error processing library {library.Name}", ex);
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

            foreach (var episode in episodesToProcess)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    _logger.Info("Cancellation requested, stopping credits detection");
                    break;
                }

                try
                {
                    await CreditsDetectionService.ProcessEpisode(episode);
                    processedCount++;

                    var percentComplete = (double)processedCount / episodesToProcess.Count * 100;
                    progress.Report(percentComplete);
                }
                catch (Exception ex)
                {
                    _logger.ErrorException($"Error processing episode {episode.Name}", ex);
                    Plugin.Progress.FailedItems++;
                }

                await Task.Delay(1000, cancellationToken);
            }

            Plugin.Progress.IsRunning = false;
            Plugin.Progress.EndTime = DateTime.Now;
            Plugin.Progress.CurrentItem = "Complete";

            _logger.Info($"Credits detection complete. Processed: {Plugin.Progress.SuccessfulItems}, Failed: {Plugin.Progress.FailedItems}");
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
                    if (markerType == "CreditsStart" || markerType == "Credits")
                        return true;

                    if (c.Name != null && c.Name.ToLowerInvariant().Contains("credit"))
                        return true;

                    return false;
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
