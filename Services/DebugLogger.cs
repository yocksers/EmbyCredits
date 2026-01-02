using MediaBrowser.Model.Logging;
using System;
using System.Text;
using System.Threading.Tasks;

namespace EmbyCredits.Services
{
    public class DebugLogger
    {
        private readonly ILogger _logger;
        private readonly PluginConfiguration _configuration;
        private StringBuilder? _debugLog;
        private bool _isDebugMode;
        private const int MaxDebugLogSize = 10 * 1024 * 1024;

        public DebugLogger(ILogger logger, PluginConfiguration configuration)
        {
            _logger = logger;
            _configuration = configuration;
        }

        public bool IsDebugMode => _isDebugMode;

        public void StartDebugMode()
        {
            _debugLog = new StringBuilder();
            _debugLog.AppendLine("=".PadRight(80, '='));
            _debugLog.AppendLine($"EMBY CREDITS DETECTION - DEBUG LOG");
            _debugLog.AppendLine($"Started: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            _debugLog.AppendLine("=".PadRight(80, '='));
            _debugLog.AppendLine();

            if (_configuration != null)
            {
                _debugLog.AppendLine("CONFIGURATION:");
                _debugLog.AppendLine($"  EnableAutoDetection: {_configuration.EnableAutoDetection}");
                _debugLog.AppendLine($"  EnableOcrDetection: {_configuration.EnableOcrDetection}");
                _debugLog.AppendLine($"  OcrEndpoint: {_configuration.OcrEndpoint}");
                _debugLog.AppendLine($"  OcrEnableCharacterDensityDetection: {_configuration.OcrEnableCharacterDensityDetection}");
                _debugLog.AppendLine($"  OcrCharacterDensityPrimaryMethod: {_configuration.OcrCharacterDensityPrimaryMethod}");
                _debugLog.AppendLine($"  OcrCharacterDensityThreshold: {_configuration.OcrCharacterDensityThreshold}");
                _debugLog.AppendLine($"  OcrCharacterDensityConsecutiveFrames: {_configuration.OcrCharacterDensityConsecutiveFrames}");
                _debugLog.AppendLine($"  UseEpisodeComparison: {_configuration.UseEpisodeComparison}");
                _debugLog.AppendLine($"  MinimumEpisodesToCompare: {_configuration.MinimumEpisodesToCompare}");
                _debugLog.AppendLine();
            }

            _isDebugMode = true;
            _logger.Info("Debug mode started - all operations will be logged");
        }

        public void LogInfo(string message)
        {
            _logger.Info(message);
            if (_isDebugMode && _debugLog != null)
            {
                TruncateIfNeeded();
                _debugLog.AppendLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [INFO] {message}");
            }
        }

        public void LogDebug(string message)
        {
            if (_configuration?.EnableDetailedLogging == true)
                _logger.Debug(message);
            if (_isDebugMode && _debugLog != null)
            {
                TruncateIfNeeded();
                _debugLog.AppendLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [DEBUG] {message}");
            }
        }

        public void LogWarn(string message)
        {
            _logger.Warn(message);
            if (_isDebugMode && _debugLog != null)
            {
                TruncateIfNeeded();
                _debugLog.AppendLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [WARN] {message}");
            }
        }

        public void LogError(string message, Exception? ex = null)
        {
            if (ex != null)
                _logger.ErrorException(message, ex);
            else
                _logger.Error(message);

            if (_isDebugMode && _debugLog != null)
            {
                TruncateIfNeeded();
                _debugLog.AppendLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [ERROR] {message}");
                if (ex != null)
                {
                    _debugLog.AppendLine($"Exception: {ex.GetType().Name}: {ex.Message}");
                    _debugLog.AppendLine($"StackTrace: {ex.StackTrace}");
                }
            }
        }

        public void LogToDebug(string level, string message)
        {
            if (_isDebugMode && _debugLog != null)
            {
                TruncateIfNeeded();
                _debugLog.AppendLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{level}] {message}");
            }
        }

        public string GetDebugLog()
        {
            var log = _debugLog?.ToString() ?? "No debug log available";
            Cleanup();
            return log;
        }

        public void Cleanup()
        {
            if (_debugLog != null)
            {
                _debugLog.Clear();
                _debugLog = null;
            }
            _isDebugMode = false;
        }

        public void ScheduleDebugLogCleanup()
        {
            Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromMinutes(5));
                if (_isDebugMode)
                {
                    _logger.Info("Debug log auto-cleanup: Debug log was not downloaded within 5 minutes, clearing from memory");
                    Cleanup();
                }
            });
        }

        private void TruncateIfNeeded()
        {
            if (_debugLog != null && _debugLog.Length > MaxDebugLogSize)
            {

                var keepSize = (int)(MaxDebugLogSize * 0.8);
                var removeSize = _debugLog.Length - keepSize;
                _debugLog.Remove(0, removeSize);
                _debugLog.Insert(0, $"[TRUNCATED: Removed {removeSize} characters to prevent memory growth]\n\n");
                _logger.Info($"Debug log truncated to prevent memory growth (was {_debugLog.Length + removeSize} bytes)");
            }
        }
    }
}
