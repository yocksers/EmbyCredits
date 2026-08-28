using EmbyCredits.Services.Utilities;
using MediaBrowser.Model.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace EmbyCredits.Services.DetectionMethods
{

    public abstract class BaseDetectionMethod : IDetectionMethod
    {
        protected readonly ILogger Logger;
        protected readonly PluginConfiguration Configuration;
        private static readonly AsyncLocal<string?> _lastError = new AsyncLocal<string?>();
        private static readonly AsyncLocal<string?> _detectionReason = new AsyncLocal<string?>();
        private static readonly AsyncLocal<int?> _activeFfmpegProcessId = new AsyncLocal<int?>();
        private bool _disposed = false;

        // AsyncLocal so concurrently-processed episodes sharing this same detection method instance don't overwrite each other's state
        protected string LastError
        {
            get => _lastError.Value ?? string.Empty;
            set => _lastError.Value = value;
        }

        protected string DetectionReason
        {
            get => _detectionReason.Value ?? string.Empty;
            set => _detectionReason.Value = value;
        }

        protected int? ActiveFfmpegProcessId
        {
            get => _activeFfmpegProcessId.Value;
            set => _activeFfmpegProcessId.Value = value;
        }

        public abstract string MethodName { get; }
        public abstract double Confidence { get; }
        public abstract int Priority { get; }
        public abstract bool IsEnabled { get; }

        protected BaseDetectionMethod(ILogger logger, PluginConfiguration configuration)
        {
            Logger = logger ?? throw new ArgumentNullException(nameof(logger));
            Configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        }

        public abstract Task<double> DetectCredits(string videoPath, double duration, CancellationToken cancellationToken = default);

        public string GetLastError()
        {
            return LastError;
        }

        public string GetDetectionReason()
        {
            return DetectionReason;
        }

        protected void LogInfo(string message)
        {
            if (Configuration.EnableDetailedLogging)
                Logger.Info($"[{MethodName}] {message}");
        }

        protected void LogDebug(string message)
        {
            if (Configuration.EnableDetailedLogging)
                Logger.Debug($"[{MethodName}] {message}");
        }

        protected void LogWarn(string message)
        {

            Logger.Warn($"[{MethodName}] {message}");
        }

        protected void LogError(string message, Exception? ex = null)
        {
            if (ex != null)
                Logger.ErrorException($"[{MethodName}] {message}", ex);
            else
                Logger.Error($"[{MethodName}] {message}");
        }

        protected string FormatTime(double seconds) => ItemLookupHelper.FormatTime(seconds);

        protected void UpdateProgress(double progressPercentage, string? statusMessage = null)
        {
            if (Plugin.Instance != null)
            {
                progressPercentage = Math.Max(0, Math.Min(100, progressPercentage));
                Plugin.Progress.CurrentItemProgress = (int)progressPercentage;

                if (ActiveFfmpegProcessId.HasValue)
                {
                    FFmpegHelper.UpdateProcessProgress(ActiveFfmpegProcessId.Value, (int)progressPercentage);
                }

                if (!string.IsNullOrEmpty(statusMessage))
                {
                    var currentItem = Plugin.Progress.CurrentItem ?? "";
                    if (!currentItem.Contains(statusMessage))
                    {
                        var baseItem = currentItem.Split(new[] { " - OCR:", " - Processing" }, StringSplitOptions.None)[0];
                        Plugin.Progress.CurrentItem = $"{baseItem} - {statusMessage}";
                    }
                }
            }
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                _disposed = true;
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
    }
}
