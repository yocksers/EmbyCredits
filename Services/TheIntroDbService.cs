using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Logging;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using EmbyCredits.Services.Utilities;

namespace EmbyCredits.Services
{
    public class TheIntroDbService : IDisposable
    {
        private const string ApiBase = "https://api.theintrodb.org/v4/media";

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

                string? tvdbId = GetNumericProviderId(series.ProviderIds, "Tvdb");
                string? tmdbId = GetNumericProviderId(series.ProviderIds, "Tmdb");
                string? imdbId = GetProviderId(series.ProviderIds, "Imdb");

                if (string.IsNullOrEmpty(tvdbId) && string.IsNullOrEmpty(tmdbId) && string.IsNullOrEmpty(imdbId))
                {
                    if (_configuration.EnableDetailedLogging)
                        _logger.Debug($"[TheIntroDB] No provider IDs found for series '{series.Name}', skipping lookup");
                    return null;
                }

                long? durationMs = null;
                if (episode.RunTimeTicks.HasValue && episode.RunTimeTicks.Value > 0)
                    durationMs = episode.RunTimeTicks.Value / TimeSpan.TicksPerMillisecond;

                var query = HttpUtility.ParseQueryString(string.Empty);

                // TVDB is the v4 identity; tmdb_id/imdb_id are resolved server-side and slower.
                // Season/episode are read in the numbering of whichever ID is sent.
                if (!string.IsNullOrEmpty(tvdbId))
                {
                    query["tvdb_id"] = tvdbId;

                    // mapping only names TVDB orders, so it only applies to tvdb_id lookups
                    var mapping = GetTvdbMapping(series.DisplayOrder);
                    if (mapping != null)
                        query["mapping"] = mapping;
                }
                else if (!string.IsNullOrEmpty(tmdbId))
                    query["tmdb_id"] = tmdbId;
                else
                    query["imdb_id"] = imdbId;

                query["season"] = seasonNumber.Value.ToString();
                query["episode"] = episodeNumber.Value.ToString();

                if (durationMs.HasValue)
                    query["duration_ms"] = durationMs.Value.ToString();

                var url = $"{ApiBase}?{query}";

                if (_configuration.EnableDetailedLogging)
                    _logger.Debug($"[TheIntroDB] Querying: {url}");

                using var response = await GetWithRetryAsync(url, cancellationToken).ConfigureAwait(false);

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

        private async Task<HttpResponseMessage> GetWithRetryAsync(string url, CancellationToken cancellationToken)
        {
            const int maxAttempts = 3;
            var delayMs = 500;

            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                try
                {
                    var response = await _httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
                    if (!IsTransientStatusCode(response.StatusCode) || attempt == maxAttempts)
                        return response;

                    response.Dispose();
                }
                catch (HttpRequestException) when (attempt < maxAttempts)
                {
                }
                catch (TaskCanceledException) when (attempt < maxAttempts && !cancellationToken.IsCancellationRequested)
                {
                }

                if (_configuration.EnableDetailedLogging)
                    _logger.Debug($"[TheIntroDB] Retrying request (attempt {attempt + 1}/{maxAttempts}) after {delayMs}ms");

                await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
                delayMs *= 2;
            }

            return await _httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
        }

        private static bool IsTransientStatusCode(System.Net.HttpStatusCode statusCode)
        {
            var code = (int)statusCode;
            return code == 429 || (code >= 500 && code <= 599);
        }

        private double? ParseCreditsTimestamp(string json, string seriesName, int season, int episode)
        {
            try
            {
                var root = SimpleJson.Parse(json) as Dictionary<string, object?>;
                if (root == null || !root.TryGetValue("credits", out var creditsValue))
                    return null;

                if (creditsValue is not List<object?> creditsArray)
                    return null;

                foreach (var segment in creditsArray)
                {
                    if (segment is not Dictionary<string, object?> segmentObj)
                        continue;

                    if (!segmentObj.TryGetValue("start_ms", out var startMsValue) || startMsValue == null)
                        continue;

                    var startMs = Convert.ToInt64(startMsValue, System.Globalization.CultureInfo.InvariantCulture);
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
            catch (SimpleJsonException ex)
            {
                if (_configuration.EnableDetailedLogging)
                    _logger.Debug($"[TheIntroDB] JSON parse error: {ex.Message}");
                return null;
            }
        }

        private static string? GetProviderId(MediaBrowser.Model.Entities.ProviderIdDictionary? ids, string provider) =>
            ids?.TryGetValue(provider, out var val) == true ? val : null;

        // v4 rejects non-integer tvdb_id/tmdb_id with 422, so skip malformed IDs and try the next provider
        private static string? GetNumericProviderId(MediaBrowser.Model.Entities.ProviderIdDictionary? ids, string provider)
        {
            var val = GetProviderId(ids, provider)?.Trim();
            return long.TryParse(val, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var id) && id > 0
                ? val
                : null;
        }

        // Aired order is TVDB's default, so no mapping is sent for it; unknown values are omitted
        // because v4 rejects an unknown mapping with 400.
        private static string? GetTvdbMapping(MediaBrowser.Model.Entities.SeriesDisplayOrder displayOrder)
        {
            switch (displayOrder)
            {
                case MediaBrowser.Model.Entities.SeriesDisplayOrder.Dvd:
                    return "dvd";
                case MediaBrowser.Model.Entities.SeriesDisplayOrder.Absolute:
                    return "absolute";
                default:
                    return null;
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            _httpClient?.Dispose();
        }
    }
}
