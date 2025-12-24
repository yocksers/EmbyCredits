using MediaBrowser.Model.Logging;
using System;
using System.Threading.Tasks;

namespace EmbyCredits.Services.DetectionMethods
{

    public abstract class BaseDetectionMethod : IDetectionMethod
    {
        protected readonly ILogger Logger;
        protected readonly PluginConfiguration Configuration;

        public abstract string MethodName { get; }
        public abstract double Confidence { get; }
        public abstract int Priority { get; }
        public abstract bool IsEnabled { get; }

        protected BaseDetectionMethod(ILogger logger, PluginConfiguration configuration)
        {
            Logger = logger ?? throw new ArgumentNullException(nameof(logger));
            Configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        }

        public abstract Task<double> DetectCredits(string videoPath, double duration);

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
            if (Configuration.EnableDetailedLogging)
                Logger.Warn($"[{MethodName}] {message}");
        }

        protected void LogError(string message, Exception? ex = null)
        {
            if (ex != null)
                Logger.ErrorException($"[{MethodName}] {message}", ex);
            else
                Logger.Error($"[{MethodName}] {message}");
        }

        protected string FormatTime(double seconds)
        {
            var ts = TimeSpan.FromSeconds(seconds);
            return $"{(int)ts.TotalMinutes}:{ts.Seconds:D2}";
        }

        protected void UpdateProgress(double progressPercentage, string? statusMessage = null)
        {
            if (Plugin.Instance != null)
            {
                progressPercentage = Math.Max(0, Math.Min(100, progressPercentage));
                Plugin.Progress.CurrentItemProgress = (int)progressPercentage;

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
    }
}
