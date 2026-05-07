using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EmbyCredits.Services
{
    /// <summary>
    /// Tracks episodes that have been added to the library but have not yet had
    /// credits detection run on them.  Thread-safe; persists state to a JSON file
    /// in the plugin data folder so entries survive restarts.
    /// </summary>
    public class TracerService : DebouncedPersistenceService
    {
        private readonly ILogger _logger;
        private readonly string _stateFilePath;
        private readonly string _detectedFilePath;
        private const int MaxDetectedEntries = 100;

        // episodeId (string) -> TracerEntry
        private ConcurrentDictionary<string, TracerEntry> _pending =
            new ConcurrentDictionary<string, TracerEntry>(StringComparer.OrdinalIgnoreCase);

        // Recently auto-detected episodes (newest first, capped at MaxDetectedEntries)
        private ConcurrentDictionary<string, TracerEntry> _detected =
            new ConcurrentDictionary<string, TracerEntry>(StringComparer.OrdinalIgnoreCase);

        private static readonly JsonSerializerOptions _jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        public TracerService(ILogger logger, string dataFolderPath)
        {
            _logger = logger;
            _stateFilePath = Path.Combine(dataFolderPath, "tracer_pending.json");
            _detectedFilePath = Path.Combine(dataFolderPath, "tracer_detected.json");
            Load();
        }

        // ------------------------------------------------------------------ //
        //  Public API
        // ------------------------------------------------------------------ //

        /// <summary>Called when a new episode is added to the library.</summary>
        public void TrackEpisode(Episode episode)
        {
            try
            {
                var id = episode.Id.ToString();
                var entry = new TracerEntry
                {
                    EpisodeId   = id,
                    SeriesName  = episode.Series?.Name ?? episode.SeriesName ?? string.Empty,
                    SeasonNumber  = episode.ParentIndexNumber ?? 0,
                    EpisodeNumber = episode.IndexNumber ?? 0,
                    EpisodeName   = episode.Name,
                    FilePath      = episode.Path,
                    AddedUtc      = DateTime.UtcNow
                };

                _pending[id] = entry;
                ScheduleSave();
                _logger.Debug($"Tracer: tracking new episode '{entry.SeriesName} S{entry.SeasonNumber:D2}E{entry.EpisodeNumber:D2}'");
            }
            catch (Exception ex)
            {
                _logger.ErrorException($"Tracer: error tracking episode '{episode.Name}'", ex);
            }
        }

        /// <summary>Called when detection has successfully run for an episode.</summary>
        public void MarkDetected(string episodeId)
        {
            try
            {
                if (_pending.TryRemove(episodeId, out var entry))
                {
                    entry.DetectedUtc = DateTime.UtcNow;
                    _detected[episodeId] = entry;

                    // Trim detected list to MaxDetectedEntries
                    var overflow = _detected.Count - MaxDetectedEntries;
                    if (overflow > 0)
                    {
                        var oldest = _detected.Values
                            .OrderBy(e => e.DetectedUtc)
                            .Take(overflow)
                            .Select(e => e.EpisodeId)
                            .ToList();
                        foreach (var id in oldest)
                            _detected.TryRemove(id, out _);
                    }

                    ScheduleSave();
                    _logger.Debug($"Tracer: moved episode {episodeId} to detected history");
                }
            }
            catch (Exception ex)
            {
                _logger.ErrorException($"Tracer: error marking episode {episodeId} as detected", ex);
            }
        }

        /// <summary>Clear the detected history list.</summary>
        public void ClearDetected()
        {
            _detected.Clear();
            ScheduleSave();
        }

        public List<TracerEntry> GetAllDetected() =>
            _detected.Values.OrderByDescending(e => e.DetectedUtc).ToList();

        /// <summary>Remove a single entry by ID (e.g. user dismissed it).</summary>
        public bool Remove(string episodeId)
        {
            var removed = _pending.TryRemove(episodeId, out _);
            if (removed) ScheduleSave();
            return removed;
        }

        /// <summary>Clear all pending entries.</summary>
        public void Clear()
        {
            _pending.Clear();
            ScheduleSave();
        }

        public List<TracerEntry> GetAll() =>
            _pending.Values.OrderBy(e => e.SeriesName).ThenBy(e => e.SeasonNumber).ThenBy(e => e.EpisodeNumber).ToList();

        public int Count => _pending.Count;

        // ------------------------------------------------------------------ //
        //  Persistence
        // ------------------------------------------------------------------ //

        private void Load()
        {
            try
            {
                if (File.Exists(_stateFilePath))
                {
                    var json = File.ReadAllText(_stateFilePath);
                    var list = JsonSerializer.Deserialize<List<TracerEntry>>(json);
                    if (list != null)
                    {
                        var dict = new ConcurrentDictionary<string, TracerEntry>(StringComparer.OrdinalIgnoreCase);
                        foreach (var entry in list.Where(e => !string.IsNullOrEmpty(e.EpisodeId)))
                            dict[entry.EpisodeId] = entry;
                        _pending = dict;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Warn($"Tracer: could not load pending state file ({ex.Message}), starting fresh");
            }

            try
            {
                if (File.Exists(_detectedFilePath))
                {
                    var json = File.ReadAllText(_detectedFilePath);
                    var list = JsonSerializer.Deserialize<List<TracerEntry>>(json);
                    if (list != null)
                    {
                        var dict = new ConcurrentDictionary<string, TracerEntry>(StringComparer.OrdinalIgnoreCase);
                        foreach (var entry in list.Where(e => !string.IsNullOrEmpty(e.EpisodeId)))
                            dict[entry.EpisodeId] = entry;
                        _detected = dict;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Warn($"Tracer: could not load detected state file ({ex.Message}), starting fresh");
            }
        }

        protected override void FlushSave()
        {
            lock (_lock)
            {
                try
                {
                    var dir = Path.GetDirectoryName(_stateFilePath);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                        Directory.CreateDirectory(dir);

                    File.WriteAllText(_stateFilePath,
                        JsonSerializer.Serialize(_pending.Values.ToList(), _jsonOptions));
                    File.WriteAllText(_detectedFilePath,
                        JsonSerializer.Serialize(_detected.Values.ToList(), _jsonOptions));
                }
                catch (Exception ex)
                {
                    _logger.Warn($"Tracer: could not save state file: {ex.Message}");
                }
            }
        }

    }

    public class TracerEntry
    {
        public string EpisodeId    { get; set; } = string.Empty;
        public string SeriesName   { get; set; } = string.Empty;
        public int    SeasonNumber  { get; set; }
        public int    EpisodeNumber { get; set; }
        public string EpisodeName  { get; set; } = string.Empty;
        public string FilePath     { get; set; } = string.Empty;
        public DateTime AddedUtc   { get; set; } = DateTime.UtcNow;
        public DateTime? DetectedUtc { get; set; }
    }
}
