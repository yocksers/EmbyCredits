using System;
using System.IO;
using System.Text.RegularExpressions;
using EmbyCredits.Api;

namespace EmbyCredits.Services
{
    public partial class CreditsDetectionApiService
    {
        private static readonly Regex ValidResourceNamePattern = new Regex(@"^[A-Za-z0-9._-]+$", RegexOptions.Compiled);

        private static bool IsValidResourceName(string name)
        {
            return ValidResourceNamePattern.IsMatch(name) && !name.Contains("..");
        }

        private static bool IsSafeFileName(string name)
        {
            if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                return false;

            if (name.Contains("..") || name.Contains('/') || name.Contains('\\'))
                return false;

            return string.Equals(name, Path.GetFileName(name), StringComparison.Ordinal);
        }

        public Stream Get(GetImageRequest request)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(request.ImageName) || !IsValidResourceName(request.ImageName))
                {
                    _logger.Warn($"Image request with invalid ImageName: {request.ImageName}");
                    return Stream.Null;
                }

                var assembly = typeof(Plugin).Assembly;
                var resourceName = $"EmbyCredits.Images.{request.ImageName}";

                var stream = assembly.GetManifestResourceStream(resourceName);

                if (stream == null)
                {
                    _logger.Warn($"Image not found: {resourceName}");
                    return Stream.Null;
                }

                return stream;
            }
            catch (Exception ex)
            {
                _logger?.ErrorException($"Error getting image: {request.ImageName}", ex);
                return Stream.Null;
            }
        }

        public Stream Get(GetThumbnailRequest request)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(request.ThumbnailId) || !IsSafeFileName(request.ThumbnailId))
                {
                    _logger?.Warn($"Thumbnail request with invalid ThumbnailId: {request.ThumbnailId}");
                    return Stream.Null;
                }

                var pluginDataPath = Plugin.Instance?.AppPaths?.PluginConfigurationsPath;
                if (string.IsNullOrEmpty(pluginDataPath))
                {
                    _logger?.Warn("Plugin data path not available");
                    return Stream.Null;
                }

                var thumbnailDir = Path.GetFullPath(Path.Combine(pluginDataPath, "EmbyCredits", "Thumbnails"));
                var thumbnailPath = Path.GetFullPath(Path.Combine(thumbnailDir, request.ThumbnailId));

                if (!thumbnailPath.StartsWith(thumbnailDir + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                {
                    _logger?.Warn($"Rejected thumbnail path outside of thumbnail directory: {request.ThumbnailId}");
                    return Stream.Null;
                }

                if (!File.Exists(thumbnailPath))
                {
                    _logger?.Warn($"Thumbnail not found: {thumbnailPath}");
                    return Stream.Null;
                }

                return new FileStream(thumbnailPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            }
            catch (Exception ex)
            {
                _logger?.ErrorException($"Error getting thumbnail: {request.ThumbnailId}", ex);
                return Stream.Null;
            }
        }
    }
}
