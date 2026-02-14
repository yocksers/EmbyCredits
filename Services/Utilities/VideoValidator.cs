using MediaBrowser.Model.Logging;
using System;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace EmbyCredits.Services.Utilities
{
    public class VideoValidator
    {
        private readonly ILogger _logger;
        private readonly PluginConfiguration _configuration;

        public VideoValidator(ILogger logger, PluginConfiguration configuration)
        {
            _logger = logger;
            _configuration = configuration;
        }

        public async Task<(bool isValid, string errorMessage)> ValidateVideo(string videoPath, CancellationToken cancellationToken = default)
        {
            if (!_configuration.EnableVideoValidation)
            {
                return (true, string.Empty);
            }

            try
            {
                var ffprobePath = FFmpegHelper.GetFfprobePath();
                var normalizedPath = FFmpegHelper.NormalizeFilePath(videoPath);

                var arguments = $"-v error -select_streams v:0 -count_packets -show_entries stream=codec_name,codec_type,duration -of default=noprint_wrappers=1 \"{normalizedPath}\"";

                var processStartInfo = new ProcessStartInfo
                {
                    FileName = ffprobePath,
                    Arguments = arguments,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };

                var outputBuilder = new StringBuilder();
                var errorBuilder = new StringBuilder();
                var timeoutSeconds = _configuration.VideoValidationTimeoutSeconds;

                using (var process = new Process { StartInfo = processStartInfo })
                {
                    process.OutputDataReceived += (sender, e) =>
                    {
                        if (!string.IsNullOrEmpty(e.Data))
                        {
                            outputBuilder.AppendLine(e.Data);
                        }
                    };

                    process.ErrorDataReceived += (sender, e) =>
                    {
                        if (!string.IsNullOrEmpty(e.Data))
                        {
                            errorBuilder.AppendLine(e.Data);
                        }
                    };

                    FFmpegHelper.RegisterProcess(process, $"Video validation: {videoPath}");

                    try
                    {
                        process.Start();
                        process.BeginOutputReadLine();
                        process.BeginErrorReadLine();

                        var completedInTime = await Task.Run(() =>
                        {
                            return process.WaitForExit(timeoutSeconds * 1000);
                        }, cancellationToken);

                        if (!completedInTime)
                        {
                            try
                            {
                                process.Kill();
                            }
                            catch { }

                            _logger?.Warn($"Video validation timed out after {timeoutSeconds} seconds for: {videoPath}");
                            return (false, $"Validation timed out after {timeoutSeconds} seconds");
                        }

                        var exitCode = process.ExitCode;
                        var output = outputBuilder.ToString();
                        var errors = errorBuilder.ToString();

                        if (exitCode != 0)
                        {
                            var errorMsg = !string.IsNullOrEmpty(errors) ? errors.Trim() : "Unknown error";
                            _logger?.Warn($"Video validation failed for {videoPath}: {errorMsg}");
                            return (false, $"FFprobe error: {errorMsg}");
                        }

                        if (!string.IsNullOrEmpty(errors))
                        {
                            if (errors.Contains("Invalid data found", StringComparison.OrdinalIgnoreCase) ||
                                errors.Contains("moov atom not found", StringComparison.OrdinalIgnoreCase) ||
                                errors.Contains("could not find codec", StringComparison.OrdinalIgnoreCase) ||
                                errors.Contains("error", StringComparison.OrdinalIgnoreCase))
                            {
                                _logger?.Warn($"Video appears corrupted: {videoPath} - {errors.Trim()}");
                                return (false, $"Video file appears corrupted: {errors.Trim()}");
                            }
                        }

                        if (string.IsNullOrEmpty(output) || !output.Contains("codec_name") || !output.Contains("codec_type"))
                        {
                            _logger?.Warn($"Video validation failed - no valid codec information found for: {videoPath}");
                            return (false, "No valid video codec information found");
                        }

                        _logger?.Debug($"Video validation passed for: {videoPath}");
                        return (true, string.Empty);
                    }
                    finally
                    {
                        FFmpegHelper.UnregisterProcess(process);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.ErrorException($"Exception during video validation for {videoPath}", ex);
                return (false, $"Validation exception: {ex.Message}");
            }
        }
    }
}
