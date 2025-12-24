using MediaBrowser.Controller.MediaEncoding;
using System;
using System.IO;

namespace EmbyCredits.Services.Utilities
{

    public static class FFmpegHelper
    {
        private static string? _customTempPath;
        private static IFfmpegManager? _ffmpegManager;

        public static void Initialize(IFfmpegManager ffmpegManager)
        {
            _ffmpegManager = ffmpegManager ?? throw new ArgumentNullException(nameof(ffmpegManager));
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
