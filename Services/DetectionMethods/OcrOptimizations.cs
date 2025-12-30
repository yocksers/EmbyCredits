using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace EmbyCredits.Services.DetectionMethods
{
    public static class OcrOptimizations
    {
        public static async Task<List<(string framePath, string ocrText, double timestamp)>> ProcessFramesBatch(
            List<(string path, double timestamp)> frames,
            Func<string, Task<string>> ocrFunction,
            int maxParallelism = 4)
        {
            var results = new List<(string, string, double)>();
            var semaphore = new System.Threading.SemaphoreSlim(maxParallelism, maxParallelism);
            var tasks = new List<Task<(string, string, double)>>();

            foreach (var frame in frames)
            {
                await semaphore.WaitAsync().ConfigureAwait(false);

                var task = Task.Run(async () =>
                {
                    try
                    {
                        var text = await ocrFunction(frame.path).ConfigureAwait(false);
                        return (frame.path, text, frame.timestamp);
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                });

                tasks.Add(task);
            }

            results = (await Task.WhenAll(tasks).ConfigureAwait(false)).ToList();
            return results;
        }

        public static int CalculateSmartSkip(int consecutiveMatches, int defaultSkip = 1)
        {
            if (consecutiveMatches >= 3)
            {
                return 10;
            }
            else if (consecutiveMatches >= 1)
            {
                return 5;
            }
            return defaultSkip;
        }

        public static bool ShouldTerminateEarly(
            List<(double timestamp, int matchCount)> recentMatches,
            int requiredConsecutive,
            double timestampTolerance = 10.0)
        {
            if (requiredConsecutive <= 0 || recentMatches.Count < requiredConsecutive)
            {
                return false;
            }

            var lastMatches = recentMatches.TakeLast(requiredConsecutive).ToList();
            
            if (lastMatches.Any(m => m.matchCount == 0))
            {
                return false;
            }

            for (int i = 1; i < lastMatches.Count; i++)
            {
                if (lastMatches[i].timestamp - lastMatches[i - 1].timestamp > timestampTolerance)
                {
                    return false;
                }
            }

            return true;
        }
    }
}
