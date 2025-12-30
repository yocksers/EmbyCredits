using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace EmbyCredits.Services
{
    /// <summary>
    /// Service that tracks successful credit timestamps for TV series and provides averaged fallback timestamps for failed episodes.
    /// </summary>
    public class SeriesAveragingService : IDisposable
    {
        private readonly ILogger _logger;
        private readonly PluginConfiguration _configuration;
        private readonly ConcurrentDictionary<string, SeriesTimestampData> _seriesData;
        private bool _disposed = false;

        public SeriesAveragingService(ILogger logger, PluginConfiguration configuration)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
            _seriesData = new ConcurrentDictionary<string, SeriesTimestampData>();
        }

        /// <summary>
        /// Records a successful timestamp for an episode.
        /// </summary>
        public void RecordSuccessfulTimestamp(Episode episode, double timestamp)
        {
            if (episode == null || timestamp <= 0)
                return;

            var series = episode.Series;
            if (series == null)
            {
                _logger.Debug($"[SeriesAveraging] Episode {episode.Name} has no parent series");
                return;
            }

            var seriesId = series.Id.ToString();
            var data = _seriesData.GetOrAdd(seriesId, _ => new SeriesTimestampData(series.Name));
            
            data.AddTimestamp(timestamp);
            
            _logger.Debug($"[SeriesAveraging] Recorded timestamp {FormatTime(timestamp)} for {series.Name}. Total: {data.TimestampCount}");
        }

        /// <summary>
        /// Attempts to get an averaged timestamp for a failed episode based on its series' successful episodes.
        /// </summary>
        /// <returns>Average timestamp in seconds, or 0 if not enough data available</returns>
        public double GetAveragedTimestamp(Episode episode)
        {
            if (episode == null)
                return 0;

            var series = episode.Series;
            if (series == null)
            {
                _logger.Debug($"[SeriesAveraging] Episode {episode.Name} has no parent series");
                return 0;
            }

            var seriesId = series.Id.ToString();
            if (!_seriesData.TryGetValue(seriesId, out var data))
            {
                _logger.Debug($"[SeriesAveraging] No data available for series {series.Name}");
                return 0;
            }

            if (data.TimestampCount < _configuration.MinimumEpisodesForAveraging)
            {
                _logger.Debug($"[SeriesAveraging] Not enough data for {series.Name}: {data.TimestampCount}/{_configuration.MinimumEpisodesForAveraging}");
                return 0;
            }

            var average = data.GetAverageTimestamp();
            _logger.Info($"[SeriesAveraging] Using averaged timestamp {FormatTime(average)} for {episode.Name} (based on {data.TimestampCount} episodes from {series.Name})");
            
            return average;
        }

        /// <summary>
        /// Gets statistics for a specific series.
        /// </summary>
        public SeriesStats? GetSeriesStats(string seriesId)
        {
            if (_seriesData.TryGetValue(seriesId, out var data))
            {
                return new SeriesStats
                {
                    SeriesName = data.SeriesName,
                    EpisodeCount = data.TimestampCount,
                    AverageTimestamp = data.GetAverageTimestamp(),
                    MinTimestamp = data.GetMinTimestamp(),
                    MaxTimestamp = data.GetMaxTimestamp()
                };
            }

            return null;
        }

        /// <summary>
        /// Clears all stored series data.
        /// </summary>
        public void Clear()
        {
            _seriesData.Clear();
            _logger.Info("[SeriesAveraging] Cleared all series data");
        }

        /// <summary>
        /// Clears data for a specific series.
        /// </summary>
        public void ClearSeries(string seriesId)
        {
            if (_seriesData.TryRemove(seriesId, out var data))
            {
                _logger.Info($"[SeriesAveraging] Cleared data for series {data.SeriesName}");
            }
        }

        /// <summary>
        /// Gets the total number of series being tracked.
        /// </summary>
        public int TrackedSeriesCount => _seriesData.Count;

        /// <summary>
        /// Gets the total number of timestamps across all series.
        /// </summary>
        public int TotalTimestampCount => _seriesData.Values.Sum(d => d.TimestampCount);

        private string FormatTime(double seconds)
        {
            var ts = TimeSpan.FromSeconds(seconds);
            return $"{(int)ts.TotalMinutes}:{ts.Seconds:D2}";
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _seriesData.Clear();
                _disposed = true;
            }
        }

        /// <summary>
        /// Internal class to store timestamp data for a series.
        /// </summary>
        private class SeriesTimestampData
        {
            private readonly List<double> _timestamps = new List<double>();
            private readonly object _lock = new object();

            public string SeriesName { get; }
            public int TimestampCount
            {
                get
                {
                    lock (_lock)
                    {
                        return _timestamps.Count;
                    }
                }
            }

            public SeriesTimestampData(string seriesName)
            {
                SeriesName = seriesName;
            }

            public void AddTimestamp(double timestamp)
            {
                lock (_lock)
                {
                    _timestamps.Add(timestamp);
                }
            }

            public double GetAverageTimestamp()
            {
                lock (_lock)
                {
                    return _timestamps.Count > 0 ? _timestamps.Average() : 0;
                }
            }

            public double GetMinTimestamp()
            {
                lock (_lock)
                {
                    return _timestamps.Count > 0 ? _timestamps.Min() : 0;
                }
            }

            public double GetMaxTimestamp()
            {
                lock (_lock)
                {
                    return _timestamps.Count > 0 ? _timestamps.Max() : 0;
                }
            }
        }
    }

    /// <summary>
    /// Statistics for a series.
    /// </summary>
    public class SeriesStats
    {
        public string SeriesName { get; set; } = string.Empty;
        public int EpisodeCount { get; set; }
        public double AverageTimestamp { get; set; }
        public double MinTimestamp { get; set; }
        public double MaxTimestamp { get; set; }
    }
}
