using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using EmbyCredits.Services;

namespace EmbyCredits
{
    public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages, IHasThumbImage, IServerEntryPoint
    {
        private readonly ILogger _logger;
        private readonly IApplicationPaths _appPaths;
        private readonly ILibraryManager _libraryManager;
        private readonly IItemRepository _itemRepository;
        private readonly IFfmpegManager _ffmpegManager;
        public static Plugin? Instance { get; private set; }
        public static CreditsDetectionProgress Progress { get; } = new CreditsDetectionProgress();
        public static CreditsBackupService? CreditsBackupService { get; private set; }
        public static ChapterMarkerService? ChapterMarkerService { get; private set; }

        public override string Name => "Credits Detector";
        public override string Description => "Automatically detects end credits in TV shows and saves timestamps to files.";
        public override Guid Id => Guid.Parse("b1a65a73-a620-432a-9f5b-285038031c26");

        public Plugin(IApplicationPaths appPaths, IXmlSerializer xmlSerializer, ILogManager logManager, ILibraryManager libraryManager, IItemRepository itemRepository, IFfmpegManager ffmpegManager)
            : base(appPaths, xmlSerializer)
        {
            Instance = this;
            _logger = logManager.GetLogger(GetType().Name);
            _appPaths = appPaths;
            _libraryManager = libraryManager;
            _itemRepository = itemRepository;
            _ffmpegManager = ffmpegManager;
        }

        public override void SaveConfiguration()
        {
            base.SaveConfiguration();
            Services.Utilities.FFmpegHelper.SetCustomTempPath(Configuration.TempFolderPath);
            CreditsDetectionService.UpdateConfiguration(Configuration);
            _logger.Info("Credits Detector configuration updated");
        }

        public IEnumerable<PluginPageInfo> GetPages()
        {
            return new[]
            {
                new PluginPageInfo
                {
                    Name = "CreditsDetectorConfiguration",
                    EmbeddedResourcePath = "EmbyCredits.Configuration.CreditsDetectorConfiguration.html",
                },
                new PluginPageInfo
                {
                    Name = "CreditsDetectorConfigurationjs",
                    EmbeddedResourcePath = "EmbyCredits.Configuration.CreditsDetectorConfiguration.js"
                },
                new PluginPageInfo
                {
                    Name = "CreditsDetectorConfigurationUtils",
                    EmbeddedResourcePath = "EmbyCredits.Configuration.CreditsDetectorConfiguration.Utils.js"
                },
                new PluginPageInfo
                {
                    Name = "CreditsDetectorConfigurationLoader",
                    EmbeddedResourcePath = "EmbyCredits.Configuration.CreditsDetectorConfiguration.Loader.js"
                },
                new PluginPageInfo
                {
                    Name = "CreditsDetectorConfigurationEvents",
                    EmbeddedResourcePath = "EmbyCredits.Configuration.CreditsDetectorConfiguration.Events.js"
                },
                new PluginPageInfo
                {
                    Name = "CreditsDetectorConfigurationDataManager",
                    EmbeddedResourcePath = "EmbyCredits.Configuration.CreditsDetectorConfiguration.DataManager.js"
                },
                new PluginPageInfo
                {
                    Name = "CreditsDetectorConfigurationSeriesManager",
                    EmbeddedResourcePath = "EmbyCredits.Configuration.CreditsDetectorConfiguration.SeriesManager.js"
                },
                new PluginPageInfo
                {
                    Name = "CreditsDetectorConfigurationProcessingActions",
                    EmbeddedResourcePath = "EmbyCredits.Configuration.CreditsDetectorConfiguration.ProcessingActions.js"
                },
                new PluginPageInfo
                {
                    Name = "CreditsDetectorConfigurationProgressMonitor",
                    EmbeddedResourcePath = "EmbyCredits.Configuration.CreditsDetectorConfiguration.ProgressMonitor.js"
                },
                new PluginPageInfo
                {
                    Name = "CreditsDetectorConfigurationMarkersManager",
                    EmbeddedResourcePath = "EmbyCredits.Configuration.CreditsDetectorConfiguration.MarkersManager.js"
                },
                new PluginPageInfo
                {
                    Name = "CreditsDetectorConfigurationBackupManager",
                    EmbeddedResourcePath = "EmbyCredits.Configuration.CreditsDetectorConfiguration.BackupManager.js"
                },
                new PluginPageInfo
                {
                    Name = "CreditsDetectorConfiguration.Settings",
                    EmbeddedResourcePath = "EmbyCredits.Configuration.CreditsDetectorConfiguration.Settings.html"
                },
                new PluginPageInfo
                {
                    Name = "CreditsDetectorConfiguration.Actions",
                    EmbeddedResourcePath = "EmbyCredits.Configuration.CreditsDetectorConfiguration.Actions.html"
                },
                new PluginPageInfo
                {
                    Name = "CreditsDetectorConfiguration.Guide",
                    EmbeddedResourcePath = "EmbyCredits.Configuration.CreditsDetectorConfiguration.Guide.html"
                },
                new PluginPageInfo
                {
                    Name = "CreditsDetectorConfiguration.API",
                    EmbeddedResourcePath = "EmbyCredits.Configuration.CreditsDetectorConfiguration.API.html"
                }
            };
        }

        public void Run()
        {
            CreditsDetectionService.SetLibraryManager(_libraryManager);
            CreditsDetectionService.SetItemRepository(_itemRepository);
            CreditsDetectionService.SetFfmpegManager(_ffmpegManager);
            Services.Utilities.FFmpegHelper.SetCustomTempPath(Configuration.TempFolderPath);

            CreditsBackupService = new CreditsBackupService(_logger, _libraryManager, _itemRepository);
            ChapterMarkerService = new ChapterMarkerService(_logger, _itemRepository);

            var cleanedCount = Services.Utilities.FFmpegHelper.CleanupOrphanedTempDirectories();
            if (cleanedCount > 0)
            {
                _logger.Info($"Cleaned up {cleanedCount} orphaned OCR temp directories from previous runs");
            }

            if (string.IsNullOrWhiteSpace(Configuration.TempFolderPath) && 
                System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Linux))
            {
                _logger.Warn("⚠️ RUNNING ON LINUX WITHOUT CUSTOM TEMP FOLDER! If using Docker/Unraid, your image may fill up with temp files. Configure 'Custom Temp Folder Path' in plugin settings to point to a mapped volume (e.g., /mnt/user/appdata/emby-temp or /tmp).");
            }

            CreditsDetectionService.Start(_logger, _appPaths, Configuration);
        }

        public void Dispose()
        {
            CreditsDetectionService.Stop();

            try
            {
                var cleanedCount = Services.Utilities.FFmpegHelper.CleanupOrphanedTempDirectories();
                if (cleanedCount > 0)
                {
                    _logger.Info($"Final cleanup: removed {cleanedCount} temp directories");
                }
            }
            catch (Exception ex)
            {
                _logger.Warn($"Error during final temp directory cleanup: {ex.Message}");
            }
        }

        public Stream GetThumbImage()
        {
            var assembly = typeof(Plugin).GetTypeInfo().Assembly;
            var resourceName = typeof(Plugin).Namespace + ".Images.logo.jpg";
            return assembly.GetManifestResourceStream(resourceName) ?? Stream.Null;
        }

        public ImageFormat ThumbImageFormat => ImageFormat.Jpg;
    }
}
