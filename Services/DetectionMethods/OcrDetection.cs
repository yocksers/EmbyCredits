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
            LastError = string.Empty; // Clear previous error
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
                    // Percentage mode (value should be 50-95, representing 50%-95%)
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
                        
                        // Create a timeout cancellation token source
                        var timeoutMinutes = Configuration.OcrMaxAnalysisDuration > 0 
                            ? (Configuration.OcrMaxAnalysisDuration / 60) + 5  // Add 5 minutes buffer
                            : 30;  // Default 30 minute timeout
                        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(timeoutMinutes));
                        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
                        var effectiveToken = linkedCts.Token;

                        var detectionScores = new List<(double timestamp, int matchCount, string matchedKeywords)>();
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
                            // Check for cancellation (including timeout)
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
                                        
                                        if (totalWaitIterations % 40 == 0) // Every 2 seconds
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
                            
                            // Parallel processing batch
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
                                        var matchedKeywords = FindKeywordMatches(ocrText, keywords);
                                        
                                        if (matchedKeywords.Count > 0)
                                        {
                                            var matchedText = string.Join(", ", matchedKeywords);
                                            detectionScores.Add((timestamp, matchedKeywords.Count, matchedText));
                                            consecutiveMatches++;
                                            recentMatches.Add((timestamp, matchedKeywords.Count));
                                            LogDebug($"Frame at {FormatTime(timestamp)}: Found {matchedKeywords.Count} keyword(s): {matchedText}");
                                        }
                                        else
                                        {
                                            consecutiveMatches = 0;
                                            recentMatches.Add((timestamp, 0));
                                        }
                                    }
                                    
                                    frameIndex++;
                                }
                                
                                // Check for early termination based on consecutive matches
                                if (Configuration.OcrConsecutiveMatchesForEarlyStop > 0)
                                {
                                    if (OcrOptimizations.ShouldTerminateEarly(recentMatches, Configuration.OcrConsecutiveMatchesForEarlyStop))
                                    {
                                        creditsTimestamp = FindCreditsStartFromOcrScores(detectionScores, duration);
                                        if (creditsTimestamp > 0)
                                        {
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
                                
                                // Smart frame skipping
                                if (Configuration.OcrEnableSmartFrameSkipping && consecutiveMatches > 0)
                                {
                                    frameSkip = OcrOptimizations.CalculateSmartSkip(consecutiveMatches);
                                    if (frameSkip > 1)
                                    {
                                        LogDebug($"Smart skipping: jumping {frameSkip} frames ahead");
                                        frameIndex += (frameSkip - batchSize);
                                    }
                                }
                                
                                // Check standard OCR minimum matches
                                if (detectionScores.Count >= Configuration.OcrMinimumMatches)
                                {
                                    creditsTimestamp = FindCreditsStartFromOcrScores(detectionScores, duration);
                                    if (creditsTimestamp > 0)
                                    {
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
                            
                            // Sequential processing (original behavior)
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
                                        var matchedKeywords = FindKeywordMatches(ocrText, keywords);

                                        if (matchedKeywords.Count > 0)
                                        {
                                            var matchedText = string.Join(", ", matchedKeywords);
                                            detectionScores.Add((timestamp, matchedKeywords.Count, matchedText));
                                            consecutiveMatches++;
                                            recentMatches.Add((timestamp, matchedKeywords.Count));
                                            LogDebug($"Frame at {FormatTime(timestamp)}: Found {matchedKeywords.Count} keyword(s): {matchedText}");
                                            
                                            // Check for early termination
                                            if (Configuration.OcrConsecutiveMatchesForEarlyStop > 0)
                                            {
                                                if (OcrOptimizations.ShouldTerminateEarly(recentMatches, Configuration.OcrConsecutiveMatchesForEarlyStop))
                                                {
                                                    creditsTimestamp = FindCreditsStartFromOcrScores(detectionScores, duration);
                                                    if (creditsTimestamp > 0)
                                                    {
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
                                        consecutiveMatches = 0;
                                        recentMatches.Add((timestamp, 0));
                                        LogDebug($"Frame at {FormatTime(timestamp)}: No text detected");
                                    }
                                }
                                
                                frameIndex++;
                                
                                // Smart frame skipping
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
                        
                        // Ensure process is properly terminated before cleanup
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
                                LogInfo($"Credits detected at {FormatTime(creditsStart)} via OCR keyword matching");
                                return creditsStart;
                            }
                        }

                        LastError = $"No OCR keywords found in {frameIndex} frames analyzed";
                        LogDebug("No sustained keyword matches found for credits");
                        return 0;
                    } // End of using (process)
                }
                finally
                {
                    // Ensure directory cleanup after process has been disposed
                    var maxRetries = 5;
                    var retryDelay = 200;

                    for (int attempt = 0; attempt < maxRetries; attempt++)
                    {
                        try
                        {
                            if (Directory.Exists(tempDir))
                            {
                                // Force release of any file handles
                                GC.Collect();
                                GC.WaitForPendingFinalizers();
                                
                                Directory.Delete(tempDir, true);
                                LogDebug($"Successfully cleaned up temp directory: {tempDir}");
                                break;
                            }
                        }
                        catch (IOException) when (attempt < maxRetries - 1)
                        {
                            // File still locked, wait and retry
                            LogDebug($"Temp directory cleanup attempt {attempt + 1} failed (file locked), retrying in {retryDelay}ms...");
                            try
                            {
                                await Task.Delay(retryDelay, cancellationToken).ConfigureAwait(false);
                            }
                            catch (OperationCanceledException)
                            {
                                // Cancellation during cleanup, log but continue trying
                                LogDebug("Cleanup cancelled, stopping retry attempts");
                                break;
                            }
                            retryDelay *= 2; // Exponential backoff
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
                                return (text, confidence);
                            }
                        }
                    }
                }
                catch (JsonException ex)
                {
                    LogWarn($"Failed to parse OCR JSON response: {ex.Message}. Attempting fallback parsing.");
                    // Fall through to plain text handling
                }
            }

            // Plain text response (fallback)
            return (response.Trim(), 0);
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
