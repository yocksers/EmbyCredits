using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace EmbyCredits.Services.DetectionMethods
{
    internal sealed class DetectionTimestampCache
    {
        private readonly ConcurrentDictionary<string, List<double>> _timestamps =
            new ConcurrentDictionary<string, List<double>>();
        private readonly ConcurrentDictionary<string, DateTime> _lastAccess =
            new ConcurrentDictionary<string, DateTime>();

        private const int MaxEntries = 100;
        private static readonly TimeSpan Expiration = TimeSpan.FromHours(24);

        public static string MakeCacheKey(string seriesId, int seasonNumber) =>
            $"{seriesId}_S{seasonNumber:D2}";

        public void TouchAccess(string cacheKey)
        {
            _lastAccess[cacheKey] = DateTime.UtcNow;
        }

        public bool TryGetTimestamps(string cacheKey, out List<double> timestamps) =>
            _timestamps.TryGetValue(cacheKey, out timestamps!);

        public List<double> GetOrAddList(string cacheKey) =>
            _timestamps.GetOrAdd(cacheKey, _ => new List<double>());

        public void EnsureCleanedIfNeeded()
        {
            if (_timestamps.Count > MaxEntries || _lastAccess.Count > MaxEntries)
                Cleanup();
        }

        public void ClearSeries(string seriesId, int seasonNumber)
        {
            var key = MakeCacheKey(seriesId, seasonNumber);
            if (_timestamps.TryGetValue(key, out var list))
            {
                lock (list) { list.Clear(); }
            }
            _timestamps.TryRemove(key, out _);
            _lastAccess.TryRemove(key, out _);
        }

        public void ClearAll()
        {
            foreach (var kvp in _timestamps)
            {
                lock (kvp.Value) { kvp.Value.Clear(); }
            }
            _timestamps.Clear();
            _lastAccess.Clear();
        }

        private void Cleanup()
        {
            var now = DateTime.UtcNow;
            var expired = _lastAccess
                .Where(kvp => now - kvp.Value > Expiration)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var key in expired)
            {
                _timestamps.TryRemove(key, out _);
                _lastAccess.TryRemove(key, out _);
            }

            if (_timestamps.Count > MaxEntries)
            {
                var toRemove = _lastAccess
                    .OrderBy(kvp => kvp.Value)
                    .Take(_timestamps.Count - MaxEntries)
                    .Select(kvp => kvp.Key)
                    .ToList();

                foreach (var key in toRemove)
                {
                    _timestamps.TryRemove(key, out _);
                    _lastAccess.TryRemove(key, out _);
                }
            }

            var orphans = _lastAccess.Keys
                .Where(k => !_timestamps.ContainsKey(k))
                .ToList();
            foreach (var key in orphans)
                _lastAccess.TryRemove(key, out _);
        }
    }
}
