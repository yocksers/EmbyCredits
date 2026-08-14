using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace EmbyCredits.Services.DetectionMethods
{
    internal sealed class DetectionTimestampCache
    {
        private sealed class CacheEntry
        {
            public readonly List<double> Timestamps = new List<double>();
            public DateTime LastAccess = DateTime.UtcNow;
        }

        private readonly ConcurrentDictionary<string, CacheEntry> _entries =
            new ConcurrentDictionary<string, CacheEntry>();

        private const int MaxEntries = 100;
        private static readonly TimeSpan Expiration = TimeSpan.FromHours(24);

        public static string MakeCacheKey(string seriesId, int seasonNumber) =>
            $"{seriesId}_S{seasonNumber:D2}";

        public void TouchAccess(string cacheKey)
        {
            if (_entries.TryGetValue(cacheKey, out var entry))
                entry.LastAccess = DateTime.UtcNow;
        }

        public bool TryGetTimestamps(string cacheKey, out List<double> timestamps)
        {
            if (_entries.TryGetValue(cacheKey, out var entry))
            {
                timestamps = entry.Timestamps;
                return true;
            }
            timestamps = null!;
            return false;
        }

        public List<double> GetOrAddList(string cacheKey)
        {
            var entry = _entries.GetOrAdd(cacheKey, _ => new CacheEntry());
            entry.LastAccess = DateTime.UtcNow;
            return entry.Timestamps;
        }

        public void EnsureCleanedIfNeeded()
        {
            if (_entries.Count > MaxEntries)
                Cleanup();
        }

        public void ClearSeries(string seriesId, int seasonNumber)
        {
            var key = MakeCacheKey(seriesId, seasonNumber);
            if (_entries.TryRemove(key, out var entry))
            {
                lock (entry.Timestamps) { entry.Timestamps.Clear(); }
            }
        }

        public void ClearAll()
        {
            foreach (var kvp in _entries)
            {
                lock (kvp.Value.Timestamps) { kvp.Value.Timestamps.Clear(); }
            }
            _entries.Clear();
        }

        private void Cleanup()
        {
            var now = DateTime.UtcNow;

            foreach (var kvp in _entries)
            {
                if (now - kvp.Value.LastAccess > Expiration)
                    _entries.TryRemove(kvp.Key, out _);
            }

            if (_entries.Count > MaxEntries)
            {
                var toRemove = _entries
                    .OrderBy(kvp => kvp.Value.LastAccess)
                    .Take(_entries.Count - MaxEntries)
                    .Select(kvp => kvp.Key)
                    .ToList();

                foreach (var key in toRemove)
                    _entries.TryRemove(key, out _);
            }
        }
    }
}
