using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Logging;
using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Web;

namespace EmbyCredits.Services
{
    public class TheIntroDbService : IDisposable
    {
        private const string ApiBase = "https://api.theintrodb.org/v3/media";

        private readonly ILogger _logger;
        private readonly PluginConfiguration _configuration;
        private readonly HttpClient _httpClient;
        private bool _disposed;

        public TheIntroDbService(ILogger logger, PluginConfiguration configuration)
        {
            _logger = logger;
            _configuration = configuration;
            _httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(10)
            };
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "EmbyCreditsPlugin/1.0");
        }

        public async Task<double?> GetCreditsTimestamp(Episode episode, CancellationToken cancellationToken = default)
        {
            try
            {
                var series = episode.Series;
                if (series == null)
                    return null;

                int? seasonNumber = episode.ParentIndexNumber;
                int? episodeNumber = episode.IndexNumber;

                if (!seasonNumber.HasValue || !episodeNumber.HasValue)
                    return null;

                string? tmdbId = GetProviderId(series.ProviderIds, "Tmdb");
                string? tvdbId = GetProviderId(series.ProviderIds, "Tvdb");
                string? imdbId = GetProviderId(series.ProviderIds, "Imdb");

                if (string.IsNullOrEmpty(tmdbId) && string.IsNullOrEmpty(tvdbId) && string.IsNullOrEmpty(imdbId))
                {
                    if (_configuration.EnableDetailedLogging)
                        _logger.Debug($"[TheIntroDB] No provider IDs found for series '{series.Name}', skipping lookup");
                    return null;
                }

                long? durationMs = null;
                if (episode.RunTimeTicks.HasValue && episode.RunTimeTicks.Value > 0)
                    durationMs = episode.RunTimeTicks.Value / TimeSpan.TicksPerMillisecond;

                var query = HttpUtility.ParseQueryString(string.Empty);

                if (!string.IsNullOrEmpty(tmdbId))
                    query["tmdb_id"] = tmdbId;
                else if (!string.IsNullOrEmpty(tvdbId))
                    query["tvdb_id"] = tvdbId;
                else
                    query["imdb_id"] = imdbId;

                query["season"] = seasonNumber.Value.ToString();
                query["episode"] = episodeNumber.Value.ToString();

                if (durationMs.HasValue)
                    query["duration_ms"] = durationMs.Value.ToString();

                var url = $"{ApiBase}?{query}";

                if (_configuration.EnableDetailedLogging)
                    _logger.Debug($"[TheIntroDB] Querying: {url}");

                using var response = await _httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    if (_configuration.EnableDetailedLogging)
                        _logger.Debug($"[TheIntroDB] API returned {(int)response.StatusCode} for '{series.Name}' S{seasonNumber}E{episodeNumber}");
                    return null;
                }

                var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                return ParseCreditsTimestamp(json, series.Name, seasonNumber.Value, episodeNumber.Value);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            catch (HttpRequestException ex)
            {
                if (_configuration.EnableDetailedLogging)
                    _logger.Debug($"[TheIntroDB] HTTP error for '{episode.SeriesName}': {ex.Message}");
                return null;
            }
            catch (Exception ex)
            {
                _logger.Warn($"[TheIntroDB] Unexpected error for '{episode.SeriesName}': {ex.Message}");
                return null;
            }
        }

        private double? ParseCreditsTimestamp(string json, string seriesName, int season, int episode)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (!root.TryGetProperty("credits", out var creditsArray))
                    return null;

                if (creditsArray.ValueKind != JsonValueKind.Array)
                    return null;

                foreach (var segment in creditsArray.EnumerateArray())
                {
                    if (!segment.TryGetProperty("start_ms", out var startMsElement))
                        continue;

                    if (startMsElement.ValueKind == JsonValueKind.Null)
                        continue;

                    if (!startMsElement.TryGetInt64(out var startMs))
                        continue;

                    if (startMs <= 0)
                        continue;

                    var timestampSeconds = startMs / 1000.0;

                    if (_configuration.EnableDetailedLogging)
                        _logger.Debug($"[TheIntroDB] Found credits at {timestampSeconds:F1}s for '{seriesName}' S{season:00}E{episode:00}");

                    return timestampSeconds;
                }

                if (_configuration.EnableDetailedLogging)
                    _logger.Debug($"[TheIntroDB] No usable credits segment for '{seriesName}' S{season:00}E{episode:00}");

                return null;
            }
            catch (JsonException ex)
            {
                if (_configuration.EnableDetailedLogging)
                    _logger.Debug($"[TheIntroDB] JSON parse error: {ex.Message}");
                return null;
            }
        }

        private static string? GetProviderId(MediaBrowser.Model.Entities.ProviderIdDictionary? ids, string provider) =>
            ids?.TryGetValue(provider, out var val) == true ? val : null;

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            _httpClient?.Dispose();
        }
    }
}
