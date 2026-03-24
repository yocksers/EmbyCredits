using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EmbyCredits.Services
{
    /// <summary>
    /// Tracks episodes that have been added to the library but not yet processed
    /// by detection. Used by the "Only process new episodes" scheduled task mode.
    /// Thread-safe; persists to a JSON file so entries survive restarts.
    /// </summary>
    public class PendingEpisodesService
    {
        private readonly ILogger _logger;
        private readonly string _stateFilePath;
        private readonly object _lock = new object();

        private ConcurrentDictionary<string, PendingEntry> _pending =
            new ConcurrentDictionary<string, PendingEntry>(StringComparer.OrdinalIgnoreCase);

        private static readonly JsonSerializerOptions _jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        public PendingEpisodesService(ILogger logger, string dataFolderPath)
        {
            _logger = logger;
            _stateFilePath = Path.Combine(dataFolderPath, "pending_episodes.json");
            Load();
        }

        // ------------------------------------------------------------------ //
        //  Public API
        // ------------------------------------------------------------------ //

        /// <summary>Called when a new episode file is added to the library.</summary>
        public void TrackEpisode(Episode episode)
        {
            try
            {
                var id = episode.Id.ToString();
                _pending[id] = new PendingEntry
                {
                    EpisodeId     = id,
                    SeriesName    = episode.Series?.Name ?? episode.SeriesName ?? string.Empty,
                    SeasonNumber  = episode.ParentIndexNumber ?? 0,
                    EpisodeNumber = episode.IndexNumber ?? 0,
                    EpisodeName   = episode.Name,
                    FilePath      = episode.Path,
                    AddedUtc      = DateTime.UtcNow
                };
                Save();
                _logger.Debug($"Pending: tracking new episode '{episode.SeriesName} S{episode.ParentIndexNumber:D2}E{episode.IndexNumber:D2}'");
            }
            catch (Exception ex)
            {
                _logger.ErrorException($"Pending: error tracking episode '{episode.Name}'", ex);
            }
        }

        /// <summary>Called after detection has run (success or failure).</summary>
        public void MarkProcessed(string episodeId)
        {
            try
            {
                if (_pending.TryRemove(episodeId, out _))
                {
                    Save();
                    _logger.Debug($"Pending: removed episode {episodeId} (detection complete)");
                }
            }
            catch (Exception ex)
            {
                _logger.ErrorException($"Pending: error marking episode {episodeId} as processed", ex);
            }
        }

        /// <summary>Returns all pending episode IDs.</summary>
        public List<string> GetPendingEpisodeIds()
        {
            return _pending.Keys.ToList();
        }

        public int GetCount() => _pending.Count;

        public void Clear()
        {
            _pending.Clear();
            Save();
        }

        // ------------------------------------------------------------------ //
        //  Persistence
        // ------------------------------------------------------------------ //

        private void Load()
        {
            try
            {
                if (!File.Exists(_stateFilePath))
                    return;

                var json = File.ReadAllText(_stateFilePath);
                var entries = JsonSerializer.Deserialize<List<PendingEntry>>(json, _jsonOptions);
                if (entries == null) return;

                _pending = new ConcurrentDictionary<string, PendingEntry>(
                    entries.Where(e => !string.IsNullOrEmpty(e.EpisodeId))
                           .Select(e => new KeyValuePair<string, PendingEntry>(e.EpisodeId!, e)),
                    StringComparer.OrdinalIgnoreCase);

                _logger.Info($"Pending: loaded {_pending.Count} pending episode(s) from state file");
            }
            catch (Exception ex)
            {
                _logger.ErrorException("Pending: error loading state file", ex);
            }
        }

        private void Save()
        {
            lock (_lock)
            {
                try
                {
                    var entries = _pending.Values.OrderBy(e => e.AddedUtc).ToList();
                    var dir = Path.GetDirectoryName(_stateFilePath);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                        Directory.CreateDirectory(dir);
                    File.WriteAllText(_stateFilePath, JsonSerializer.Serialize(entries, _jsonOptions));
                }
                catch (Exception ex)
                {
                    _logger.ErrorException("Pending: error saving state file", ex);
                }
            }
        }

        // ------------------------------------------------------------------ //
        //  State model
        // ------------------------------------------------------------------ //

        private class PendingEntry
        {
            public string? EpisodeId     { get; set; }
            public string? SeriesName    { get; set; }
            public int     SeasonNumber  { get; set; }
            public int     EpisodeNumber { get; set; }
            public string? EpisodeName   { get; set; }
            public string? FilePath      { get; set; }
            public DateTime AddedUtc     { get; set; }
        }
    }
}
