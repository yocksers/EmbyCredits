using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.MediaInfo;
using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace EmbyCredits.Services.Utilities
{

    public static class FFmpegHelper
    {
        private static string? _customTempPath;
        private static IFfmpegManager? _ffmpegManager;
        private static IMediaEncoder? _mediaEncoder;

        public static string NormalizeFilePath(string path)
        {
            if (string.IsNullOrEmpty(path))
                return path;

            if (path.StartsWith("smb://"))
            {

                if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
                    System.Runtime.InteropServices.OSPlatform.Windows))
                {

                    var uncPath = path.Substring(6);

                    uncPath = uncPath.Replace('/', '\\');

                    uncPath = "\\\\" + uncPath;

                    return uncPath;
                }
                else
                {

                    var smbPath = path.Substring(6);
                    var pathParts = smbPath.Split('/');

                    if (pathParts.Length >= 2)
                    {
                        var server = pathParts[0];
                        var remainingPath = string.Join("/", pathParts.Skip(1));

                        var mountPatterns = new[]
                        {
                            $"/mnt/{server}/{remainingPath}",
                            $"/media/{server}/{remainingPath}",
                            $"/mnt/smb/{remainingPath}",
                            $"/media/smb/{remainingPath}",
                            $"/mnt/nas/{remainingPath}",
                            $"/media/nas/{remainingPath}"
                        };

                        foreach (var mountPath in mountPatterns)
                        {
                            if (File.Exists(mountPath))
                            {
                                return mountPath;
                            }
                        }
                    }

                    return path;
                }
            }

            return path;
        }

        public static void Initialize(IFfmpegManager ffmpegManager, IMediaEncoder mediaEncoder)
        {
            _ffmpegManager = ffmpegManager ?? throw new ArgumentNullException(nameof(ffmpegManager));
            _mediaEncoder = mediaEncoder ?? throw new ArgumentNullException(nameof(mediaEncoder));
        }

        public static void SetCustomTempPath(string? customPath)
        {
            _customTempPath = customPath;
        }

        public static string GetTempPath()
        {
            if (!string.IsNullOrWhiteSpace(_customTempPath) && Directory.Exists(_customTempPath))
            {
                return _customTempPath;
            }
            return Path.GetTempPath();
        }

        public static string GetFfmpegPath()
        {
            if (_ffmpegManager == null)
                throw new InvalidOperationException("FFmpegHelper not initialized");

            var config = _ffmpegManager.FfmpegConfiguration;
            if (config == null || string.IsNullOrEmpty(config.EncoderPath) || !File.Exists(config.EncoderPath))
            {
                throw new FileNotFoundException("FFmpeg encoder not found");
            }

            return config.EncoderPath;
        }

        public static string GetFfprobePath()
        {
            if (_ffmpegManager == null)
                throw new InvalidOperationException("FFmpegHelper not initialized");

            var config = _ffmpegManager.FfmpegConfiguration;
            if (config == null || string.IsNullOrEmpty(config.ProbePath) || !File.Exists(config.ProbePath))
            {
                throw new FileNotFoundException("FFprobe not found");
            }

            return config.ProbePath;
        }

        public static string GetInputArgument(string path)
        {
            if (_mediaEncoder == null)
            {
                return NormalizeFilePath(path);
            }

            var normalizedPath = NormalizeFilePath(path);
            
            MediaProtocol protocol;
            if (normalizedPath.StartsWith("smb://", StringComparison.OrdinalIgnoreCase))
            {
                protocol = MediaProtocol.File;
            }
            else if (normalizedPath.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                     normalizedPath.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                protocol = MediaProtocol.Http;
            }
            else if (normalizedPath.StartsWith("rtsp://", StringComparison.OrdinalIgnoreCase))
            {
                protocol = MediaProtocol.Rtsp;
            }
            else
            {
                protocol = MediaProtocol.File;
            }

            return _mediaEncoder.GetInputArgument(normalizedPath.AsSpan(), protocol);
        }

        public static int CleanupOrphanedTempDirectories()
        {
            var deletedCount = 0;
            try
            {
                var tempPath = GetTempPath();
                var directories = Directory.GetDirectories(tempPath, "ocr_frames_*");

                foreach (var dir in directories)
                {
                    try
                    {
                        var dirInfo = new DirectoryInfo(dir);

                        if (dirInfo.Exists && (DateTime.Now - dirInfo.CreationTime).TotalHours > 1)
                        {
                            Directory.Delete(dir, true);
                            deletedCount++;
                        }
                    }
                    catch
                    {

                    }
                }
            }
            catch
            {

            }
            return deletedCount;
        }
    }
}
