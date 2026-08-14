using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.MediaInfo;
using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace EmbyCredits.Services.Utilities
{

    public static class FFmpegHelper
    {
        private static string? _customTempPath;
        private static IFfmpegManager? _ffmpegManager;
        private static IMediaEncoder? _mediaEncoder;
        private static readonly ConcurrentDictionary<int, (Process process, DateTime startTime, string description)> _activeProcesses = new ConcurrentDictionary<int, (Process, DateTime, string)>();
        private static readonly ConcurrentDictionary<int, DateTime> _lastOutputTime = new ConcurrentDictionary<int, DateTime>();
        private static readonly object _cleanupLock = new object();
        private static Timer? _hungProcessCleanupTimer;

        public static void UpdateLastOutputTime(int pid)
        {
            _lastOutputTime[pid] = DateTime.UtcNow;
        }

        public static void RegisterProcess(Process process, string description)
        {
            try
            {
                if (process != null && !process.HasExited)
                {
                    _activeProcesses.TryAdd(process.Id, (process, DateTime.UtcNow, description));
                }
            }
            catch
            {
            }
        }

        public static void UnregisterProcess(Process process)
        {
            try
            {
                if (process != null)
                {
                    _activeProcesses.TryRemove(process.Id, out _);
                    _lastOutputTime.TryRemove(process.Id, out _);
                }
            }
            catch
            {
            }
        }

        public static int KillHungProcesses(int maxAgeSeconds = 900)
        {
            lock (_cleanupLock)
            {
                var killedCount = 0;
                var now = DateTime.UtcNow;
                var toRemove = new List<int>();

                foreach (var kvp in _activeProcesses)
                {
                    try
                    {
                        var (process, startTime, description) = kvp.Value;
                        var age = (now - startTime).TotalSeconds;

                        if (process.HasExited)
                        {
                            toRemove.Add(kvp.Key);
                            continue;
                        }

                        if (age > maxAgeSeconds)
                        {
                            try
                            {
                                process.Kill();
                                killedCount++;
                            }
                            catch
                            {
                            }
                            toRemove.Add(kvp.Key);
                        }
                    }
                    catch
                    {
                        toRemove.Add(kvp.Key);
                    }
                }

                foreach (var id in toRemove)
                {
                    _activeProcesses.TryRemove(id, out _);
                }

                return killedCount;
            }
        }

        private const int HungProcessTimeoutSeconds = 900;

        public static IReadOnlyList<object> GetActiveProcesses()
        {
            var result = new List<object>();
            var now = DateTime.UtcNow;
            foreach (var kvp in _activeProcesses)
            {
                try
                {
                    var (process, startTime, description) = kvp.Value;
                    if (process.HasExited) continue;
                    var ageSeconds = (now - startTime).TotalSeconds;
                    var percentOfTimeout = Math.Min(100.0, ageSeconds / HungProcessTimeoutSeconds * 100.0);
                    int? secondsSinceLastOutput = null;
                    if (_lastOutputTime.TryGetValue(kvp.Key, out var lastOutput))
                    {
                        secondsSinceLastOutput = (int)(now - lastOutput).TotalSeconds;
                    }
                    result.Add(new { Description = description, AgeSeconds = (int)ageSeconds, PercentOfTimeout = Math.Round(percentOfTimeout, 1), SecondsSinceLastOutput = secondsSinceLastOutput });
                }
                catch
                {
                }
            }
            return result;
        }

        public static string NormalizeFilePath(string path)
        {
            if (string.IsNullOrEmpty(path))
                return path;

            if (path.StartsWith("smb://"))
            {

                if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
                    System.Runtime.InteropServices.OSPlatform.Windows))
                {

                    var uncPath = path.Substring(6);

                    uncPath = uncPath.Replace('/', '\\');

                    uncPath = "\\\\" + uncPath;

                    return uncPath;
                }
                else
                {

                    var smbPath = path.Substring(6);
                    var pathParts = smbPath.Split('/');

                    if (pathParts.Length >= 2)
                    {
                        var server = pathParts[0];
                        var remainingPath = string.Join("/", pathParts.Skip(1));

                        var mountPatterns = new[]
                        {
                            $"/mnt/{server}/{remainingPath}",
                            $"/media/{server}/{remainingPath}",
                            $"/mnt/smb/{remainingPath}",
                            $"/media/smb/{remainingPath}",
                            $"/mnt/nas/{remainingPath}",
                            $"/media/nas/{remainingPath}"
                        };

                        foreach (var mountPath in mountPatterns)
                        {
                            if (File.Exists(mountPath))
                            {
                                return mountPath;
                            }
                        }
                    }

                    return path;
                }
            }

            return path;
        }

        public static void Initialize(IFfmpegManager ffmpegManager, IMediaEncoder mediaEncoder)
        {
            _ffmpegManager = ffmpegManager ?? throw new ArgumentNullException(nameof(ffmpegManager));
            _mediaEncoder = mediaEncoder ?? throw new ArgumentNullException(nameof(mediaEncoder));
            _hungProcessCleanupTimer?.Dispose();
            _hungProcessCleanupTimer = new Timer(_ => KillHungProcesses(), null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
        }

        public static void SetCustomTempPath(string? customPath)
        {
            _customTempPath = customPath;
        }

        public static string GetTempPath()
        {
            if (!string.IsNullOrWhiteSpace(_customTempPath) && Directory.Exists(_customTempPath))
            {
                return _customTempPath;
            }
            return Path.GetTempPath();
        }

        public static string GetFfmpegPath()
        {
            if (_ffmpegManager == null)
                throw new InvalidOperationException("FFmpegHelper not initialized");

            var config = _ffmpegManager.FfmpegConfiguration;
            if (config == null || string.IsNullOrEmpty(config.EncoderPath) || !File.Exists(config.EncoderPath))
            {
                throw new FileNotFoundException("FFmpeg encoder not found");
            }

            return config.EncoderPath;
        }

        public static string GetFfprobePath()
        {
            if (_ffmpegManager == null)
                throw new InvalidOperationException("FFmpegHelper not initialized");

            var config = _ffmpegManager.FfmpegConfiguration;
            if (config == null || string.IsNullOrEmpty(config.ProbePath) || !File.Exists(config.ProbePath))
            {
                throw new FileNotFoundException("FFprobe not found");
            }

            return config.ProbePath;
        }

        public static string GetInputArgument(string path)
        {
            if (_mediaEncoder == null)
            {
                return NormalizeFilePath(path);
            }

            var normalizedPath = NormalizeFilePath(path);
            
            MediaProtocol protocol;
            if (normalizedPath.StartsWith("smb://", StringComparison.OrdinalIgnoreCase))
            {
                protocol = MediaProtocol.File;
            }
            else if (normalizedPath.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                     normalizedPath.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                protocol = MediaProtocol.Http;
            }
            else if (normalizedPath.StartsWith("rtsp://", StringComparison.OrdinalIgnoreCase))
            {
                protocol = MediaProtocol.Rtsp;
            }
            else
            {
                protocol = MediaProtocol.File;
            }

            return _mediaEncoder.GetInputArgument(normalizedPath.AsSpan(), protocol);
        }

        public static int CleanupOrphanedTempDirectories()
        {
            var deletedCount = 0;
            try
            {
                var tempPath = GetTempPath();
                var directories = Directory.GetDirectories(tempPath, "ocr_frames_*");

                foreach (var dir in directories)
                {
                    try
                    {
                        var dirInfo = new DirectoryInfo(dir);

                        if (dirInfo.Exists && (DateTime.UtcNow - dirInfo.CreationTimeUtc).TotalHours > 1)
                        {
                            Directory.Delete(dir, true);
                            deletedCount++;
                        }
                    }
                    catch
                    {

                    }
                }
            }
            catch
            {

            }
            return deletedCount;
        }

        /// <summary>
        /// Generate chromaprint audio fingerprint for a media file.
        /// </summary>
        /// <param name="filePath">Path to the media file</param>
        /// <param name="startTime">Start time in seconds</param>
        /// <param name="endTime">End time in seconds</param>
        /// <param name="logger">Logger instance</param>
        /// <returns>Array of chromaprint fingerprint points (uint32 values)</returns>
        public static uint[] GenerateChromaprint(string filePath, double startTime, double endTime, MediaBrowser.Model.Logging.ILogger? logger = null, int threads = 0)
        {
            var normalizedPath = NormalizeFilePath(filePath);
            var ffmpegPath = GetFfmpegPath();
            var duration = endTime - startTime;

            var threadArg = threads > 0 ? $"-threads {threads} " : string.Empty;
            var args = string.Format(
                CultureInfo.InvariantCulture,
                "-hide_banner -loglevel warning {3}-ss {0} -i \"{1}\" -to {2} -ac 2 -f chromaprint -fp_format raw -",
                startTime,
                normalizedPath,
                duration,
                threadArg);

            logger?.Debug($"FFmpeg chromaprint command: {ffmpegPath} {args}");

            var info = new ProcessStartInfo(ffmpegPath, args)
            {
                WindowStyle = ProcessWindowStyle.Hidden,
                CreateNoWindow = true,
                UseShellExecute = false,
                ErrorDialog = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var process = new Process { StartInfo = info };
            process.Start();
            
            RegisterProcess(process, $"Chromaprint: {Path.GetFileName(filePath)}");

            try
            {
                process.PriorityClass = ProcessPriorityClass.BelowNormal;
            }
            catch (Exception e)
            {
                logger?.Debug($"FFmpeg priority could not be modified: {e.Message}");
            }

            try
            {
                using var ms = new MemoryStream();
                const int BufSize = 8192;
                var buf = ArrayPool<byte>.Shared.Rent(BufSize);
                try
                {
                    int bytesRead;
                    while ((bytesRead = process.StandardOutput.BaseStream.Read(buf, 0, BufSize)) > 0)
                    {
                        ms.Write(buf, 0, bytesRead);
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buf);
                }

                if (!process.WaitForExit(60000))
                {
                    try
                    {
                        if (!process.HasExited)
                        {
                            process.Kill();
                            logger?.Warn($"Chromaprint process timed out and was killed for {filePath}");
                        }
                    }
                    catch (Exception ex)
                    {
                        logger?.Error($"Failed to kill timed out chromaprint process: {ex.Message}");
                    }
                    return Array.Empty<uint>();
                }

                var rawPoints = ms.ToArray();
                if (rawPoints.Length == 0 || rawPoints.Length % 4 != 0)
                {
                    logger?.Warn($"Chromaprint returned {rawPoints.Length} bytes for {filePath}");
                    return Array.Empty<uint>();
                }

                var pointCount = rawPoints.Length / 4;
                var results = new uint[pointCount];
                for (int i = 0, j = 0; i < rawPoints.Length; i += 4, j++)
                {
                    results[j] = BitConverter.ToUInt32(rawPoints, i);
                }

                logger?.Debug($"Generated {results.Length} fingerprint points for {filePath}");
                return results;
            }
            finally
            {
                UnregisterProcess(process);
            }
        }

        /// <summary>
        /// Compare two fingerprints using XOR operations to find matching sequences.
        /// This is similar to how Intro Skipper compares chromaprint fingerprints.
        /// </summary>
        /// <param name="fp1">First fingerprint</param>
        /// <param name="fp2">Second fingerprint</param>
        /// <param name="maxShift">Maximum time shift to consider in points</param>
        /// <returns>Best match score and offset (0-1, where 1 is perfect match)</returns>
        public static (double score, int offset) CompareFingerprints(uint[] fp1, uint[] fp2, int maxShift = 120)
        {
            if (fp1.Length == 0 || fp2.Length == 0)
                return (0, 0);

            double bestScore = 0;
            int bestOffset = 0;

            // Try different time shifts
            for (int shift = -maxShift; shift <= maxShift; shift++)
            {
                int matchCount = 0;
                int compareCount = 0;

                // Calculate overlap region
                int start1 = Math.Max(0, -shift);
                int start2 = Math.Max(0, shift);
                int length = Math.Min(fp1.Length - start1, fp2.Length - start2);

                if (length <= 0)
                    continue;

                // Compare fingerprints using XOR
                for (int i = 0; i < length; i++)
                {
                    var xorResult = fp1[start1 + i] ^ fp2[start2 + i];
                    
                    // Count matching bits (bits that are 0 after XOR)
                    var matchingBits = 32 - System.Numerics.BitOperations.PopCount(xorResult);
                    matchCount += matchingBits;
                    compareCount += 32;
                }

                double score = (double)matchCount / compareCount;
                if (score > bestScore)
                {
                    bestScore = score;
                    bestOffset = shift;
                }
            }

            return (bestScore, bestOffset);
        }
        
        /// <summary>
        /// Find the best matching subsequence within two fingerprints.
        /// This is useful for credits detection where we fingerprint a large window but need to find
        /// where the credits music specifically begins within that window.
        /// </summary>
        /// <param name="fp1">First fingerprint</param>
        /// <param name="fp2">Second fingerprint</param>
        /// <param name="minWindowSize">Minimum window size in points (default 100 = ~13 seconds)</param>
        /// <returns>Best match info: score, offset in fp1, offset in fp2</returns>
        public static (double score, int offsetFp1, int offsetFp2) FindBestMatchingSubsequence(uint[] fp1, uint[] fp2, int minWindowSize = 100, int maxDiagonalOffset = 75)
        {
            if (fp1.Length == 0 || fp2.Length == 0)
                return (0, 0, 0);

            double bestScore = 0;
            int bestOffsetFp1 = 0;
            int bestOffsetFp2 = 0;

            var windowSize = Math.Min(Math.Min(fp1.Length, fp2.Length), 300);
            windowSize = Math.Max(windowSize, minWindowSize);

            for (int start1 = 0; start1 <= fp1.Length - windowSize; start1 += 10)
            {
                // Both fingerprints are anchored to the same position relative to the end
                // of the video, so the credits audio appears at the same index in both arrays.
                // Restricting search to positions near the diagonal (start2 ≈ start1) avoids
                // the O(L²) explosion that causes lockups on large batches.
                var start2Min = Math.Max(0, start1 - maxDiagonalOffset);
                var start2Max = Math.Min(fp2.Length - windowSize, start1 + maxDiagonalOffset);

                for (int start2 = start2Min; start2 <= start2Max; start2 += 10)
                {
                    int matchCount = 0;
                    int compareCount = 0;

                    for (int i = 0; i < windowSize; i++)
                    {
                        var xorResult = fp1[start1 + i] ^ fp2[start2 + i];
                        var matchingBits = 32 - System.Numerics.BitOperations.PopCount(xorResult);
                        matchCount += matchingBits;
                        compareCount += 32;
                    }

                    double score = (double)matchCount / compareCount;
                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestOffsetFp1 = start1;
                        bestOffsetFp2 = start2;
                    }
                }
            }

            return (bestScore, bestOffsetFp1, bestOffsetFp2);
        }    }
}