using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Logging;
using System;
using System.Collections.Generic;
using System.Linq;

namespace EmbyCredits.Services.Utilities
{
    public static class ItemLookupHelper
    {
        /// <summary>
        /// Determines if an episode is a TV special (Season 0 or no season).
        /// TV specials are excluded from credits detection.
        /// </summary>
        public static bool IsSpecialEpisode(Episode episode)
        {
            return episode.ParentIndexNumber == null || episode.ParentIndexNumber == 0;
        }

        public static BaseItem? GetItemById(ILibraryManager libraryManager, string itemId, ILogger? logger = null)
        {
            if (string.IsNullOrEmpty(itemId))
            {
                return null;
            }

            if (Guid.TryParse(itemId, out Guid itemGuid))
            {
                return libraryManager.GetItemById(itemGuid);
            }

            if (long.TryParse(itemId, out long internalId))
            {
                return libraryManager.GetItemById(internalId);
            }

            logger?.Warn($"Invalid ItemId format: {itemId}");
            return null;
        }
        public static List<Episode> GetSeriesEpisodes(ILibraryManager libraryManager, long seriesInternalId, ILogger? logger = null)
        {
            var allEpisodes = libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { "Episode" },
                IsVirtualItem = false,
                HasPath = true,
                AncestorIds = new[] { seriesInternalId }
            }).OfType<Episode>().ToList();

            var episodes = allEpisodes.Where(e => !IsSpecialEpisode(e)).ToList();
            var specialCount = allEpisodes.Count - episodes.Count;
            
            logger?.Info($"Found {episodes.Count} episodes for series InternalId: {seriesInternalId} (excluded {specialCount} specials)");
            return episodes;
        }
        public static List<Episode> GetLibraryEpisodes(ILibraryManager libraryManager, long libraryId, ILogger? logger = null)
        {
            var allEpisodes = libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { "Episode" },
                IsVirtualItem = false,
                HasPath = true,
                AncestorIds = new[] { libraryId }
            }).OfType<Episode>().ToList();

            var episodes = allEpisodes.Where(e => !IsSpecialEpisode(e)).ToList();
            var specialCount = allEpisodes.Count - episodes.Count;
            
            logger?.Info($"Found {episodes.Count} episodes for library InternalId: {libraryId} (excluded {specialCount} specials)");
            return episodes;
        }
        public static BaseItem? ResolveSeries(ILibraryManager libraryManager, string seriesId, ILogger? logger = null)
        {
            if (string.IsNullOrEmpty(seriesId))
            {
                return null;
            }

            Guid seriesGuid;
            if (!Guid.TryParse(seriesId, out seriesGuid))
            {
                logger?.Info($"SeriesId '{seriesId}' is not a GUID, attempting to parse as InternalId");

                if (long.TryParse(seriesId, out long internalId))
                {
                    var series = libraryManager.GetItemById(internalId);
                    if (series != null)
                    {
                        logger?.Info($"Found series via InternalId {internalId}: {series.Name}");
                        return series;
                    }

                    logger?.Info($"Series not found with InternalId: {internalId}");
                    return null;
                }

                logger?.Info($"Invalid SeriesId format: {seriesId}");
                return null;
            }

            logger?.Info($"Looking up series with GUID: {seriesGuid}");
            var seriesByGuid = libraryManager.GetItemById(seriesGuid);

            if (seriesByGuid != null)
            {
                logger?.Info($"Series found: {seriesByGuid.Name}, InternalId: {seriesByGuid.InternalId}");
            }
            else
            {
                logger?.Info($"Series not found for GUID: {seriesGuid}");
            }

            return seriesByGuid;
        }
    }
}
