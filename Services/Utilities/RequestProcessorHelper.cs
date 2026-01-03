using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Logging;
using System;
using System.Collections.Generic;

namespace EmbyCredits.Services.Utilities
{
    public static class RequestProcessorHelper
    {
        public static ProcessResult ProcessDetectionRequest(
            ILibraryManager libraryManager,
            string? episodeId,
            string? seriesId,
            string? libraryId,
            Action<Episode> processEpisode,
            Action<List<Episode>> processSeries,
            ILogger? logger = null)
        {
            try
            {

                if (!string.IsNullOrEmpty(episodeId))
                {
                    return ProcessSingleEpisode(libraryManager, episodeId, processEpisode, logger);
                }

                if (!string.IsNullOrEmpty(seriesId))
                {
                    return ProcessSeries(libraryManager, seriesId, processSeries, logger);
                }

                if (!string.IsNullOrEmpty(libraryId))
                {
                    return ProcessLibrary(libraryManager, libraryId, processSeries, logger);
                }

                return new ProcessResult
                {
                    Success = false,
                    Message = "Either LibraryId, SeriesId, or EpisodeId is required"
                };
            }
            catch (Exception ex)
            {
                logger?.ErrorException("Error processing detection request", ex);
                return new ProcessResult { Success = false, Message = ex.Message };
            }
        }

        private static ProcessResult ProcessSingleEpisode(
            ILibraryManager libraryManager,
            string episodeId,
            Action<Episode> processEpisode,
            ILogger? logger)
        {
            logger?.Info($"Processing single episode: {episodeId}");

            var item = ItemLookupHelper.GetItemById(libraryManager, episodeId, logger);
            if (item == null)
            {
                return new ProcessResult { Success = false, Message = "Episode not found" };
            }

            if (item is not Episode episode)
            {
                return new ProcessResult { Success = false, Message = "Item is not an episode" };
            }

            if (ItemLookupHelper.IsSpecialEpisode(episode))
            {
                return new ProcessResult 
                { 
                    Success = false, 
                    Message = $"Episode '{episode.Name}' is a TV special (Season 0) and will not be processed" 
                };
            }

            logger?.Info($"Episode found: {episode.Name}");
            processEpisode(episode);

            return new ProcessResult
            {
                Success = true,
                Message = $"Queued episode: {episode.Name}",
                ItemCount = 1
            };
        }

        private static ProcessResult ProcessSeries(
            ILibraryManager libraryManager,
            string seriesId,
            Action<List<Episode>> processSeries,
            ILogger? logger)
        {
            logger?.Info($"Processing series: {seriesId}");

            var series = ItemLookupHelper.ResolveSeries(libraryManager, seriesId, logger);
            if (series == null)
            {
                return new ProcessResult { Success = false, Message = "Series not found" };
            }

            var episodes = ItemLookupHelper.GetSeriesEpisodes(libraryManager, series.InternalId, logger);

            if (episodes.Count == 0)
            {
                return new ProcessResult
                {
                    Success = true,
                    Message = $"No episodes found for series: {series.Name}",
                    ItemCount = 0
                };
            }

            processSeries(episodes);

            return new ProcessResult
            {
                Success = true,
                Message = $"Queued {episodes.Count} episodes from {series.Name}",
                ItemCount = episodes.Count
            };
        }

        private static ProcessResult ProcessLibrary(
            ILibraryManager libraryManager,
            string libraryId,
            Action<List<Episode>> processSeries,
            ILogger? logger)
        {
            logger?.Info($"Processing library: {libraryId}");

            if (!long.TryParse(libraryId, out long libraryInternalId))
            {
                return new ProcessResult
                {
                    Success = false,
                    Message = "Invalid LibraryId format - must be InternalId"
                };
            }

            var library = libraryManager.GetItemById(libraryInternalId);
            if (library == null)
            {
                return new ProcessResult { Success = false, Message = "Library not found" };
            }

            logger?.Info($"Library found: {library.Name}, InternalId: {libraryInternalId}");

            var episodes = ItemLookupHelper.GetLibraryEpisodes(libraryManager, libraryInternalId, logger);

            if (episodes.Count == 0)
            {
                return new ProcessResult
                {
                    Success = true,
                    Message = $"No episodes found in library: {library.Name}",
                    ItemCount = 0
                };
            }

            processSeries(episodes);

            return new ProcessResult
            {
                Success = true,
                Message = $"Queued {episodes.Count} episodes from library {library.Name}",
                ItemCount = episodes.Count
            };
        }
    }
    public class ProcessResult
    {
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;
        public int ItemCount { get; set; }
    }
}
