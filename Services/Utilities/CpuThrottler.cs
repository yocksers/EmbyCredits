using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace EmbyCredits.Services.Utilities
{
    public class CpuThrottler
    {
        private readonly PluginConfiguration _configuration;
        private DateTime _lastWorkTime = DateTime.UtcNow;
        private TimeSpan _lastWorkDuration = TimeSpan.Zero;
        private static readonly bool IsWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        private static readonly bool IsLinux = RuntimeInformation.IsOSPlatform(OSPlatform.Linux);

        public CpuThrottler(PluginConfiguration configuration)
        {
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        }

        public void BeginWork()
        {
            _lastWorkTime = DateTime.UtcNow;
        }

        public async Task EndWork(CancellationToken cancellationToken = default)
        {
            _lastWorkDuration = DateTime.UtcNow - _lastWorkTime;

            if (_configuration.CpuUsageLimit >= 100)
                return;

            var throttleDelayMs = CalculateThrottleDelay();
            if (throttleDelayMs > 0)
            {
                try { await Task.Delay(throttleDelayMs, cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { }
            }
        }

        private int CalculateThrottleDelay()
        {
            if (_configuration.CpuUsageLimit >= 100)
                return 0;

            var cpuLimit = Math.Max(1, Math.Min(100, _configuration.CpuUsageLimit));
            var workMs = _lastWorkDuration.TotalMilliseconds;
            
            var targetIdleRatio = (100.0 - cpuLimit) / cpuLimit;
            var delayMs = (int)(workMs * targetIdleRatio);
            
            var baseThrottle = _configuration.CpuThrottleDelayMs;
            if (baseThrottle > 0)
            {
                delayMs = Math.Max(delayMs, baseThrottle);
            }

            return Math.Max(0, Math.Min(delayMs, 5000));
        }

        public static void SetProcessPriority(Process process, PluginConfiguration configuration)
        {
            if (process == null || configuration == null)
                return;

            if (!configuration.LowerProcessPriority)
                return;

            try
            {
                if (!process.HasExited)
                {
                    if (IsWindows)
                    {
                        process.PriorityClass = ProcessPriorityClass.BelowNormal;
                    }
                    else if (IsLinux)
                    {
                        try
                        {
                            var processId = process.Id;
                            var startInfo = new ProcessStartInfo
                            {
                                FileName = "/usr/bin/renice",
                                Arguments = $"10 -p {processId}",
                                UseShellExecute = false,
                                RedirectStandardOutput = true,
                                RedirectStandardError = true,
                                CreateNoWindow = true
                            };
                            using (var reniceProcess = Process.Start(startInfo))
                            {
                                if (reniceProcess != null)
                                {
                                    reniceProcess.WaitForExit(1000);
                                }
                            }
                        }
                        catch
                        {
                        }
                    }
                }
            }
            catch (Exception)
            {
            }
        }

        public static void SetProcessPriority(int processId, PluginConfiguration configuration)
        {
            if (configuration == null || !configuration.LowerProcessPriority)
                return;

            try
            {
                using (var process = Process.GetProcessById(processId))
                {
                    SetProcessPriority(process, configuration);
                }
            }
            catch (Exception)
            {
            }
        }
    }
}
