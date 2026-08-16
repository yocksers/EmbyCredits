using System;
using System.Buffers;
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
            int maxParallelism = 4,
            System.Threading.CancellationToken cancellationToken = default)
        {
            using (var semaphore = new System.Threading.SemaphoreSlim(maxParallelism, maxParallelism))
            {
                var tasks = new List<Task<(string, string, double, double)>>(frames.Count);

                try
                {
                    foreach (var frame in frames)
                    {
                        await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);

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

                    return (await Task.WhenAll(tasks).ConfigureAwait(false)).ToList();
                }
                catch
                {
                    // Wait for already-scheduled tasks so they release the semaphore before it's disposed
                    try { await Task.WhenAll(tasks).ConfigureAwait(false); } catch { }
                    throw;
                }
            }
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

            int startIndex = recentMatches.Count - requiredConsecutive;
            for (int i = startIndex; i < recentMatches.Count; i++)
            {
                if (recentMatches[i].matchCount == 0)
                    return false;
            }

            for (int i = startIndex + 1; i < recentMatches.Count; i++)
            {
                if (recentMatches[i].timestamp - recentMatches[i - 1].timestamp > timestampTolerance)
                    return false;
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
            int startIndex = recentFrames.Count - minFrames;
            for (int i = startIndex + 1; i < recentFrames.Count; i++)
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

            var set2Exact = new HashSet<string>(lines2, StringComparer.OrdinalIgnoreCase);
            var set2Lower = new HashSet<string>(lines2.Select(l => l.ToLowerInvariant()), StringComparer.Ordinal);

            int matches = 0;
            foreach (var line in lines1)
            {
                if (set2Exact.Contains(line))
                {
                    matches++;
                    continue;
                }

                var lineLower = line.ToLowerInvariant();
                bool found = false;
                foreach (var l2Lower in set2Lower)
                {
                    if (l2Lower.Contains(lineLower, StringComparison.Ordinal) ||
                        lineLower.Contains(l2Lower, StringComparison.Ordinal))
                    {
                        found = true;
                        break;
                    }
                }
                if (found) matches++;
            }

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

            int rowSize = m + 1;
            int[] prev = ArrayPool<int>.Shared.Rent(rowSize);
            int[] curr = ArrayPool<int>.Shared.Rent(rowSize);

            try
            {
                for (int j = 0; j <= m; j++) prev[j] = j;

                for (int i = 1; i <= n; i++)
                {
                    curr[0] = i;
                    for (int j = 1; j <= m; j++)
                    {
                        int cost = (t[j - 1] == s[i - 1]) ? 0 : 1;
                        curr[j] = Math.Min(Math.Min(prev[j] + 1, curr[j - 1] + 1), prev[j - 1] + cost);
                    }
                    var tmp = prev;
                    prev = curr;
                    curr = tmp;
                }

                return prev[m];
            }
            finally
            {
                ArrayPool<int>.Shared.Return(prev);
                ArrayPool<int>.Shared.Return(curr);
            }
        }
    }
}
