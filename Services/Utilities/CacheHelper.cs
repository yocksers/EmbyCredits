using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace EmbyCredits.Services.Utilities
{
    public class FingerprintCache
    {
        public string? EpisodeId { get; set; }
        public double Duration { get; set; }
        public List<VideoFingerprint>? Fingerprint { get; set; }
        public DateTime Timestamp { get; set; }
    }
    public class VideoFingerprint
    {
        public double Timestamp { get; set; }
        public string? Hash { get; set; }
    }
    public class AudioFingerprintFrame
    {
        public double Timestamp { get; set; }
        public string? SpectralHash { get; set; }
    }
    public static class CacheHelper
    {
        private static string? _fingerprintCacheDirectory;
        private static string? _audioCacheDirectory;

        public static void Initialize(string baseCachePath)
        {
            _fingerprintCacheDirectory = Path.Combine(baseCachePath, "EmbyCredits", "Fingerprints");
            _audioCacheDirectory = Path.Combine(baseCachePath, "EmbyCredits", "AudioFingerprints");

            Directory.CreateDirectory(_fingerprintCacheDirectory);
            Directory.CreateDirectory(_audioCacheDirectory);
        }

        public static string GetFingerprintCacheFile(string episodeId)
        {
            if (string.IsNullOrEmpty(_fingerprintCacheDirectory))
                throw new InvalidOperationException("CacheHelper not initialized");

            return Path.Combine(_fingerprintCacheDirectory, $"{episodeId}.json");
        }

        public static string GetAudioCacheFile(string episodeId)
        {
            if (string.IsNullOrEmpty(_audioCacheDirectory))
                throw new InvalidOperationException("CacheHelper not initialized");

            return Path.Combine(_audioCacheDirectory, $"{episodeId}_audio.json");
        }

        public static void SaveToCache<T>(string filePath, T data)
        {
            try
            {
                var json = System.Text.Json.JsonSerializer.Serialize(data);
                File.WriteAllText(filePath, json);
            }
            catch
            {
            }
        }

        public static T? LoadFromCache<T>(string filePath) where T : class
        {
            try
            {
                if (!File.Exists(filePath))
                    return null;

                var json = File.ReadAllText(filePath);
                return System.Text.Json.JsonSerializer.Deserialize<T>(json);
            }
            catch
            {
                return null;
            }
        }

        public static int ClearCache()
        {
            int deletedCount = 0;

            if (!string.IsNullOrEmpty(_fingerprintCacheDirectory) && Directory.Exists(_fingerprintCacheDirectory))
            {
                deletedCount += DeleteFilesInDirectory(_fingerprintCacheDirectory, "*.json");
            }

            if (!string.IsNullOrEmpty(_audioCacheDirectory) && Directory.Exists(_audioCacheDirectory))
            {
                deletedCount += DeleteFilesInDirectory(_audioCacheDirectory, "*.json");
            }

            return deletedCount;
        }

        private static int DeleteFilesInDirectory(string directory, string pattern)
        {
            int count = 0;
            var files = Directory.GetFiles(directory, pattern);
            foreach (var file in files)
            {
                try
                {
                    File.Delete(file);
                    count++;
                }
                catch
                {
                }
            }
            return count;
        }
    }
}
