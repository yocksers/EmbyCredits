using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Services;
using System;
using System.IO;
using System.Threading.Tasks;
using EmbyCredits.Services.Utilities;

namespace EmbyCredits.Services
{
    public partial class CreditsDetectionApiService : IService
    {
        private const int MaxImportBytes = 50 * 1024 * 1024;
        private readonly ILibraryManager _libraryManager;
        private readonly ILogger _logger;

        public CreditsDetectionApiService(ILibraryManager libraryManager, ILogManager logManager)
        {
            _libraryManager = libraryManager;
            _logger = logManager.GetLogger(GetType().Name);
        }

        private string FormatTime(double seconds) => ItemLookupHelper.FormatTime(seconds);

        private object ToStaticResult(MemoryStream stream)
        {
            try
            {
                var bytes = stream.ToArray();
                stream.Dispose();
                return new MemoryStream(bytes, false);
            }
            catch
            {
                stream?.Dispose();
                throw;
            }
        }

        private void TryAutoBackupEpisode(Episode episode, long creditsStartTicks)
        {
            if (Plugin.CreditsBackupService == null) return;
            var config = Plugin.Instance?.Configuration;
            if (config == null || !config.EnableAutoBackupAfterDetection || string.IsNullOrWhiteSpace(config.BackupFolderPath))
                return;
            var svc = Plugin.CreditsBackupService;
            var folder = config.BackupFolderPath;
            _ = Task.Run(async () =>
            {
                try
                {
                    await svc.UpsertEpisodeInSeriesBackup(episode, creditsStartTicks, folder).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger?.ErrorException("Auto-backup after marker save failed", ex);
                }
            });
        }
    }
}
