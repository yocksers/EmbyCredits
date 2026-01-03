using MediaBrowser.Model.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using EmbyCredits.Services.Utilities;

namespace EmbyCredits.Services.DetectionMethods
{

    public class OcrDetection : BaseDetectionMethod
    {
        public override string MethodName => "OCR Detection";
        public override double Confidence => 0.95;
        public override int Priority => Configuration.OcrDetectionPriority;
        public override bool IsEnabled => Configuration.EnableOcrDetection;

        private static readonly HttpClient _httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };

        public OcrDetection(ILogger logger, PluginConfiguration configuration)
            : base(logger, configuration)
        {
        }

        public override async Task<double> DetectCredits(string videoPath, double duration, CancellationToken cancellationToken = default)
        {
            LastError = string.Empty;
            try
            {
                if (string.IsNullOrWhiteSpace(Configuration.OcrEndpoint))
                {
                    LastError = "OCR endpoint not configured";
                    LogWarn("OCR endpoint not configured. Please set the OCR API URL in settings.");
                    return 0;
                }

                var endpointAvailable = await TestOcrEndpoint(cancellationToken).ConfigureAwait(false);
                if (!endpointAvailable)
                {
                    LastError = $"OCR endpoint {Configuration.OcrEndpoint} is not accessible";
                    LogWarn($"OCR endpoint {Configuration.OcrEndpoint} is not accessible. Skipping OCR detection.");
                    return 0;
                }

                LogDebug("Analyzing video for OCR-based credits detection...");
                UpdateProgress(5, "Starting OCR detection");

                var keywords = ParseKeywords(Configuration.OcrDetectionKeywords);
                if (keywords.Count == 0)
                {
                    LastError = "No OCR keywords configured";
                    LogWarn("No keywords configured for OCR detection");
                    return 0;
                }

                LogDebug($"OCR searching for {keywords.Count} keywords: {string.Join(", ", keywords.Take(5))}{(keywords.Count > 5 ? "..." : "")}");

                double startTime;
                var searchUnit = Configuration.OcrSearchStartUnit ?? "minutes";
                var searchValue = Configuration.OcrSearchStartValue;

                if (searchUnit == "minutes")
                {
                    startTime = Math.Max(0, duration - (searchValue * 60));
                    LogDebug($"OCR starting {searchValue} minutes from end at {FormatTime(startTime)}");
                }
                else
                {

                    var searchStartPercentage = searchValue / 100.0;
                    startTime = duration * searchStartPercentage;
                    LogDebug($"OCR starting at {searchValue}% ({FormatTime(startTime)})");
                }

                var analysisDuration = duration - startTime;

                if (Configuration.OcrStopSecondsFromEnd > 0)
                {
                    var stopTime = duration - Configuration.OcrStopSecondsFromEnd;
                    if (startTime + analysisDuration > stopTime)
                    {
                        analysisDuration = Math.Max(0, stopTime - startTime);
                        LogDebug($"Stopping analysis {Configuration.OcrStopSecondsFromEnd} seconds before end at {FormatTime(stopTime)}");
                    }
                }

                if (Configuration.OcrMaxAnalysisDuration > 0 && analysisDuration > Configuration.OcrMaxAnalysisDuration)
                {
                    analysisDuration = Configuration.OcrMaxAnalysisDuration;
                    LogDebug($"Limiting OCR analysis to {Configuration.OcrMaxAnalysisDuration} seconds (video has {duration - startTime:F0}s remaining)");
                }

                var tempDir = Path.Combine(FFmpegHelper.GetTempPath(), $"ocr_frames_{Guid.NewGuid()}");
                Directory.CreateDirectory(tempDir);

                try
                {
                    var fps = Configuration.OcrFrameRate;
                    var imageFormat = Configuration.OcrImageFormat?.ToLowerInvariant() == "jpg" ? "jpg" : "png";
                    var imageExtension = imageFormat;

                    var qualityParam = "";
                    if (imageFormat == "jpg")
                    {
                        var userQuality = Math.Max(1, Math.Min(100, Configuration.OcrJpegQuality));
                        var ffmpegQuality = 2 + (int)Math.Round((100 - userQuality) * 29.0 / 99.0);
                        qualityParam = $"-q:v {ffmpegQuality}";
                    }

                    var frameOutputPath = $"{tempDir.Replace("\\", "/")}/frame_%04d.{imageExtension}";

                    var ffmpegTempDir = tempDir.Replace("\\", "/");
                    var ffmpegFramePath = $"{ffmpegTempDir}/frame_%04d.{imageExtension}";
                    var extractArgs = $"-ss {startTime.ToString(CultureInfo.InvariantCulture)} -i \"{videoPath}\" -t {analysisDuration.ToString(CultureInfo.InvariantCulture)} -vf \"fps={fps.ToString(CultureInfo.InvariantCulture)}\" {qualityParam} -f image2 \"{ffmpegFramePath}\"";

                    LogDebug($"Extracting frames from {FormatTime(startTime)} at {fps} fps ({imageFormat.ToUpperInvariant()}{(imageFormat == "jpg" ? $" Q{Configuration.OcrJpegQuality}" : "")}) for OCR analysis");
                    LogDebug($"FFmpeg command: {FFmpegHelper.GetFfmpegPath()} {extractArgs}");
                    UpdateProgress(10, "Starting frame extraction and OCR processing");

                    using (var process = new Process
                    {
                        StartInfo = new ProcessStartInfo
                        {
                            FileName = FFmpegHelper.GetFfmpegPath(),
                            Arguments = extractArgs,
                            UseShellExecute = false,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            CreateNoWindow = true
                        }
                    })
                    {
                        process.Start();

                        var timeoutMinutes = Configuration.OcrMaxAnalysisDuration > 0 
                            ? (Configuration.OcrMaxAnalysisDuration / 60) + 5
                            : 30;
                        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(timeoutMinutes));
                        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
                        var effectiveToken = linkedCts.Token;

                        var detectionScores = new List<(double timestamp, int matchCount, string matchedKeywords)>();
                        var characterDensityHistory = new List<(double timestamp, int charCount)>();
                        bool loggedFirstFrame = false;
                        int frameIndex = 0;
                        int maxFramesToProcess = Configuration.OcrMaxFramesToProcess > 0 ? Configuration.OcrMaxFramesToProcess : int.MaxValue;
                        bool creditsFound = false;
                        double creditsTimestamp = 0;

                        var ffmpegTask = Task.Run(async () =>
                    {
                        try
                        {
                            var ffmpegError = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
                            if (!string.IsNullOrWhiteSpace(ffmpegError) && (ffmpegError.Contains("error", StringComparison.OrdinalIgnoreCase) || ffmpegError.Contains("invalid", StringComparison.OrdinalIgnoreCase)))
                            {
                                LogDebug($"FFmpeg output: {ffmpegError}");
                            }
                            return ffmpegError;
                        }
                        catch (Exception ex)
                        {
                            LogWarn($"Error reading FFmpeg output: {ex.Message}");
                            return string.Empty;
                        }
                    });

                    var processingTask = Task.Run(async () =>
                    {
                        var lastFrameCount = 0;
                        var noNewFramesCount = 0;
                        var waitingForFirstFrame = true;
                        const int maxNoNewFramesIterations = 100;
                        const int maxWaitForFirstFrameIterations = 600;
                        var totalWaitIterations = 0;

                        var consecutiveMatches = 0;
                        var recentMatches = new List<(double timestamp, int matchCount)>();
                        var frameSkip = 1;

                        while (!creditsFound && frameIndex < maxFramesToProcess)
                        {

                            if (effectiveToken.IsCancellationRequested)
                            {
                                if (timeoutCts.IsCancellationRequested)
                                {
                                    LastError = $"OCR detection timed out after {timeoutMinutes} minutes";
                                    LogError($"OCR detection timed out after {timeoutMinutes} minutes");
                                }
                                else
                                {
                                    LogInfo("OCR detection cancelled");
                                }
                                break;
                            }

                            if (!Directory.Exists(tempDir))
                            {
                                await Task.Delay(50, effectiveToken).ConfigureAwait(false);
                                continue;
                            }

                            var currentFrames = Directory.GetFiles(tempDir, $"frame_*.{imageExtension}")
                                .OrderBy(f => f)
                                .Skip(frameIndex)
                                .ToList();

                            if (currentFrames.Count == 0)
                            {
                                if (process.HasExited)
                                {
                                    if (frameIndex == 0 || noNewFramesCount > 10)
                                    {
                                        break;
                                    }
                                }

                                if (lastFrameCount == frameIndex)
                                {
                                    noNewFramesCount++;
                                    totalWaitIterations++;

                                    if (waitingForFirstFrame)
                                    {
                                        if (totalWaitIterations > maxWaitForFirstFrameIterations)
                                        {
                                            LogWarn($"Timeout waiting for first frame after {totalWaitIterations * 50}ms");
                                            break;
                                        }

                                        if (totalWaitIterations % 40 == 0)
                                        {
                                            LogDebug($"Waiting for FFmpeg to generate first frame... ({totalWaitIterations * 50 / 1000}s elapsed)");
                                        }
                                    }
                                    else
                                    {
                                        if (noNewFramesCount > maxNoNewFramesIterations)
                                        {
                                            LogDebug($"No new frames for {noNewFramesCount * 50}ms, stopping frame processing");
                                            break;
                                        }
                                    }
                                }
                                else
                                {
                                    noNewFramesCount = 0;
                                }

                                lastFrameCount = frameIndex;
                                await Task.Delay(50, effectiveToken).ConfigureAwait(false);
                                continue;
                            }

                            if (waitingForFirstFrame)
                            {
                                LogDebug($"First frame(s) received after {totalWaitIterations * 50}ms, beginning OCR processing");
                                waitingForFirstFrame = false;
                            }

                            noNewFramesCount = 0;

                            if (Configuration.OcrEnableParallelProcessing && currentFrames.Count > 1)
                            {
                                var batchSize = Math.Min(Configuration.OcrParallelBatchSize, currentFrames.Count);
                                var frameBatch = currentFrames.Take(batchSize).ToList();
                                var frameBatchWithTimestamps = frameBatch.Select((f, i) => (f, startTime + ((frameIndex + i) / fps))).ToList();

                                LogDebug($"Processing {frameBatch.Count} frames in parallel");

                                var batchResults = await OcrOptimizations.ProcessFramesBatch(
                                    frameBatchWithTimestamps,
                                    async (path) => await PerformOcr(path, effectiveToken).ConfigureAwait(false),
                                    batchSize
                                ).ConfigureAwait(false);

                                foreach (var (framePath, ocrText, timestamp) in batchResults)
                                {
                                    if (!string.IsNullOrWhiteSpace(ocrText))
                                    {
                                        var charCount = CountMeaningfulCharacters(ocrText);
                                        characterDensityHistory.Add((timestamp, charCount));

                                        var textPreview = ocrText.Length > 100 ? ocrText.Substring(0, 100) + "..." : ocrText;
                                        var textOneLine = textPreview.Replace("\n", " ").Replace("\r", "");
                                        LogDebug($"Frame at {FormatTime(timestamp)}: OCR detected {charCount} chars: \"{textOneLine}\"");

                                        var matchedKeywords = FindKeywordMatches(ocrText, keywords);

                                        var densityDetected = false;
                                        if (Configuration.OcrEnableCharacterDensityDetection)
                                        {
                                            densityDetected = CheckCharacterDensity(characterDensityHistory, detectionScores, timestamp, charCount, ocrText);
                                        }

                                        bool frameIndicatesCredits = false;
                                        if (Configuration.OcrCharacterDensityPrimaryMethod)
                                        {

                                            frameIndicatesCredits = densityDetected || matchedKeywords.Count > 0;
                                            if (densityDetected)
                                            {
                                                var keywordBonus = matchedKeywords.Count > 0 ? $" + {matchedKeywords.Count} keyword(s): {string.Join(", ", matchedKeywords)}" : "";
                                                LogDebug($"Frame at {FormatTime(timestamp)}: ✓ MATCH - High text density ({charCount} chars){keywordBonus}");
                                            }
                                        }
                                        else
                                        {

                                            frameIndicatesCredits = matchedKeywords.Count > 0;
                                        }

                                        if (frameIndicatesCredits)
                                        {
                                            var matchedText = matchedKeywords.Count > 0 ? string.Join(", ", matchedKeywords) : "density";
                                            detectionScores.Add((timestamp, matchedKeywords.Count > 0 ? matchedKeywords.Count : 1, matchedText));
                                            consecutiveMatches++;
                                            recentMatches.Add((timestamp, matchedKeywords.Count > 0 ? matchedKeywords.Count : 1));

                                            if (matchedKeywords.Count > 0 && !densityDetected)
                                            {
                                                LogDebug($"Frame at {FormatTime(timestamp)}: ✓ MATCH - Found {matchedKeywords.Count} keyword(s): {string.Join(", ", matchedKeywords)}");
                                            }
                                        }
                                        else
                                        {
                                            consecutiveMatches = 0;
                                            recentMatches.Add((timestamp, 0));
                                        }
                                    }
                                    else
                                    {
                                        characterDensityHistory.Add((timestamp, 0));
                                    }

                                    frameIndex++;
                                }

                                if (Configuration.OcrConsecutiveMatchesForEarlyStop > 0)
                                {
                                    if (OcrOptimizations.ShouldTerminateEarly(recentMatches, Configuration.OcrConsecutiveMatchesForEarlyStop))
                                    {
                                        creditsTimestamp = FindCreditsStartFromOcrScores(detectionScores, duration);
                                        if (creditsTimestamp > 0)
                                        {
                                            DetectionReason = BuildDetectionReason(detectionScores, characterDensityHistory, creditsTimestamp);
                                            creditsFound = true;
                                            UpdateProgress(98, $"Credits found via consecutive matches! Processed {frameIndex} frames");
                                            LogInfo($"Early termination: {Configuration.OcrConsecutiveMatchesForEarlyStop} consecutive matches at {FormatTime(creditsTimestamp)}");

                                            try
                                            {
                                                if (!process.HasExited)
                                                {
                                                    process.Kill();
                                                    LogDebug("FFmpeg process terminated after early stop");
                                                }
                                            }
                                            catch (Exception killEx)
                                            {
                                                LogDebug($"Error killing FFmpeg process: {killEx.Message}");
                                            }

                                            break;
                                        }
                                    }
                                }

                                if (Configuration.OcrEnableSmartFrameSkipping && consecutiveMatches > 0)
                                {
                                    frameSkip = OcrOptimizations.CalculateSmartSkip(consecutiveMatches);
                                    if (frameSkip > 1)
                                    {
                                        LogDebug($"Smart skipping: jumping {frameSkip} frames ahead");
                                        frameIndex += (frameSkip - batchSize);
                                    }
                                }

                                if (detectionScores.Count >= Configuration.OcrMinimumMatches)
                                {
                                    creditsTimestamp = FindCreditsStartFromOcrScores(detectionScores, duration);
                                    if (creditsTimestamp > 0)
                                    {
                                        DetectionReason = BuildDetectionReason(detectionScores, characterDensityHistory, creditsTimestamp);
                                        creditsFound = true;
                                        UpdateProgress(98, $"Credits found! Processed {frameIndex} frames");
                                        LogInfo($"Credits detected at {FormatTime(creditsTimestamp)} via OCR keyword matching");

                                        try
                                        {
                                            if (!process.HasExited)
                                            {
                                                process.Kill();
                                                LogDebug("FFmpeg process terminated after credits detected");
                                            }
                                        }
                                        catch (Exception killEx)
                                        {
                                            LogDebug($"Error killing FFmpeg process: {killEx.Message}");
                                        }

                                        break;
                                    }
                                }

                                continue;
                            }

                            foreach (var frameFile in currentFrames)
                            {
                                if (creditsFound || frameIndex >= maxFramesToProcess)
                                {
                                    break;
                                }

                                var timestamp = startTime + (frameIndex / fps);

                                {
                                    if (!loggedFirstFrame)
                                    {
                                        LogInfo($"Processing first frame: {frameFile}");
                                        loggedFirstFrame = true;
                                    }

                                    var estimatedTotal = Math.Min(maxFramesToProcess, (int)(analysisDuration * fps));
                                    var ocrProgress = estimatedTotal > 0 ? (double)(frameIndex + 1) / estimatedTotal : 0;
                                    var overallProgress = 15 + (ocrProgress * 80);
                                    UpdateProgress(overallProgress, $"OCR: {frameIndex + 1} frames ({ocrProgress:P0})");

                                    if (frameIndex > 0 && frameIndex % 50 == 0)
                                    {
                                        LogDebug($"OCR progress: {frameIndex} frames processed");
                                    }

                                    LogDebug($"Sending frame {frameIndex + 1} to OCR API: {frameFile}");
                                    var ocrText = await PerformOcr(frameFile, effectiveToken).ConfigureAwait(false);
                                    LogDebug($"OCR response for frame {frameIndex + 1}: {(string.IsNullOrWhiteSpace(ocrText) ? "empty" : $"{ocrText.Length} chars")}");;

                                    if (!string.IsNullOrWhiteSpace(ocrText))
                                    {
                                        var charCount = CountMeaningfulCharacters(ocrText);
                                        characterDensityHistory.Add((timestamp, charCount));

                                        var textPreview = ocrText.Length > 100 ? ocrText.Substring(0, 100) + "..." : ocrText;
                                        var textOneLine = textPreview.Replace("\n", " ").Replace("\r", "");
                                        LogDebug($"Frame at {FormatTime(timestamp)}: OCR detected {charCount} chars: \"{textOneLine}\"");

                                        var matchedKeywords = FindKeywordMatches(ocrText, keywords);

                                        var densityDetected = false;
                                        if (Configuration.OcrEnableCharacterDensityDetection)
                                        {
                                            densityDetected = CheckCharacterDensity(characterDensityHistory, detectionScores, timestamp, charCount, ocrText);
                                        }

                                        bool frameIndicatesCredits = false;
                                        if (Configuration.OcrCharacterDensityPrimaryMethod)
                                        {

                                            frameIndicatesCredits = densityDetected || matchedKeywords.Count > 0;
                                            if (densityDetected)
                                            {
                                                var keywordBonus = matchedKeywords.Count > 0 ? $" + {matchedKeywords.Count} keyword(s): {string.Join(", ", matchedKeywords)}" : "";
                                                LogDebug($"Frame at {FormatTime(timestamp)}: ✓ MATCH - High text density ({charCount} chars){keywordBonus}");
                                            }
                                        }
                                        else
                                        {

                                            frameIndicatesCredits = matchedKeywords.Count > 0;
                                        }

                                        if (frameIndicatesCredits)
                                        {
                                            var matchedText = matchedKeywords.Count > 0 ? string.Join(", ", matchedKeywords) : "density";
                                            detectionScores.Add((timestamp, matchedKeywords.Count > 0 ? matchedKeywords.Count : 1, matchedText));
                                            consecutiveMatches++;
                                            recentMatches.Add((timestamp, matchedKeywords.Count > 0 ? matchedKeywords.Count : 1));

                                            if (matchedKeywords.Count > 0 && !densityDetected)
                                            {
                                                LogDebug($"Frame at {FormatTime(timestamp)}: ✓ MATCH - Found {matchedKeywords.Count} keyword(s): {string.Join(", ", matchedKeywords)}");
                                            }

                                            if (Configuration.OcrConsecutiveMatchesForEarlyStop > 0)
                                            {
                                                if (OcrOptimizations.ShouldTerminateEarly(recentMatches, Configuration.OcrConsecutiveMatchesForEarlyStop))
                                                {
                                                    creditsTimestamp = FindCreditsStartFromOcrScores(detectionScores, duration);
                                                    if (creditsTimestamp > 0)
                                                    {
                                                        DetectionReason = BuildDetectionReason(detectionScores, characterDensityHistory, creditsTimestamp);
                                                        creditsFound = true;
                                                        UpdateProgress(98, $"Credits found via consecutive matches! Processed {frameIndex + 1} frames");
                                                        LogInfo($"Early termination: {Configuration.OcrConsecutiveMatchesForEarlyStop} consecutive matches at {FormatTime(creditsTimestamp)}");

                                                        try
                                                        {
                                                            if (!process.HasExited)
                                                            {
                                                                process.Kill();
                                                                LogDebug("FFmpeg process terminated after early stop");
                                                            }
                                                        }
                                                        catch (Exception killEx)
                                                        {
                                                            LogDebug($"Error killing FFmpeg process: {killEx.Message}");
                                                        }

                                                        break;
                                                    }
                                                }
                                            }

                                            if (detectionScores.Count >= Configuration.OcrMinimumMatches)
                                            {
                                                creditsTimestamp = FindCreditsStartFromOcrScores(detectionScores, duration);
                                                if (creditsTimestamp > 0)
                                                {
                                                    DetectionReason = BuildDetectionReason(detectionScores, characterDensityHistory, creditsTimestamp);
                                                    creditsFound = true;
                                                    UpdateProgress(98, $"Credits found! Processed {frameIndex + 1} frames");
                                                    LogInfo($"Credits detected at {FormatTime(creditsTimestamp)} via OCR keyword matching");
                                                    LogInfo($"OCR processing stopped early after finding credits (processed {frameIndex + 1} frames, FFmpeg extraction stopped)");

                                                    try
                                                    {
                                                        if (!process.HasExited)
                                                        {
                                                            process.Kill();
                                                            LogDebug("FFmpeg process terminated after credits detected");
                                                        }
                                                    }
                                                    catch (Exception killEx)
                                                    {
                                                        LogDebug($"Error killing FFmpeg process: {killEx.Message}");
                                                    }

                                                    break;
                                                }
                                            }
                                        }
                                        else
                                        {
                                            consecutiveMatches = 0;
                                            recentMatches.Add((timestamp, 0));
                                        }
                                    }
                                    else
                                    {
                                        characterDensityHistory.Add((timestamp, 0));
                                        consecutiveMatches = 0;
                                        recentMatches.Add((timestamp, 0));
                                        LogDebug($"Frame at {FormatTime(timestamp)}: No text detected");
                                    }
                                }

                                frameIndex++;

                                if (Configuration.OcrEnableSmartFrameSkipping && consecutiveMatches > 0)
                                {
                                    frameSkip = OcrOptimizations.CalculateSmartSkip(consecutiveMatches);
                                    if (frameSkip > 1)
                                    {
                                        LogDebug($"Smart skipping: jumping {frameSkip - 1} frames ahead");
                                        var framesToSkip = Math.Min(frameSkip - 1, currentFrames.Count - 1);
                                        for (int i = 0; i < framesToSkip; i++)
                                        {
                                            frameIndex++;
                                        }
                                        break;
                                    }
                                }

                                if (Configuration.OcrDelayBetweenFramesMs > 0)
                                {
                                    await Task.Delay(Configuration.OcrDelayBetweenFramesMs, effectiveToken).ConfigureAwait(false);
                                }
                            }
                        }
                    });

                        await processingTask.ConfigureAwait(false);

                        if (creditsFound)
                        {
                            return creditsTimestamp;
                        }

                        var ffmpegError = await ffmpegTask.ConfigureAwait(false);

                        if (!process.HasExited)
                        {
                            try
                            {
                                process.Kill();
                                LogDebug("Terminated FFmpeg process");
                            }
                            catch (Exception ex)
                            {
                                LogWarn($"Error terminating FFmpeg process: {ex.Message}");
                            }
                        }

                        await process.WaitForExitAsync().ConfigureAwait(false);

                        if (process.ExitCode != 0 && !creditsFound)
                        {
                            LastError = $"FFmpeg frame extraction failed (exit code {process.ExitCode})";
                            LogError($"FFmpeg frame extraction failed with exit code {process.ExitCode}");
                            if (!string.IsNullOrWhiteSpace(ffmpegError))
                            {
                                LogError($"FFmpeg error output: {ffmpegError}");
                            }
                            return 0;
                        }

                        if (frameIndex == 0)
                        {
                            LastError = "No frames extracted for OCR analysis";
                            LogWarn("No frames extracted for OCR analysis");
                            return 0;
                        }

                        LogDebug($"Extracted and processed {frameIndex} frames for OCR analysis");

                        LogDebug($"Extracted and processed {frameIndex} frames for OCR analysis");
                        UpdateProgress(95, $"OCR: {frameIndex} frames (100%)");

                        LogDebug($"OCR analysis complete: Found {detectionScores.Count} frames with keyword matches");
                        UpdateProgress(98, "Analyzing results");

                        if (detectionScores.Count > 0)
                        {
                            var creditsStart = FindCreditsStartFromOcrScores(detectionScores, duration);
                            if (creditsStart > 0)
                            {
                                DetectionReason = BuildDetectionReason(detectionScores, characterDensityHistory, creditsStart);
                                LogInfo($"Credits detected at {FormatTime(creditsStart)} via OCR keyword matching");
                                return creditsStart;
                            }
                        }

                        LastError = $"No OCR keywords found in {frameIndex} frames analyzed";
                        LogDebug("No sustained keyword matches found for credits");
                        return 0;
                    }
                }
                finally
                {

                    var maxRetries = 5;
                    var retryDelay = 200;

                    for (int attempt = 0; attempt < maxRetries; attempt++)
                    {
                        try
                        {
                            if (Directory.Exists(tempDir))
                            {

                                GC.Collect();
                                GC.WaitForPendingFinalizers();

                                Directory.Delete(tempDir, true);
                                LogDebug($"Successfully cleaned up temp directory: {tempDir}");
                                break;
                            }
                        }
                        catch (IOException) when (attempt < maxRetries - 1)
                        {

                            LogDebug($"Temp directory cleanup attempt {attempt + 1} failed (file locked), retrying in {retryDelay}ms...");
                            try
                            {
                                await Task.Delay(retryDelay, cancellationToken).ConfigureAwait(false);
                            }
                            catch (OperationCanceledException)
                            {

                                LogDebug("Cleanup cancelled, stopping retry attempts");
                                break;
                            }
                            retryDelay *= 2;
                        }
                        catch (Exception ex)
                        {
                            if (attempt == maxRetries - 1)
                            {
                                LogError($"Failed to cleanup temp directory after {maxRetries} attempts: {ex.GetType().Name}: {ex.Message}. Directory: {tempDir}", ex);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                LastError = $"OCR detection error: {ex.Message}";
                LogError("Error in OCR detection", ex);
                return 0;
            }
        }

        private async Task<bool> TestOcrEndpoint(CancellationToken cancellationToken = default)
        {
            try
            {
                var endpoint = Configuration.OcrEndpoint.TrimEnd('/');

                var response = await _httpClient.GetAsync(endpoint, cancellationToken).ConfigureAwait(false);

                if (response.IsSuccessStatusCode)
                {
                    LogDebug($"OCR endpoint {endpoint} is accessible");
                    return true;
                }
                else
                {
                    LogWarn($"OCR endpoint {endpoint} returned status: {response.StatusCode}");
                    return false;
                }
            }
            catch (HttpRequestException ex)
            {
                LogWarn($"Cannot connect to OCR endpoint {Configuration.OcrEndpoint}: {ex.Message}");
                return false;
            }
            catch (TaskCanceledException)
            {
                LogWarn($"OCR endpoint {Configuration.OcrEndpoint} timed out");
                return false;
            }
            catch (Exception ex)
            {
                LogWarn($"Error testing OCR endpoint: {ex.Message}");
                return false;
            }
        }

        private async Task<string> PerformOcr(string imagePath, CancellationToken cancellationToken = default)
        {
            try
            {
                var endpoint = Configuration.OcrEndpoint.TrimEnd('/') + "/tesseract";

                LogDebug($"Reading image file: {imagePath}");
                var imageBytes = File.ReadAllBytes(imagePath);
                LogDebug($"Image size: {imageBytes.Length} bytes");

                using (var content = new MultipartFormDataContent())
                {
                    var imageContent = new ByteArrayContent(imageBytes);
                    imageContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
                    content.Add(imageContent, "file", Path.GetFileName(imagePath));

                    var options = "{\"languages\":[\"eng\"]}";
                    content.Add(new StringContent(options), "options");

                    LogDebug($"Sending POST request to {endpoint}...");
                    var response = await _httpClient.PostAsync(endpoint, content, cancellationToken).ConfigureAwait(false);
                    LogDebug($"OCR response status: {response.StatusCode}");

                    if (!response.IsSuccessStatusCode)
                    {
                        LogWarn($"OCR API returned error: {response.StatusCode}");
                        return string.Empty;
                    }

                    var responseText = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

                    var (text, confidence) = ParseOcrResponse(responseText);

                    if (Configuration.OcrMinimumConfidence > 0 && confidence > 0 && confidence < Configuration.OcrMinimumConfidence)
                    {
                        LogDebug($"OCR result rejected due to low confidence: {confidence:F2} < {Configuration.OcrMinimumConfidence:F2}");
                        return string.Empty;
                    }

                    if (confidence > 0)
                    {
                        LogDebug($"OCR confidence: {confidence:F2}");
                    }

                    return text;
                }
            }
            catch (HttpRequestException ex)
            {
                LogWarn($"Failed to connect to OCR API at {Configuration.OcrEndpoint}: {ex.Message}");
                return string.Empty;
            }
            catch (TaskCanceledException)
            {
                LogWarn("OCR request timed out");
                return string.Empty;
            }
            catch (Exception ex)
            {
                LogWarn($"Error performing OCR: {ex.Message}");
                return string.Empty;
            }
        }

        private (string text, double confidence) ParseOcrResponse(string response)
        {
            if (string.IsNullOrWhiteSpace(response))
                return (string.Empty, 0);

            double confidence = 0;

            if (response.TrimStart().StartsWith("{"))
            {
                try
                {
                    var confidenceStart = response.IndexOf("\"confidence\"", StringComparison.OrdinalIgnoreCase);
                    if (confidenceStart >= 0)
                    {
                        var colonIndex = response.IndexOf(":", confidenceStart);
                        var valueStart = colonIndex + 1;
                        var valueEnd = valueStart;

                        while (valueEnd < response.Length && (char.IsDigit(response[valueEnd]) || response[valueEnd] == '.'))
                        {
                            valueEnd++;
                        }

                        if (valueEnd > valueStart)
                        {
                            var confStr = response.Substring(valueStart, valueEnd - valueStart).Trim();
                            if (double.TryParse(confStr, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var conf))
                            {
                                confidence = conf;
                                if (confidence > 1)
                                {
                                    confidence = confidence / 100.0;
                                }
                            }
                        }
                    }

                    var stdoutStart = response.IndexOf("\"stdout\"", StringComparison.OrdinalIgnoreCase);
                    if (stdoutStart >= 0)
                    {
                        var colonIndex = response.IndexOf(":", stdoutStart);
                        var quoteStart = response.IndexOf("\"", colonIndex + 1);

                        if (quoteStart >= 0)
                        {
                            var quoteEnd = quoteStart + 1;
                            while (quoteEnd < response.Length)
                            {
                                if (response[quoteEnd] == '"' && response[quoteEnd - 1] != '\\')
                                {
                                    break;
                                }
                                quoteEnd++;
                            }

                            if (quoteEnd < response.Length)
                            {
                                var text = response.Substring(quoteStart + 1, quoteEnd - quoteStart - 1);
                                text = text.Replace("\\n", "\n").Replace("\\r", "\r").Replace("\\t", "\t").Replace("\\\"", "\"").Replace("\\\\", "\\");
                                text = SanitizeOcrText(text);
                                return (text, confidence);
                            }
                        }
                    }
                }
                catch (JsonException ex)
                {
                    LogWarn($"Failed to parse OCR JSON response: {ex.Message}. Attempting fallback parsing.");

                }
            }

            var sanitized = SanitizeOcrText(response.Trim());
            return (sanitized, 0);
        }
        
        private string SanitizeOcrText(string text)
        {
            if (string.IsNullOrEmpty(text))
                return string.Empty;
            
            // Remove form feed and other control characters (except newline, carriage return, and tab which may be meaningful)
            var cleaned = new System.Text.StringBuilder(text.Length);
            foreach (char c in text)
            {
                // Keep printable characters and meaningful whitespace (newline, carriage return, tab, space)
                if (!char.IsControl(c) || c == '\n' || c == '\r' || c == '\t' || c == ' ')
                {
                    cleaned.Append(c);
                }
            }
            
            return cleaned.ToString().Trim();
        }
        
        private int CountMeaningfulCharacters(string text)
        {
            if (string.IsNullOrEmpty(text))
                return 0;
            
            return text.Count(c => !char.IsWhiteSpace(c));
        }

        private List<string> ParseKeywords(string keywordString)
        {
            if (string.IsNullOrWhiteSpace(keywordString))
                return new List<string>();

            return keywordString
                .Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(k => k.Trim())
                .Where(k => !string.IsNullOrWhiteSpace(k))
                .Distinct()
                .ToList();
        }

        private List<string> FindKeywordMatches(string text, List<string> keywords)
        {
            var matches = new List<string>();
            var lowerText = text.ToLowerInvariant();

            foreach (var keyword in keywords)
            {
                if (lowerText.Contains(keyword.ToLowerInvariant()))
                {
                    matches.Add(keyword);
                }
            }

            return matches;
        }

        private bool CheckCharacterDensity(List<(double timestamp, int charCount)> history, List<(double timestamp, int matchCount, string matchedKeywords)> detectionScores, double currentTimestamp, int currentCharCount, string currentText)
        {
            if (!Configuration.OcrEnableCharacterDensityDetection)
                return false;

            var threshold = Configuration.OcrCharacterDensityThreshold;
            var consecutiveRequired = Configuration.OcrCharacterDensityConsecutiveFrames;

            if (currentCharCount < threshold)
                return false;

            var recentFrames = history
                .Where(h => h.timestamp >= currentTimestamp - 20.0)
                .OrderBy(h => h.timestamp)
                .ToList();

            if (recentFrames.Count < consecutiveRequired)
                return false;

            var consecutiveCount = 0;
            for (int i = recentFrames.Count - 1; i >= 0; i--)
            {
                if (recentFrames[i].charCount >= threshold)
                {
                    consecutiveCount++;
                    if (consecutiveCount >= consecutiveRequired)
                    {
                        // Basic density check passed, now apply additional filters
                        
                        // Filter 1: Keyword Requirement
                        if (Configuration.OcrDensityRequireKeyword && !CheckKeywordRequirement(detectionScores, currentTimestamp))
                        {
                            LogDebug($"Density detected at {FormatTime(currentTimestamp)} but no keywords found within {Configuration.OcrDensityKeywordWindowSeconds}s window - rejected");
                            return false;
                        }
                        
                        // Filter 2: Temporal Consistency Check
                        if (Configuration.OcrDensityRequireTemporalConsistency && !CheckTemporalConsistency(history, currentTimestamp))
                        {
                            LogDebug($"Density detected at {FormatTime(currentTimestamp)} but temporal consistency requirement not met (need {Configuration.OcrDensityMinimumDurationSeconds}s sustained) - rejected");
                            return false;
                        }
                        
                        // Filter 3: Text Style Consistency
                        if (Configuration.OcrDensityRequireStyleConsistency && !CheckStyleConsistency(history, currentTimestamp, currentCharCount))
                        {
                            LogDebug($"Density detected at {FormatTime(currentTimestamp)} but style consistency check failed - rejected");
                            return false;
                        }
                        
                        return true;
                    }
                }
                else
                {

                    break;
                }
            }

            return false;
        }
        
        private bool CheckKeywordRequirement(List<(double timestamp, int matchCount, string matchedKeywords)> detectionScores, double currentTimestamp)
        {
            // Check if there are any keyword matches within the specified time window
            var windowSeconds = Configuration.OcrDensityKeywordWindowSeconds;
            var keywordMatches = detectionScores
                .Where(s => Math.Abs(s.timestamp - currentTimestamp) <= windowSeconds && 
                           s.matchedKeywords != "density" && 
                           !string.IsNullOrEmpty(s.matchedKeywords))
                .ToList();
            
            return keywordMatches.Count > 0;
        }
        
        private bool CheckTemporalConsistency(List<(double timestamp, int charCount)> history, double currentTimestamp)
        {
            // Check if we have sustained high density for the minimum required duration
            var minDuration = Configuration.OcrDensityMinimumDurationSeconds;
            var threshold = Configuration.OcrCharacterDensityThreshold;
            
            // Get frames within the lookback window
            var relevantFrames = history
                .Where(h => h.timestamp <= currentTimestamp && h.timestamp >= currentTimestamp - minDuration)
                .OrderBy(h => h.timestamp)
                .ToList();
            
            if (relevantFrames.Count == 0)
                return false;
            
            // Calculate the actual time span covered
            var timeSpan = currentTimestamp - relevantFrames.First().timestamp;
            
            // Must have frames spanning at least the minimum duration
            if (timeSpan < minDuration * 0.8) // Allow 20% tolerance
                return false;
            
            // Count how many frames meet the threshold
            var framesAboveThreshold = relevantFrames.Count(f => f.charCount >= threshold);
            
            // Require at least 60% of frames to be above threshold (allows for some variation)
            var requiredRatio = 0.6;
            var actualRatio = (double)framesAboveThreshold / relevantFrames.Count;
            
            return actualRatio >= requiredRatio;
        }
        
        private bool CheckStyleConsistency(List<(double timestamp, int charCount)> history, double currentTimestamp, int currentCharCount)
        {
            // Check if character counts are relatively consistent (indicates credits-style text)
            // Credits typically have similar amounts of text per frame, unlike random documents
            var lookbackSeconds = 10.0;
            var threshold = Configuration.OcrCharacterDensityThreshold;
            
            var recentFrames = history
                .Where(h => h.timestamp <= currentTimestamp && 
                           h.timestamp >= currentTimestamp - lookbackSeconds &&
                           h.charCount >= threshold)
                .ToList();
            
            if (recentFrames.Count < 3)
                return false;
            
            // Calculate coefficient of variation (standard deviation / mean)
            // Lower values indicate more consistent text amounts (typical for credits)
            var charCounts = recentFrames.Select(f => (double)f.charCount).ToList();
            var mean = charCounts.Average();
            
            if (mean == 0)
                return false;
            
            var variance = charCounts.Select(x => Math.Pow(x - mean, 2)).Average();
            var stdDev = Math.Sqrt(variance);
            var coefficientOfVariation = stdDev / mean;
            
            // Credits typically have CV < 0.5, random text/documents have higher variation
            // User configurable threshold (default 0.7 is lenient)
            var maxAllowedCV = Configuration.OcrDensityStyleConsistencyThreshold;
            
            return coefficientOfVariation <= maxAllowedCV;
        }

        private string BuildDetectionReason(List<(double timestamp, int matchCount, string matchedKeywords)> scores, List<(double timestamp, int charCount)> densityHistory, double detectedTimestamp)
        {
            var reasonParts = new List<string>();

            var relevantScores = scores
                .Where(s => Math.Abs(s.timestamp - detectedTimestamp) <= 10.0)
                .OrderBy(s => s.timestamp)
                .ToList();

            var densityMatches = relevantScores.Where(s => s.matchedKeywords == "density").Count();
            var keywordMatches = relevantScores.Where(s => s.matchedKeywords != "density").Count();

            if (Configuration.OcrEnableCharacterDensityDetection && densityMatches > 0)
            {

                var densityAtDetection = densityHistory
                    .Where(d => Math.Abs(d.timestamp - detectedTimestamp) <= 5.0)
                    .OrderByDescending(d => d.charCount)
                    .FirstOrDefault();

                if (densityAtDetection.charCount > 0)
                {
                    reasonParts.Add($"Character density: {densityAtDetection.charCount} chars/frame (threshold: {Configuration.OcrCharacterDensityThreshold})");
                }
            }

            if (keywordMatches > 0)
            {

                var allKeywords = relevantScores
                    .Where(s => s.matchedKeywords != "density")
                    .SelectMany(s => s.matchedKeywords.Split(new[] { ", " }, StringSplitOptions.RemoveEmptyEntries))
                    .Distinct()
                    .ToList();

                if (allKeywords.Count > 0)
                {
                    reasonParts.Add($"Keywords: {string.Join(", ", allKeywords)} ({keywordMatches} matches)");
                }
            }

            if (reasonParts.Count == 0)
            {
                return $"OCR detection ({relevantScores.Count} frames)";
            }

            return string.Join(" | ", reasonParts);
        }

        private double FindCreditsStartFromOcrScores(List<(double timestamp, int matchCount, string matchedKeywords)> scores, double duration)
        {
            if (scores.Count == 0)
                return 0;

            var sortedScores = scores.OrderBy(s => s.timestamp).ToList();

            var minMatches = Configuration.OcrMinimumMatches;
            var windowSeconds = 10.0; 

            for (int i = 0; i < sortedScores.Count; i++)
            {
                var matchesInWindow = sortedScores
                    .Where(s => s.timestamp >= sortedScores[i].timestamp && 
                               s.timestamp <= sortedScores[i].timestamp + windowSeconds)
                    .ToList();

                if (matchesInWindow.Count >= minMatches)
                {
                    LogInfo($"Found sustained keyword detection: {matchesInWindow.Count} matches within {windowSeconds}s starting at {FormatTime(sortedScores[i].timestamp)}");
                    return sortedScores[i].timestamp;
                }
            }

            if (sortedScores[0].matchCount >= 2)
            {
                LogInfo($"Single strong match with {sortedScores[0].matchCount} keywords at {FormatTime(sortedScores[0].timestamp)}");
                return sortedScores[0].timestamp;
            }

            return 0;
        }
    }
}
