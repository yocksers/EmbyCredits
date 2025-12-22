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
            var libraryIds = config.ScheduledTaskLibraryIds ?? Array.Empty<string>();

            if (libraryIds.Length == 0)
            {
                _logger.Info("No libraries configured for scheduled credits detection");
                return;
            }

            _logger.Info($"Starting scheduled credits detection for {libraryIds.Length} libraries");

            CreditsDetectionService.SetLibraryManager(_libraryManager);
            CreditsDetectionService.SetItemRepository(_itemRepository);

            var allEpisodes = new List<Episode>();

            foreach (var libraryId in libraryIds)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;

                try
                {
                    var library = _libraryManager.GetItemById(libraryId);
                    if (library == null)
                    {
                        _logger.Warn($"Library not found: {libraryId}");
                        continue;
                    }

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
                    _logger.ErrorException($"Error processing library {libraryId}", ex);
                }
            }

            if (allEpisodes.Count == 0)
            {
                _logger.Info("No episodes found to process");
                return;
            }

            _logger.Info($"Processing {allEpisodes.Count} total episodes");

            Plugin.Progress.Reset();
            Plugin.Progress.TotalItems = allEpisodes.Count;
            Plugin.Progress.IsRunning = true;
            Plugin.Progress.StartTime = DateTime.Now;

            var processedCount = 0;

            foreach (var episode in allEpisodes)
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

                    var percentComplete = (double)processedCount / allEpisodes.Count * 100;
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
