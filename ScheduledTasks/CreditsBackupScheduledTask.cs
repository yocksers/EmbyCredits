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
                var libraryIds = config.LibraryIds;

                progress.Report(10);

                var result = await backupService.ExportCreditsMarkers(
                    libraryIds?.Length > 0 ? new List<string>(libraryIds) : null,
                    null,
                    cancellationToken);

                progress.Report(80);

                if (result.Success && !string.IsNullOrEmpty(result.JsonData))
                {
                    var backupDir = !string.IsNullOrWhiteSpace(config.BackupFolderPath)
                        ? config.BackupFolderPath
                        : Path.Combine(_appPaths.DataPath, "plugins", "EmbyCredits", "Backups");
                    
                    if (!Directory.Exists(backupDir))
                    {
                        Directory.CreateDirectory(backupDir);
                    }

                    var timestamp = DateTime.Now.ToString("yyyy-MM-dd_HHmmss");
                    var backupFilePath = Path.Combine(backupDir, $"credits_backup_{timestamp}.json");
                    
                    await File.WriteAllTextAsync(backupFilePath, result.JsonData, cancellationToken);
                    
                    _logger.Info($"Backup saved to: {backupFilePath}");
                    _logger.Info(result.Message);

                    var maxBackups = config.MaxScheduledBackups > 0 ? config.MaxScheduledBackups : 10;
                    CleanupOldBackups(backupDir, maxBackups);
                }
                else
                {
                    _logger.Error($"Backup failed: {result.Message}");
                }

                progress.Report(100);
            }
            catch (Exception ex)
            {
                _logger.ErrorException("Error during scheduled backup", ex);
                throw;
            }
        }

        private void CleanupOldBackups(string backupDir, int maxBackups)
        {
            try
            {
                var files = Directory.GetFiles(backupDir, "credits_backup_*.json");
                
                if (files.Length <= maxBackups)
                    return;

                var fileInfos = new List<FileInfo>();
                foreach (var file in files)
                {
                    fileInfos.Add(new FileInfo(file));
                }

                fileInfos.Sort((a, b) => b.CreationTime.CompareTo(a.CreationTime));

                for (int i = maxBackups; i < fileInfos.Count; i++)
                {
                    try
                    {
                        fileInfos[i].Delete();
                        _logger.Info($"Deleted old backup: {fileInfos[i].Name}");
                    }
                    catch (Exception ex)
                    {
                        _logger.Warn($"Failed to delete old backup {fileInfos[i].Name}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.ErrorException("Error cleaning up old backups", ex);
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
