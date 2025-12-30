using MediaBrowser.Model.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace EmbyCredits.Services
{
    public class ProcessedFilesTracker
    {
        private readonly ILogger _logger;
        private readonly string _trackingFilePath;
        private ConcurrentDictionary<string, ProcessedFileEntry> _processedFiles;
        private readonly object _fileLock = new object();

        public ProcessedFilesTracker(ILogger logger, string trackingDirectory)
        {
            _logger = logger;
            
            if (!Directory.Exists(trackingDirectory))
            {
                Directory.CreateDirectory(trackingDirectory);
            }
            
            _trackingFilePath = Path.Combine(trackingDirectory, "processed_files.json");
            _processedFiles = new ConcurrentDictionary<string, ProcessedFileEntry>();
            
            LoadFromFile();
        }

        public bool ShouldSkipFile(string episodeId, bool skipOnlySuccessful)
        {
            if (!_processedFiles.TryGetValue(episodeId, out var entry))
            {
                return false;
            }

            if (skipOnlySuccessful)
            {
                return entry.Success;
            }

            return true;
        }

        public void MarkFileProcessed(string episodeId, bool success, double? timestamp = null)
        {
            var entry = new ProcessedFileEntry
            {
                EpisodeId = episodeId,
                ProcessedDate = DateTime.UtcNow,
                Success = success,
                Timestamp = timestamp
            };

            _processedFiles.AddOrUpdate(episodeId, entry, (key, existing) => entry);
            SaveToFile();
        }

        public void RemoveFile(string episodeId)
        {
            _processedFiles.TryRemove(episodeId, out _);
            SaveToFile();
        }

        public void Clear()
        {
            _processedFiles.Clear();
            SaveToFile();
        }

        public int GetProcessedCount()
        {
            return _processedFiles.Count;
        }

        public int GetSuccessCount()
        {
            return _processedFiles.Values.Count(e => e.Success);
        }

        public int GetFailedCount()
        {
            return _processedFiles.Values.Count(e => !e.Success);
        }

        private void LoadFromFile()
        {
            try
            {
                if (!File.Exists(_trackingFilePath))
                {
                    _logger.Info($"Processed files tracking file not found at {_trackingFilePath}, starting fresh");
                    return;
                }

                lock (_fileLock)
                {
                    var json = File.ReadAllText(_trackingFilePath);
                    var entries = JsonSerializer.Deserialize<List<ProcessedFileEntry>>(json);
                    
                    if (entries != null)
                    {
                        _processedFiles = new ConcurrentDictionary<string, ProcessedFileEntry>(
                            entries.ToDictionary(e => e.EpisodeId, e => e));
                        _logger.Info($"Loaded {_processedFiles.Count} processed file entries from {_trackingFilePath}");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.ErrorException($"Error loading processed files from {_trackingFilePath}", ex);
                _processedFiles = new ConcurrentDictionary<string, ProcessedFileEntry>();
            }
        }

        private void SaveToFile()
        {
            try
            {
                lock (_fileLock)
                {
                    var entries = _processedFiles.Values.ToList();
                    var json = JsonSerializer.Serialize(entries, new JsonSerializerOptions 
                    { 
                        WriteIndented = true 
                    });
                    File.WriteAllText(_trackingFilePath, json);
                }
            }
            catch (Exception ex)
            {
                _logger.ErrorException($"Error saving processed files to {_trackingFilePath}", ex);
            }
        }
    }

    public class ProcessedFileEntry
    {
        public string EpisodeId { get; set; } = string.Empty;
        public DateTime ProcessedDate { get; set; }
        public bool Success { get; set; }
        public double? Timestamp { get; set; }
    }
}
