using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace EmbyCredits.Services.DetectionMethods
{
    public static class OcrOptimizations
    {
        public static async Task<List<(string framePath, string ocrText, double confidence, double timestamp)>> ProcessFramesBatch(
            List<(string path, double timestamp)> frames,
            Func<string, Task<(string text, double confidence)>> ocrFunction,
            int maxParallelism = 4)
        {
            var results = new List<(string, string, double, double)>();
            using (var semaphore = new System.Threading.SemaphoreSlim(maxParallelism, maxParallelism))
            {
                var tasks = new List<Task<(string, string, double, double)>>();

                foreach (var frame in frames)
                {
                    await semaphore.WaitAsync().ConfigureAwait(false);

                    var task = Task.Run(async () =>
                    {
                        try
                        {
                            var (text, confidence) = await ocrFunction(frame.path).ConfigureAwait(false);
                            return (frame.path, text, confidence, frame.timestamp);
                        }
                        finally
                        {
                            semaphore.Release();
                        }
                    });

                    tasks.Add(task);
                }

                results = (await Task.WhenAll(tasks).ConfigureAwait(false)).ToList();
            }
            return results;
        }

        public static int CalculateSmartSkip(int consecutiveMatches, int defaultSkip = 1)
        {
            if (consecutiveMatches >= 5)
            {
                return 20;
            }
            else if (consecutiveMatches >= 3)
            {
                return 10;
            }
            else if (consecutiveMatches >= 2)
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

        public static bool DetectScrollingPattern(
            List<(double timestamp, string text)> recentFrames,
            int minFrames = 5,
            double overlapThreshold = 0.3)
        {
            if (recentFrames.Count < minFrames)
                return false;

            var textPositionChanges = 0;
            for (int i = 1; i < recentFrames.Count; i++)
            {
                var overlap = GetTextOverlap(recentFrames[i - 1].text, recentFrames[i].text);
                if (overlap > overlapThreshold)
                    textPositionChanges++;
            }

            return textPositionChanges >= minFrames - 2;
        }

        public static double GetTextOverlap(string text1, string text2)
        {
            var lines1 = text1.Split('\n').Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
            var lines2 = text2.Split('\n').Where(l => !string.IsNullOrWhiteSpace(l)).ToList();

            if (lines1.Count == 0 || lines2.Count == 0)
                return 0;

            int matches = lines1.Count(line => lines2.Any(l => l.Contains(line, StringComparison.OrdinalIgnoreCase) || 
                                                               line.Contains(l, StringComparison.OrdinalIgnoreCase)));
            return (double)matches / Math.Max(lines1.Count, lines2.Count);
        }

        public static double CalculateAdaptiveFrameRate(int consecutiveMatches, double baseFps, double minFps)
        {
            if (consecutiveMatches >= 5)
                return Math.Max(minFps, baseFps * 0.5);
            else if (consecutiveMatches >= 2)
                return Math.Max(minFps, baseFps * 0.75);
            return baseFps;
        }

        public static int LevenshteinDistance(string s, string t)
        {
            int n = s.Length, m = t.Length;
            if (n == 0) return m;
            if (m == 0) return n;

            int[,] d = new int[n + 1, m + 1];

            for (int i = 0; i <= n; i++) d[i, 0] = i;
            for (int j = 0; j <= m; j++) d[0, j] = j;

            for (int i = 1; i <= n; i++)
            {
                for (int j = 1; j <= m; j++)
                {
                    int cost = (t[j - 1] == s[i - 1]) ? 0 : 1;
                    d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
                }
            }

            return d[n, m];
        }
    }
}
