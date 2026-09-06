using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Notifications;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using EmbyCredits.Services;

namespace EmbyCredits
{
    public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages, IHasThumbImage, IServerEntryPoint, IDisposable
    {
        private readonly ILogger _logger;
        private readonly IApplicationPaths _appPaths;
        private readonly ILibraryManager _libraryManager;
        public ILibraryManager LibraryManager => _libraryManager;
        private readonly IItemRepository _itemRepository;
        private readonly IFfmpegManager _ffmpegManager;
        private readonly IMediaEncoder _mediaEncoder;
        private readonly INotificationManager _notificationManager;
        private bool _disposed = false;
        
        public static Plugin? Instance { get; private set; }
        public static CreditsDetectionProgress Progress { get; } = new CreditsDetectionProgress();
        public static CreditsDetectionProgress BackupExportProgress { get; } = new CreditsDetectionProgress();
        public static CreditsDetectionProgress BackupImportProgress { get; } = new CreditsDetectionProgress();
        public static CreditsDetectionProgress ClearZeroCreditsProgress { get; } = new CreditsDetectionProgress();
        public static CreditsBackupService? CreditsBackupService { get; private set; }
        public static ChapterMarkerService? ChapterMarkerService { get; private set; }
        public static TracerService? TracerService { get; private set; }
        public static PendingEpisodesService? PendingEpisodesService { get; private set; }
        public static INotificationManager? NotificationManager { get; private set; }

        public IApplicationPaths AppPaths => _appPaths;

        public override string Name => "Credits Detector";
        public override string Description => "Automatically detects end credits in TV shows and saves timestamps to files.";
        public override Guid Id => Guid.Parse("b1a65a73-a620-432a-9f5b-285038031c26");

        public Plugin(IApplicationPaths appPaths, IXmlSerializer xmlSerializer, ILogManager logManager, ILibraryManager libraryManager, IItemRepository itemRepository, IFfmpegManager ffmpegManager, IMediaEncoder mediaEncoder, INotificationManager notificationManager)
            : base(appPaths, xmlSerializer)
        {
            Instance = this;
            _logger = logManager.GetLogger(GetType().Name);
            _appPaths = appPaths;
            _libraryManager = libraryManager;
            _itemRepository = itemRepository;
            _ffmpegManager = ffmpegManager;
            _mediaEncoder = mediaEncoder;
            _notificationManager = notificationManager;
            NotificationManager = notificationManager;
        }
        
        public override void SaveConfiguration()
        {
            ValidateConfiguration();
            
            base.SaveConfiguration();
            Services.Utilities.FFmpegHelper.SetCustomTempPath(Configuration.TempFolderPath);
            CreditsDetectionService.UpdateConfiguration(Configuration);
            _logger.Info("Credits Detector configuration updated");
        }

        private void ValidateConfiguration()
        {
            if (!string.IsNullOrWhiteSpace(Configuration.OcrEndpoint))
            {
                if (!IsValidOcrEndpoint(Configuration.OcrEndpoint))
                {
                    _logger.Warn($"Invalid OCR endpoint: {Configuration.OcrEndpoint}. Resetting to default.");
                    Configuration.OcrEndpoint = "http://localhost:8884";
                }
            }

            Configuration.TempFolderPath = ValidatePath(Configuration.TempFolderPath, "TempFolderPath");
            Configuration.LogFileFolderPath = ValidatePath(Configuration.LogFileFolderPath, "LogFileFolderPath");
            Configuration.BackupFolderPath = ValidatePath(Configuration.BackupFolderPath, "BackupFolderPath");

            if (Configuration.OcrMaxAnalysisDuration < 0 || Configuration.OcrMaxAnalysisDuration > 7200)
            {
                _logger.Warn($"OcrMaxAnalysisDuration out of range: {Configuration.OcrMaxAnalysisDuration}. Setting to 600.");
                Configuration.OcrMaxAnalysisDuration = 600;
            }

            if (Configuration.OcrMaxFramesToProcess < 0 || Configuration.OcrMaxFramesToProcess > 10000)
            {
                _logger.Warn($"OcrMaxFramesToProcess out of range: {Configuration.OcrMaxFramesToProcess}. Setting to 0 (unlimited).");
                Configuration.OcrMaxFramesToProcess = 0;
            }
        }

        private bool IsValidOcrEndpoint(string endpoint)
        {
            if (string.IsNullOrWhiteSpace(endpoint))
                return false;

            try
            {
                var uri = new Uri(endpoint);
                
                if (uri.Scheme != "http" && uri.Scheme != "https")
                    return false;

                var host = uri.Host.ToLowerInvariant();
                if (host == "localhost" || host == "127.0.0.1" || host == "::1")
                    return true;

                if (System.Net.IPAddress.TryParse(host, out var ipAddress))
                {
                    var bytes = ipAddress.GetAddressBytes();
                    if (bytes.Length == 4)
                    {
                        if (bytes[0] == 10) return true;
                        if (bytes[0] == 192 && bytes[1] == 168) return true;
                        if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) return true;
                    }
                }

                _logger.Warn($"OCR endpoint is a public/non-local address: {endpoint}. Ensure this is intentional.");
                return true;
            }
            catch (Exception ex)
            {
                _logger.Error($"Invalid OCR endpoint URI: {endpoint} - {ex.Message}");
                return false;
            }
        }

        private string ValidatePath(string path, string fieldName)
        {
            if (string.IsNullOrWhiteSpace(path))
                return string.Empty;

            try
            {
                var fullPath = Path.GetFullPath(path);
                var normalizedPath = fullPath.ToLowerInvariant();

                string[] forbiddenPaths;
                if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
                        System.Runtime.InteropServices.OSPlatform.Windows))
                {
                    var sep = System.IO.Path.DirectorySeparatorChar;
                    forbiddenPaths = new[]
                    {
                        sep + "windows" + sep,
                        sep + "program files" + sep,
                        sep + "system32" + sep
                    };
                }
                else
                {
                    forbiddenPaths = new[]
                    {
                        "/etc/",
                        "/bin/",
                        "/sbin/",
                        "/usr/bin/",
                        "/usr/sbin/"
                    };
                }

                foreach (var forbidden in forbiddenPaths)
                {
                    if (normalizedPath.Contains(forbidden))
                    {
                        _logger.Warn($"{fieldName} points to system directory. Clearing value.");
                        return string.Empty;
                    }
                }

                return fullPath;
            }
            catch (Exception ex)
            {
                _logger.Error($"Invalid path for {fieldName}: {path} - {ex.Message}");
                return string.Empty;
            }
        }

        public IEnumerable<PluginPageInfo> GetPages()
        {
            return new[]
            {
                new PluginPageInfo
                {
                    Name = "CreditsDetectorConfiguration",
                    EmbeddedResourcePath = "EmbyCredits.Configuration.CreditsDetectorConfiguration.html",
                    EnableInMainMenu = true,
                    MenuIcon = "movie_filter",
                    DisplayName = "Credits Detector"
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
                    Name = "CreditsDetectorConfigurationVideoPlayer",
                    EmbeddedResourcePath = "EmbyCredits.Configuration.CreditsDetectorConfiguration.VideoPlayer.js"
                },
                new PluginPageInfo
                {
                    Name = "CreditsDetectorConfigurationBackupManager",
                    EmbeddedResourcePath = "EmbyCredits.Configuration.CreditsDetectorConfiguration.BackupManager.js"
                },
                new PluginPageInfo
                {
                    Name = "CreditsDetectorConfigurationRulesManager",
                    EmbeddedResourcePath = "EmbyCredits.Configuration.CreditsDetectorConfiguration.RulesManager.js"
                },
                new PluginPageInfo
                {
                    Name = "CreditsDetectorConfigurationAutoSkipManager",
                    EmbeddedResourcePath = "EmbyCredits.Configuration.CreditsDetectorConfiguration.AutoSkipManager.js"
                },
                new PluginPageInfo
                {
                    Name = "CreditsDetectorConfiguration.Settings",
                    EmbeddedResourcePath = "EmbyCredits.Configuration.CreditsDetectorConfiguration.Settings.html"
                },
                new PluginPageInfo
                {
                    Name = "CreditsDetectorConfiguration.Rules",
                    EmbeddedResourcePath = "EmbyCredits.Configuration.CreditsDetectorConfiguration.Rules.html"
                },
                new PluginPageInfo
                {
                    Name = "CreditsDetectorConfiguration.Notifications",
                    EmbeddedResourcePath = "EmbyCredits.Configuration.CreditsDetectorConfiguration.Notifications.html"
                },
                new PluginPageInfo
                {
                    Name = "CreditsDetectorConfiguration.OcrEnhancements",
                    EmbeddedResourcePath = "EmbyCredits.Configuration.CreditsDetectorConfiguration.OcrEnhancements.html"
                },
                new PluginPageInfo
                {
                    Name = "CreditsDetectorConfiguration.AutoSkip",
                    EmbeddedResourcePath = "EmbyCredits.Configuration.CreditsDetectorConfiguration.AutoSkip.html"
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
                },
                new PluginPageInfo
                {
                    Name = "CreditsDetectorConfigurationTracerManager",
                    EmbeddedResourcePath = "EmbyCredits.Configuration.CreditsDetectorConfiguration.TracerManager.js"
                },
                new PluginPageInfo
                {
                    Name = "CreditsDetectorConfigurationOcrStatusManager",
                    EmbeddedResourcePath = "EmbyCredits.Configuration.CreditsDetectorConfiguration.OcrStatusManager.js"
                },
                new PluginPageInfo
                {
                    Name = "CreditsDetectorConfiguration.Tracer",
                    EmbeddedResourcePath = "EmbyCredits.Configuration.CreditsDetectorConfiguration.Tracer.html"
                }
            };
        }

        public void Run()
        {
            CreditsDetectionService.SetLibraryManager(_libraryManager);
            CreditsDetectionService.SetItemRepository(_itemRepository);
            CreditsDetectionService.SetFfmpegManager(_ffmpegManager);
            
            Services.Utilities.FFmpegHelper.Initialize(_ffmpegManager, _mediaEncoder);
            Services.Utilities.FFmpegHelper.SetCustomTempPath(Configuration.TempFolderPath);
            Services.Utilities.FFmpegHelper.SetLogger(_logger);

            CreditsBackupService = new CreditsBackupService(_logger, _libraryManager, _itemRepository);
            ChapterMarkerService = new ChapterMarkerService(_logger, _itemRepository);
            TracerService = new TracerService(_logger, _appPaths.DataPath);
            PendingEpisodesService = new PendingEpisodesService(_logger, _appPaths.DataPath);

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
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (_disposed)
                return;

            if (disposing)
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

                _logger?.Info("Credits Detector plugin disposed");

                if (Services.Http.HttpClientPool.IsInitialized)
                    Services.Http.HttpClientPool.Instance.Dispose();
            }

            _disposed = true;
        }

        public INotificationManager GetNotificationManager()
        {
            return _notificationManager;
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
