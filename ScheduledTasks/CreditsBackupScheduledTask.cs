using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Tasks;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using EmbyCredits.Services;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Common.Configuration;

namespace EmbyCredits.ScheduledTasks
{
    public class CreditsBackupScheduledTask : IScheduledTask
    {
        private readonly ILogger _logger;
        private readonly ILibraryManager _libraryManager;
        private readonly IItemRepository _itemRepository;
        private readonly IApplicationPaths _appPaths;

        public string Name => "Backup Credits Markers";
        public string Description => "Creates a backup of all credits markers to a JSON file";
        public string Category => "Library";
        public string Key => "CreditsBackup";

        public CreditsBackupScheduledTask(
            ILogManager logManager,
            ILibraryManager libraryManager,
            IItemRepository itemRepository,
            IApplicationPaths appPaths)
        {
            _logger = logManager.GetLogger(GetType().Name);
            _libraryManager = libraryManager;
            _itemRepository = itemRepository;
            _appPaths = appPaths;
        }

        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            if (Plugin.Instance == null)
            {
                _logger.Error("Plugin instance not available");
                return;
            }

            try
            {
                progress.Report(0);
                _logger.Info("Starting scheduled credits markers backup");

                var backupService = new CreditsBackupService(_logger, _libraryManager, _itemRepository);
                
                var config = Plugin.Instance.Configuration;

                var backupDir = !string.IsNullOrWhiteSpace(config.BackupFolderPath)
                    ? config.BackupFolderPath
                    : Path.Combine(_appPaths.DataPath, "plugins", "EmbyCredits", "Backups");

                if (!Directory.Exists(backupDir))
                    Directory.CreateDirectory(backupDir);

                progress.Report(10);

                var maxBackups = config.MaxScheduledBackups > 0 ? config.MaxScheduledBackups : 10;

                var result = await backupService.ExportCreditsMarkers(
                    null,
                    null,
                    cancellationToken,
                    backupDir,
                    maxBackups);

                progress.Report(100);

                if (result.Success)
                {
                    _logger.Info(result.Message);
                }
                else
                {
                    _logger.Error($"Backup failed: {result.Message}");
                }
            }
            catch (Exception ex)
            {
                _logger.ErrorException("Error during scheduled backup", ex);
                throw;
            }
        }

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            return new[]
            {
                new TaskTriggerInfo
                {
                    Type = TaskTriggerInfo.TriggerWeekly,
                    DayOfWeek = DayOfWeek.Sunday,
                    TimeOfDayTicks = TimeSpan.FromHours(3).Ticks
                }
            };
        }
    }
}
