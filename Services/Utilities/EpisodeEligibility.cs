using System;
using System.IO;

namespace EmbyCredits.Services.Utilities
{
    public static class EpisodeEligibility
    {
        public static bool IsStrmPath(string? path)
        {
            return !string.IsNullOrWhiteSpace(path) &&
                string.Equals(Path.GetExtension(path), ".strm", StringComparison.OrdinalIgnoreCase);
        }

        public static bool ShouldSkipMediaProcessing(string? path, bool skipStrmEpisodes)
        {
            return skipStrmEpisodes && IsStrmPath(path);
        }
    }
}