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
        
        private double _calculatedConfidence = 0.95;
        public override double Confidence => _calculatedConfidence;
        
        public override int Priority => Configuration.OcrDetectionPriority;
        public override bool IsEnabled => Configuration.EnableOcrDetection;

        private List<double> _ocrTextConfidences = new List<double>();
        private int _totalKeywordMatches = 0;
        private int _totalFramesProcessed = 0;

        private int _lastProcessedFrameIndex = -1;
        private DateTime _lastFrameProgressTime = DateTime.UtcNow;
        private int _stuckRetryCount = 0;
        private const int StuckDetectionTimeoutSeconds = 20;
        private const int MaxStuckRetries = 1;

        private static readonly HttpClient _httpClient = new HttpClient 
        { 
            Timeout = TimeSpan.FromMinutes(2)
        };

        static OcrDetection()
        {
            _httpClient.DefaultRequestHeaders.ConnectionClose = false;
        }

        public OcrDetection(ILogger logger, PluginConfiguration configuration)
            : base(logger, configuration)
        {
        }

        public override async Task<double> DetectCredits(string videoPath, double duration, CancellationToken cancellationToken = default)
        {
            LastError = string.Empty;
            
            _ocrTextConfidences.Clear();
            _totalKeywordMatches = 0;
            _totalFramesProcessed = 0;
            _calculatedConfidence = 0.95;
            
            _lastProcessedFrameIndex = -1;
            _lastFrameProgressTime = DateTime.UtcNow;
            _stuckRetryCount = 0;
            
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
                    var recentTextFrames = new List<(double timestamp, string text)>();
                    
                    if (Configuration.OcrUseDirectMemoryPipeline)
                    {
                        LogInfo("Using direct memory pipeline (no disk I/O) for frame extraction");
                        return await ProcessFramesDirectFromMemory(videoPath, duration, startTime, analysisDuration, fps, keywords, recentTextFrames, cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        LogInfo("Using disk-based frame extraction (legacy method)");
                        return await ProcessFramesFromDisk(videoPath, duration, startTime, analysisDuration, fps, keywords, tempDir, recentTextFrames, cancellationToken).ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    LastError = $"OCR detection error: {ex.Message}";
                    LogError("Error in OCR detection", ex);
                    return 0;
                }
                finally
                {
                    if (Directory.Exists(tempDir))
                    {
                        try
                        {
                            Directory.Delete(tempDir, true);
                            LogDebug($"Cleaned up temp directory: {tempDir}");
                        }
                        catch (Exception ex)
                        {
                            LogDebug($"Could not cleanup temp directory: {ex.Message}");
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

        private Task ProcessOcrResult(
            string ocrText,
            double timestamp,
            int frameIndex,
            double analysisDuration,
            double fps,
            List<string> keywords,
            List<(double timestamp, int score, string matchedText)> detectionScores,
            List<(double timestamp, int charCount)> characterDensityHistory,
            List<(double timestamp, string text)> recentTextFrames,
            int maxFramesToProcess)
        {
            if (frameIndex == 0)
            {
                LogInfo($"Processing first frame at {FormatTime(timestamp)}");
            }

            var estimatedTotal = Math.Min(maxFramesToProcess, (int)(analysisDuration * fps));
            var ocrProgress = estimatedTotal > 0 ? (double)(frameIndex + 1) / estimatedTotal : 0;
            var overallProgress = 15 + (ocrProgress * 80);
            UpdateProgress(overallProgress, $"OCR: {frameIndex + 1} frames ({ocrProgress:P0})");

            if (frameIndex > 0 && frameIndex % 50 == 0)
            {
                LogDebug($"OCR progress: {frameIndex} frames processed");
            }

            if (!string.IsNullOrWhiteSpace(ocrText))
            {
                recentTextFrames.Add((timestamp, ocrText));
                if (recentTextFrames.Count > Configuration.OcrScrollingMinFrames + 5)
                {
                    recentTextFrames.RemoveAt(0);
                }

                var charCount = CountMeaningfulCharacters(ocrText);
                characterDensityHistory.Add((timestamp, charCount));

                var textPreview = ocrText.Length > 100 ? ocrText.Substring(0, 100) + "..." : ocrText;
                var textOneLine = textPreview.Replace("\n", " ").Replace("\r", "");
                LogDebug($"Frame at {FormatTime(timestamp)}: OCR detected {charCount} chars: \"{textOneLine}\"");

                var matchedKeywords = Configuration.OcrEnableFuzzyMatching 
                    ? FindKeywordMatchesFuzzy(ocrText, keywords, Configuration.OcrFuzzyMatchMaxDistance)
                    : FindKeywordMatches(ocrText, keywords);

                var densityDetected = false;
                if (Configuration.OcrEnableCharacterDensityDetection)
                {
                    densityDetected = CheckCharacterDensity(characterDensityHistory, detectionScores, timestamp, charCount, ocrText);
                }

                var scrollingDetected = false;
                if (Configuration.OcrEnableScrollingDetection && recentTextFrames.Count >= Configuration.OcrScrollingMinFrames)
                {
                    scrollingDetected = OcrOptimizations.DetectScrollingPattern(
                        recentTextFrames.TakeLast(Configuration.OcrScrollingMinFrames).ToList(),
                        Configuration.OcrScrollingMinFrames,
                        Configuration.OcrScrollingOverlapThreshold);
                    
                    if (scrollingDetected)
                    {
                        LogDebug($"Frame at {FormatTime(timestamp)}: ✓ Scrolling credits pattern detected");
                    }
                }

                var structureDetected = false;
                if (Configuration.OcrEnableCreditStructureDetection)
                {
                    structureDetected = DetectCreditStructure(ocrText, Configuration.OcrMinimumStructureLines);
                    if (structureDetected)
                    {
                        LogDebug($"Frame at {FormatTime(timestamp)}: ✓ Credit structure pattern detected");
                    }
                }

                bool frameIndicatesCredits = false;
                if (Configuration.OcrCharacterDensityPrimaryMethod)
                {
                    frameIndicatesCredits = densityDetected || matchedKeywords.Count > 0 || scrollingDetected || structureDetected;
                    if (densityDetected)
                    {
                        var keywordBonus = matchedKeywords.Count > 0 ? $" + {matchedKeywords.Count} keyword(s): {string.Join(", ", matchedKeywords)}" : "";
                        LogDebug($"Frame at {FormatTime(timestamp)}: ✓ MATCH - High text density ({charCount} chars){keywordBonus}");
                    }
                }
                else
                {
                    frameIndicatesCredits = matchedKeywords.Count > 0 || scrollingDetected || structureDetected;
                }

                if (frameIndicatesCredits)
                {
                    var matchReasons = new List<string>();
                    if (matchedKeywords.Count > 0) matchReasons.Add(string.Join(", ", matchedKeywords));
                    if (densityDetected && matchedKeywords.Count == 0) matchReasons.Add("density");
                    if (scrollingDetected) matchReasons.Add("scrolling");
                    if (structureDetected) matchReasons.Add("structure");
                    
                    var matchedText = matchReasons.Count > 0 ? string.Join(" | ", matchReasons) : "density";
                    var matchScore = matchedKeywords.Count > 0 ? matchedKeywords.Count : 1;
                    if (scrollingDetected) matchScore += 1;
                    if (structureDetected) matchScore += 1;
                    
                    detectionScores.Add((timestamp, matchScore, matchedText));

                    if (matchedKeywords.Count > 0 && !densityDetected)
                    {
                        LogDebug($"Frame at {FormatTime(timestamp)}: ✓ MATCH - Found {matchedKeywords.Count} keyword(s): {string.Join(", ", matchedKeywords)}");
                    }
                }
            }
            else
            {
                characterDensityHistory.Add((timestamp, 0));
            }
            
            return Task.CompletedTask;
        }

        private async Task<double> ProcessFramesDirectFromMemory(
            string videoPath, 
            double duration, 
            double startTime, 
            double analysisDuration, 
            double fps, 
            List<string> keywords,
            List<(double timestamp, string text)> recentTextFrames,
            CancellationToken cancellationToken)
        {
            var ffmpegInputPath = FFmpegHelper.GetInputArgument(videoPath);
            var preInputArgs = BuildPreInputArgs();
            var threadArgs = BuildThreadArgs();
            var filterChain = BuildFilterChain(fps);
            
            var vcodec = "mjpeg";
            var codecArgs = $"-q:v {Configuration.OcrJpegQuality}";
            var outputFormat = $"-f image2pipe -vcodec {vcodec} {codecArgs} pipe:1";
            
            var extractArgs = $"{preInputArgs}-ss {startTime.ToString(CultureInfo.InvariantCulture)} -i {ffmpegInputPath} {threadArgs}-t {analysisDuration.ToString(CultureInfo.InvariantCulture)} -vf \"{filterChain}\" {outputFormat}";

            LogDebug($"Extracting frames from {FormatTime(startTime)} at {fps} fps (JPG Q{Configuration.OcrJpegQuality}) (Direct Memory Pipeline)");
            LogDebug($"FFmpeg command: {FFmpegHelper.GetFfmpegPath()} {extractArgs}");
            UpdateProgress(10, "Starting direct memory frame extraction");

            using (var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = FFmpegHelper.GetFfmpegPath(),
                    Arguments = extractArgs,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = null
                }
            })
            {
                try
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
                int frameIndex = 0;
                int maxFramesToProcess = Configuration.OcrMaxFramesToProcess > 0 ? Configuration.OcrMaxFramesToProcess : int.MaxValue;
                double creditsTimestamp = 0;

                var ffmpegErrorTask = Task.Run(async () =>
                {
                    try
                    {
                        var ffmpegError = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
                        if (!string.IsNullOrWhiteSpace(ffmpegError) && 
                            (ffmpegError.Contains("error", StringComparison.OrdinalIgnoreCase) || 
                             ffmpegError.Contains("invalid", StringComparison.OrdinalIgnoreCase)))
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

                try
                {
                    var stdoutStream = process.StandardOutput.BaseStream;
                    
                    byte[] imageSignature = new byte[] { 0xFF, 0xD8, 0xFF };
                    byte[] imageEndMarker = new byte[] { 0xFF, 0xD9 };
                    
                    var buffer = new List<byte>();
                    var readBuffer = new byte[65536];
                    var frameQueue = new List<(byte[] data, double timestamp, int index)>();
                    var batchSize = Configuration.OcrEnableParallelProcessing ? Configuration.OcrParallelBatchSize : 1;
                    const int MaxBufferSize = 20 * 1024 * 1024;
                    
                    while (!effectiveToken.IsCancellationRequested && frameIndex < maxFramesToProcess)
                    {
                        int bytesRead = await stdoutStream.ReadAsync(readBuffer, 0, readBuffer.Length, effectiveToken).ConfigureAwait(false);
                        
                        if (bytesRead == 0)
                        {
                            if (buffer.Count > 0)
                            {
                                int imageStart = FindSequence(buffer, imageSignature, 0);
                                if (imageStart >= 0)
                                {
                                    int endMarker = FindSequence(buffer, imageEndMarker, imageStart);
                                    if (endMarker >= 0)
                                    {
                                        var frameData = buffer.GetRange(imageStart, endMarker + imageEndMarker.Length - imageStart).ToArray();
                                        var timestamp = startTime + (frameIndex / fps);
                                        frameQueue.Add((frameData, timestamp, frameIndex));
                                        frameIndex++;
                                    }
                                }
                            }
                            
                            if (frameQueue.Count > 0)
                            {
                                await ProcessFrameBatch(frameQueue, keywords, detectionScores, characterDensityHistory, 
                                    recentTextFrames, analysisDuration, fps, maxFramesToProcess, effectiveToken).ConfigureAwait(false);
                                frameQueue.Clear();
                            }
                            break;
                        }

                        buffer.AddRange(readBuffer.Take(bytesRead));

                        while (buffer.Count > imageSignature.Length && !effectiveToken.IsCancellationRequested && frameIndex < maxFramesToProcess)
                        {
                            int imageStart = FindSequence(buffer, imageSignature, 0);
                            if (imageStart == -1)
                            {
                                if (buffer.Count > imageSignature.Length)
                                {
                                    buffer.RemoveRange(0, buffer.Count - imageSignature.Length);
                                }
                                break;
                            }

                            if (imageStart > 0)
                            {
                                buffer.RemoveRange(0, imageStart);
                                imageStart = 0;
                            }

                            int endMarker = FindSequence(buffer, imageEndMarker, imageStart + imageSignature.Length);
                            
                            if (endMarker == -1)
                            {
                                if (buffer.Count > MaxBufferSize)
                                {
                                    LogWarn($"Frame buffer exceeded {MaxBufferSize / (1024 * 1024)}MB, clearing to prevent memory leak");
                                    buffer.Clear();
                                }
                                break;
                            }

                            var frameLength = endMarker + imageEndMarker.Length - imageStart;
                            var frameData = buffer.GetRange(imageStart, frameLength).ToArray();
                            buffer.RemoveRange(0, imageStart + frameLength);

                            if (frameData.Length < 1024)
                            {
                                LogDebug($"Skipping frame {frameIndex} - too small ({frameData.Length} bytes)");
                                frameIndex++;
                                continue;
                            }

                            var timestamp = startTime + (frameIndex / fps);
                            frameQueue.Add((frameData, timestamp, frameIndex));
                            frameIndex++;

                            if (frameQueue.Count >= batchSize)
                            {
                                await ProcessFrameBatch(frameQueue, keywords, detectionScores, characterDensityHistory, 
                                    recentTextFrames, analysisDuration, fps, maxFramesToProcess, effectiveToken).ConfigureAwait(false);
                                frameQueue.Clear();
                            }
                        }
                    }

                    if (!process.HasExited)
                    {
                        try
                        {
                            process.Kill();
                            LogDebug("FFmpeg process terminated");
                        }
                        catch (Exception ex)
                        {
                            LogDebug($"Error killing FFmpeg process: {ex.Message}");
                        }
                    }

                    await ffmpegErrorTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    LogInfo("Frame extraction cancelled");
                    return 0;
                }

                UpdateProgress(95, $"Analyzing {detectionScores.Count} OCR detections");

                if (detectionScores.Count == 0)
                {
                    LastError = "No credits keywords detected via OCR";
                    LogWarn("OCR completed but found no matching keywords");
                    return 0;
                }

                creditsTimestamp = FindCreditsStartFromOcrScores(detectionScores, duration);

                if (creditsTimestamp > 0)
                {
                    CalculateDynamicConfidence(detectionScores.Count);
                    
                    DetectionReason = BuildDetectionReason(detectionScores, characterDensityHistory, creditsTimestamp);
                    UpdateProgress(100, $"Credits detected at {FormatTime(creditsTimestamp)}");
                    LogInfo($"✓ OCR detected credits starting at {FormatTime(creditsTimestamp)}");
                    LogInfo($"  Detection based on {detectionScores.Count} keyword matches");
                    LogInfo($"  Confidence: {_calculatedConfidence:F2} ({(_calculatedConfidence * 100):F0}%)");
                    return creditsTimestamp;
                }
                else
                {
                    LastError = "No significant credits pattern found";
                    LogWarn("OCR found keywords but no clear credits start point");
                    return 0;
                }
                }
                finally
                {
                    try
                    {
                        if (!process.HasExited)
                        {
                            process.Kill();
                            process.WaitForExit(1000);
                        }
                    }
                    catch (Exception cleanupEx)
                    {
                        LogDebug($"Error during process cleanup: {cleanupEx.Message}");
                    }
                }
            }
        }

        private async Task ProcessFrameBatch(
            List<(byte[] data, double timestamp, int index)> frameQueue,
            List<string> keywords,
            List<(double timestamp, int matchCount, string matchedKeywords)> detectionScores,
            List<(double timestamp, int charCount)> characterDensityHistory,
            List<(double timestamp, string text)> recentTextFrames,
            double analysisDuration,
            double fps,
            int maxFramesToProcess,
            CancellationToken cancellationToken)
        {
            if (frameQueue.Count == 0) return;

            if (Configuration.OcrEnableParallelProcessing && frameQueue.Count > 1)
            {
                LogDebug($"Processing {frameQueue.Count} frames in parallel from memory");
                
                var ocrTasks = frameQueue.Select(async frame =>
                {
                    try
                    {
                        var (ocrText, ocrConfidence) = await PerformOcrOnFrameData(frame.data, cancellationToken).ConfigureAwait(false);
                        
                        if (ocrConfidence > 0)
                        {
                            _ocrTextConfidences.Add(ocrConfidence);
                        }
                        
                        return (frame.timestamp, frame.index, ocrText);
                    }
                    catch (Exception ex)
                    {
                        LogWarn($"Error processing frame {frame.index}: {ex.Message}");
                        return (frame.timestamp, frame.index, string.Empty);
                    }
                }).ToList();

                var results = await Task.WhenAll(ocrTasks).ConfigureAwait(false);

                foreach (var (timestamp, index, ocrText) in results.OrderBy(r => r.timestamp))
                {
                    await ProcessOcrResult(ocrText, timestamp, index, analysisDuration, fps, keywords,
                        detectionScores, characterDensityHistory, recentTextFrames, maxFramesToProcess).ConfigureAwait(false);
                }
                
                if (Configuration.OcrDelayBetweenBatchesMs > 0)
                {
                    await Task.Delay(Configuration.OcrDelayBetweenBatchesMs, cancellationToken).ConfigureAwait(false);
                }
            }
            else
            {
                foreach (var frame in frameQueue)
                {
                    try
                    {
                        var (ocrText, ocrConfidence) = await PerformOcrOnFrameData(frame.data, cancellationToken).ConfigureAwait(false);
                        
                        if (ocrConfidence > 0)
                        {
                            _ocrTextConfidences.Add(ocrConfidence);
                        }
                        
                        await ProcessOcrResult(ocrText, frame.timestamp, frame.index, analysisDuration, fps, keywords,
                            detectionScores, characterDensityHistory, recentTextFrames, maxFramesToProcess).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        LogWarn($"Error processing frame {frame.index}: {ex.Message}");
                    }
                }
            }
        }

        private async Task<double> ProcessFramesFromDisk(
            string videoPath, 
            double duration, 
            double startTime, 
            double analysisDuration, 
            double fps, 
            List<string> keywords,
            string tempDir,
            List<(double timestamp, string text)> recentTextFrames,
            CancellationToken cancellationToken)
        {
            try
            {
                var imageExtension = "jpg";

                var userQuality = Math.Max(1, Math.Min(100, Configuration.OcrJpegQuality));
                var ffmpegQuality = 2 + (int)Math.Round((100 - userQuality) * 29.0 / 99.0);
                var qualityParam = $"-q:v {ffmpegQuality}";

                var frameOutputPath = $"{tempDir.Replace("\\", "/")}/frame_%04d.{imageExtension}";

                var ffmpegTempDir = tempDir.Replace("\\", "/");
                var ffmpegFramePath = $"{ffmpegTempDir}/frame_%04d.{imageExtension}";
                
                var ffmpegInputPath = FFmpegHelper.GetInputArgument(videoPath);

                var preInputArgs = BuildPreInputArgs();
                var threadArgs = BuildThreadArgs();
                var filterChain = BuildFilterChain(fps);
                var extractArgs = $"{preInputArgs}-ss {startTime.ToString(CultureInfo.InvariantCulture)} -i {ffmpegInputPath} {threadArgs}-t {analysisDuration.ToString(CultureInfo.InvariantCulture)} -vf \"{filterChain}\" {qualityParam} -f image2 \"{ffmpegFramePath}\"";

                LogDebug($"Extracting frames from {FormatTime(startTime)} at {fps} fps (JPG Q{Configuration.OcrJpegQuality}) for OCR analysis");
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
                        var stuckDetected = false;

                        while (!creditsFound && frameIndex < maxFramesToProcess && !stuckDetected)
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

                                foreach (var (framePath, ocrText, ocrConfidence, timestamp) in batchResults)
                                {
                                    if (ocrConfidence > 0)
                                    {
                                        _ocrTextConfidences.Add(ocrConfidence);
                                    }
                                    
                                    if (!loggedFirstFrame)
                                    {
                                        LogInfo($"Processing first frame: {framePath}");
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

                                            if (matchedKeywords.Count > 0)
                                            {
                                                _totalKeywordMatches += matchedKeywords.Count;
                                            }

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

                                    _totalFramesProcessed++;
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

                                    if (frameIndex != _lastProcessedFrameIndex)
                                    {
                                        _lastProcessedFrameIndex = frameIndex;
                                        _lastFrameProgressTime = DateTime.UtcNow;
                                    }
                                    else
                                    {
                                        var timeSinceLastProgress = (DateTime.UtcNow - _lastFrameProgressTime).TotalSeconds;
                                        if (timeSinceLastProgress > StuckDetectionTimeoutSeconds)
                                        {
                                            if (_stuckRetryCount < MaxStuckRetries)
                                            {
                                                _stuckRetryCount++;
                                                LogWarn($"Detection stuck on frame {frameIndex + 1} for {timeSinceLastProgress:F0}s. Retry attempt {_stuckRetryCount}/{MaxStuckRetries}");
                                                _lastFrameProgressTime = DateTime.UtcNow;
                                                await Task.Delay(1000, effectiveToken).ConfigureAwait(false);
                                            }
                                            else
                                            {
                                                LastError = $"Detection stuck on frame {frameIndex + 1} for {timeSinceLastProgress:F0}s after {MaxStuckRetries} retry attempt(s). OCR server may be unresponsive.";
                                                LogError(LastError);
                                                
                                                try
                                                {
                                                    if (!process.HasExited)
                                                    {
                                                        process.Kill();
                                                        LogDebug("FFmpeg process terminated due to stuck detection");
                                                    }
                                                }
                                                catch (Exception killEx)
                                                {
                                                    LogDebug($"Error killing FFmpeg process: {killEx.Message}");
                                                }
                                                
                                                stuckDetected = true;
                                                break;
                                            }
                                        }
                                    }

                                    LogDebug($"Sending frame {frameIndex + 1} to OCR API: {frameFile}");
                                    var (ocrText, ocrConfidence) = await PerformOcr(frameFile, effectiveToken).ConfigureAwait(false);
                                    
                                    if (ocrConfidence > 0)
                                    {
                                        _ocrTextConfidences.Add(ocrConfidence);
                                    }
                                    
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
            catch (Exception ex)
            {
                LastError = $"OCR detection error: {ex.Message}";
                LogError("Error in disk-based frame extraction", ex);
                return 0;
            }
        }

        private int FindSequence(List<byte> haystack, byte[] needle, int startIndex)
        {
            for (int i = startIndex; i <= haystack.Count - needle.Length; i++)
            {
                bool found = true;
                for (int j = 0; j < needle.Length; j++)
                {
                    if (haystack[i + j] != needle[j])
                    {
                        found = false;
                        break;
                    }
                }
                if (found)
                {
                    return i;
                }
            }
            return -1;
        }

        private async Task<(string text, double confidence)> PerformOcrOnFrameData(byte[] frameData, CancellationToken cancellationToken)
        {
            try
            {
                var endpoint = Configuration.OcrEndpoint.TrimEnd('/') + "/tesseract";
                
                LogDebug($"Sending {frameData.Length} bytes to OCR endpoint {endpoint} as jpg");
                
                using (var content = new MultipartFormDataContent())
                {
                    var imageContent = new ByteArrayContent(frameData);
                    var contentType = "image/jpeg";
                    var filename = "frame.jpg";
                    imageContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
                    content.Add(imageContent, "file", filename);
                    
                    var optionsDict = new Dictionary<string, object>
                    {
                        { "languages", new[] { "eng" } },
                        { "dpi", 300 }
                    };
                    
                    
                    var options = JsonSerializer.Serialize(optionsDict);
                    content.Add(new StringContent(options), "options");

                    using (var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
                    using (var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token))
                    {
                        using (var response = await _httpClient.PostAsync(endpoint, content, linkedCts.Token).ConfigureAwait(false))
                        {
                            if (response.IsSuccessStatusCode)
                            {
                                var jsonResponse = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                                LogDebug($"OCR response: {jsonResponse}");
                                var ocrResult = JsonSerializer.Deserialize<OcrResponse>(jsonResponse, new JsonSerializerOptions 
                                { 
                                    PropertyNameCaseInsensitive = true 
                                });

                                if (ocrResult?.Data?.Stdout != null)
                                {
                                    var ocrText = ocrResult.Data.Stdout.Trim();
                                    var (parsedText, confidence) = ParseOcrResponse(jsonResponse);
                                    LogDebug($"OCR extracted text length: {ocrText.Length}, confidence: {confidence:F2}");
                                    return (ocrText, confidence);
                                }
                                else
                                {
                                    LogDebug("OCR result Data.Stdout was null or empty");
                                }
                            }
                            else
                            {
                                var errorContent = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                                LogDebug($"OCR request failed with status: {response.StatusCode}, body: {errorContent}");
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                LogDebug($"OCR request error: {ex.Message}");
            }

            return (string.Empty, 0);
        }

        private class OcrResponse
        {
            public OcrData? Data { get; set; }
        }

        private class OcrData
        {
            public string? Stdout { get; set; }
            public string? Stderr { get; set; }
        }

        private string BuildPreInputArgs()
        {
            var args = new List<string>();

            if (Configuration.OcrEnableHardwareAcceleration && 
                !string.IsNullOrWhiteSpace(Configuration.OcrHardwareAccelerationType))
            {
                var hwAccelType = Configuration.OcrHardwareAccelerationType.ToLowerInvariant();
                
                if (hwAccelType == "nvdec" || hwAccelType == "cuda")
                {
                    int threadLimit;
                    if (Configuration.OcrFfmpegThreads > 0)
                    {
                        threadLimit = Math.Min(Configuration.OcrFfmpegThreads, 8);
                        if (Configuration.OcrFfmpegThreads > 8)
                        {
                            LogWarn($"OcrFfmpegThreads={Configuration.OcrFfmpegThreads} exceeds NVDEC safe limit (32 decode surfaces). Capping at 8 threads.");
                        }
                    }
                    else
                    {
                        threadLimit = 4;
                    }
                    args.Add($"-threads {threadLimit}");
                    LogDebug($"NVIDIA hardware - limiting to {threadLimit} threads (max 32 decode surfaces)");
                }
                else if (Configuration.OcrFfmpegThreads > 0)
                {
                    args.Add($"-threads {Configuration.OcrFfmpegThreads}");
                    LogDebug($"Limiting FFmpeg to {Configuration.OcrFfmpegThreads} threads");
                }
            }
            else if (Configuration.OcrFfmpegThreads > 0)
            {
                args.Add($"-threads {Configuration.OcrFfmpegThreads}");
                LogDebug($"Limiting FFmpeg to {Configuration.OcrFfmpegThreads} threads");
            }

            if (!string.IsNullOrWhiteSpace(Configuration.OcrFfmpegPreInputArgs))
            {
                args.Add(Configuration.OcrFfmpegPreInputArgs.Trim());
                LogDebug($"Using custom FFmpeg pre-input args: {Configuration.OcrFfmpegPreInputArgs}");
            }
            else if (Configuration.OcrEnableHardwareAcceleration && 
                     !string.IsNullOrWhiteSpace(Configuration.OcrHardwareAccelerationType) && 
                     Configuration.OcrHardwareAccelerationType != "none")
            {
                var hwAccelType = Configuration.OcrHardwareAccelerationType.ToLowerInvariant();
                
                switch (hwAccelType)
                {
                    case "vaapi":
                        args.Add("-hwaccel vaapi");
                        var vaapiDevice = string.IsNullOrWhiteSpace(Configuration.OcrHardwareDevice) 
                            ? "/dev/dri/renderD128" 
                            : Configuration.OcrHardwareDevice;
                        args.Add($"-vaapi_device {vaapiDevice}");
                        if (Configuration.OcrUseHardwareOutputFormat)
                        {
                            args.Add("-hwaccel_output_format vaapi");
                            LogDebug($"Using VAAPI hardware acceleration with device: {vaapiDevice} (output format: vaapi)");
                        }
                        else
                        {
                            LogDebug($"Using VAAPI hardware acceleration with device: {vaapiDevice} (output format: software)");
                        }
                        break;
                    
                    case "qsv":
                        args.Add("-hwaccel qsv");
                        if (!string.IsNullOrWhiteSpace(Configuration.OcrHardwareDevice))
                        {
                            args.Add($"-qsv_device {Configuration.OcrHardwareDevice}");
                        }
                        if (Configuration.OcrUseHardwareOutputFormat)
                        {
                            args.Add("-hwaccel_output_format qsv");
                            LogDebug("Using Intel Quick Sync Video (QSV) hardware acceleration (output format: qsv)");
                        }
                        else
                        {
                            LogDebug("Using Intel Quick Sync Video (QSV) hardware acceleration (output format: software)");
                        }
                        break;
                    
                    case "cuda":
                        args.Add("-hwaccel cuda");
                        if (!string.IsNullOrWhiteSpace(Configuration.OcrHardwareDevice))
                        {
                            args.Add($"-hwaccel_device {Configuration.OcrHardwareDevice}");
                        }
                        if (Configuration.OcrUseHardwareOutputFormat)
                        {
                            args.Add("-hwaccel_output_format cuda");
                            LogDebug("Using NVIDIA CUDA hardware acceleration (output format: cuda)");
                        }
                        else
                        {
                            LogDebug("Using NVIDIA CUDA hardware acceleration (output format: software)");
                        }
                        break;
                    
                    case "nvdec":
                        args.Add("-hwaccel nvdec");
                        if (!string.IsNullOrWhiteSpace(Configuration.OcrHardwareDevice))
                        {
                            args.Add($"-hwaccel_device {Configuration.OcrHardwareDevice}");
                        }
                        if (Configuration.OcrUseHardwareOutputFormat)
                        {
                            args.Add("-hwaccel_output_format cuda");
                            LogDebug("Using NVIDIA NVDEC hardware acceleration (output format: cuda)");
                        }
                        else
                        {
                            LogDebug("Using NVIDIA NVDEC hardware acceleration (output format: software)");
                        }
                        break;
                    
                    case "d3d11va":
                        args.Add("-hwaccel d3d11va");
                        if (!string.IsNullOrWhiteSpace(Configuration.OcrHardwareDevice))
                        {
                            args.Add($"-hwaccel_device {Configuration.OcrHardwareDevice}");
                        }
                        if (Configuration.OcrUseHardwareOutputFormat)
                        {
                            args.Add("-hwaccel_output_format d3d11");
                            LogDebug("Using Direct3D 11 hardware acceleration (output format: d3d11)");
                        }
                        else
                        {
                            LogDebug("Using Direct3D 11 hardware acceleration (output format: software)");
                        }
                        break;
                    
                    case "dxva2":
                        args.Add("-hwaccel dxva2");
                        if (Configuration.OcrUseHardwareOutputFormat)
                        {
                            args.Add("-hwaccel_output_format dxva2_vld");
                            LogDebug("Using DXVA2 hardware acceleration (output format: dxva2_vld)");
                        }
                        else
                        {
                            LogDebug("Using DXVA2 hardware acceleration (output format: software)");
                        }
                        break;
                    
                    case "videotoolbox":
                        args.Add("-hwaccel videotoolbox");
                        if (Configuration.OcrUseHardwareOutputFormat)
                        {
                            args.Add("-hwaccel_output_format videotoolbox_vld");
                            LogDebug("Using macOS VideoToolbox hardware acceleration (output format: videotoolbox_vld)");
                        }
                        else
                        {
                            LogDebug("Using macOS VideoToolbox hardware acceleration (output format: software)");
                        }
                        break;
                    
                    default:
                        LogWarn($"Unknown hardware acceleration type: {Configuration.OcrHardwareAccelerationType}");
                        break;
                }
            }

            return args.Count > 0 ? string.Join(" ", args) + " " : "";
        }

        private string BuildFilterChain(double fps)
        {
            var filters = new List<string>();
            
            bool useHwFilters = Configuration.OcrEnableHardwareAcceleration && 
                               Configuration.OcrUseHardwareOutputFormat && 
                               Configuration.OcrUseHardwareFilters &&
                               !string.IsNullOrWhiteSpace(Configuration.OcrHardwareAccelerationType) &&
                               Configuration.OcrHardwareAccelerationType != "none";
            
            filters.Add($"fps={fps.ToString(CultureInfo.InvariantCulture)}");
            
            if (useHwFilters)
            {
                var hwAccelType = Configuration.OcrHardwareAccelerationType.ToLowerInvariant();
                
                if (Configuration.OcrMaxResolutionHeight > 0 && Configuration.OcrMaxResolutionHeight < 4320)
                {
                    switch (hwAccelType)
                    {
                        case "vaapi":
                            filters.Add($"scale_vaapi=w=-2:h='min({Configuration.OcrMaxResolutionHeight},ih)':format=nv12");
                            LogDebug($"Resolution limiting: Using VAAPI hardware scaling to max height {Configuration.OcrMaxResolutionHeight}px");
                            break;
                        
                        case "qsv":
                            filters.Add($"scale_qsv=w=-2:h='min({Configuration.OcrMaxResolutionHeight},ih)':format=nv12");
                            LogDebug($"Resolution limiting: Using QSV hardware scaling to max height {Configuration.OcrMaxResolutionHeight}px");
                            break;
                        
                        case "cuda":
                        case "nvdec":
                            filters.Add($"scale_cuda=w=-2:h='min({Configuration.OcrMaxResolutionHeight},ih)':format=nv12");
                            LogDebug($"Resolution limiting: Using CUDA hardware scaling to max height {Configuration.OcrMaxResolutionHeight}px");
                            break;
                    }
                }
                else
                {
                    switch (hwAccelType)
                    {
                        case "vaapi":
                            filters.Add("scale_vaapi=format=nv12");
                            LogDebug($"Using VAAPI hardware scaling");
                            break;
                        
                        case "qsv":
                            filters.Add("scale_qsv=format=nv12");
                            LogDebug($"Using QSV hardware scaling");
                            break;
                        
                        case "cuda":
                        case "nvdec":
                            filters.Add("scale_cuda=format=nv12");
                            LogDebug($"Using CUDA hardware scaling");
                            break;
                    }
                }
                
                filters.Add("hwdownload");
                filters.Add("format=nv12");
                LogDebug($"Hardware filters: fps -> {hwAccelType} scale -> hwdownload -> software filters");
            }
            else if (Configuration.OcrEnableHardwareAcceleration)
            {
                LogDebug($"Using software filters (HW filters disabled in config)");
                
                if (Configuration.OcrMaxResolutionHeight > 0 && Configuration.OcrMaxResolutionHeight < 4320)
                {
                    filters.Add($"scale=-2:'min({Configuration.OcrMaxResolutionHeight},ih)':flags=lanczos");
                    LogDebug($"Resolution limiting: Scaling to max height {Configuration.OcrMaxResolutionHeight}px");
                }
            }
            else
            {
                if (Configuration.OcrMaxResolutionHeight > 0 && Configuration.OcrMaxResolutionHeight < 4320)
                {
                    filters.Add($"scale=-2:'min({Configuration.OcrMaxResolutionHeight},ih)':flags=lanczos");
                    LogDebug($"Resolution limiting: Scaling to max height {Configuration.OcrMaxResolutionHeight}px");
                }
            }
            
            if (Configuration.OcrEnableRoiDetection && !string.IsNullOrWhiteSpace(Configuration.OcrRoiRegion))
            if (Configuration.OcrEnableRoiDetection && !string.IsNullOrWhiteSpace(Configuration.OcrRoiRegion))
            {
                var roi = Configuration.OcrRoiRegion.ToLowerInvariant();
                switch (roi)
                {
                    case "bottom_third":
                        filters.Add("crop=iw:ih/3:0:ih*2/3");
                        LogDebug("ROI: Cropping to bottom third of frame");
                        break;
                    case "bottom_half":
                        filters.Add("crop=iw:ih/2:0:ih/2");
                        LogDebug("ROI: Cropping to bottom half of frame");
                        break;
                    case "center":
                        filters.Add("crop=iw:ih*0.6:0:ih*0.2");
                        LogDebug("ROI: Cropping to center 60% of frame");
                        break;
                    case "top_third":
                        filters.Add("crop=iw:ih/3:0:0");
                        LogDebug("ROI: Cropping to top third of frame");
                        break;
                    case "full":
                    default:
                        break;
                }
            }
            
            if (Configuration.OcrEnableImagePreprocessing)
            {
                var preprocessFilters = new List<string>();
                
                if (Configuration.OcrContrastEnhancement != 1.0 || Configuration.OcrBrightnessAdjustment != 0.0)
                {
                    preprocessFilters.Add($"eq=contrast={Configuration.OcrContrastEnhancement.ToString(CultureInfo.InvariantCulture)}:brightness={Configuration.OcrBrightnessAdjustment.ToString(CultureInfo.InvariantCulture)}");
                }
                
                if (Configuration.OcrEnableSharpening)
                {
                    preprocessFilters.Add($"unsharp=5:5:{Configuration.OcrSharpenAmount.ToString(CultureInfo.InvariantCulture)}");
                }
                
                if (preprocessFilters.Count > 0)
                {
                    filters.AddRange(preprocessFilters);
                    LogDebug($"Image preprocessing: {string.Join(", ", preprocessFilters)}");
                }
            }
            
            return string.Join(",", filters);
        }

        private string BuildThreadArgs()
        {
            var args = new List<string>();

            if (Configuration.OcrFfmpegFilterThreads > 0)
            {
                args.Add($"-filter_threads {Configuration.OcrFfmpegFilterThreads}");
                LogDebug($"Limiting FFmpeg filter threads to {Configuration.OcrFfmpegFilterThreads}");
            }

            return args.Count > 0 ? string.Join(" ", args) + " " : "";
        }

        private async Task<bool> TestOcrEndpoint(CancellationToken cancellationToken = default)
        {
            try
            {
                var endpoint = Configuration.OcrEndpoint.TrimEnd('/');

                using (var response = await _httpClient.GetAsync(endpoint, cancellationToken).ConfigureAwait(false))
                {
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

        private async Task<(string text, double confidence)> PerformOcr(string imagePath, CancellationToken cancellationToken = default)
        {
            var maxRetries = Configuration.OcrRetryAttempts;
            var retryDelay = Configuration.OcrRetryDelayMs;

            for (int attempt = 1; attempt <= maxRetries; attempt++)
            {
                try
                {
                    var (text, confidence) = await PerformOcrInternal(imagePath, cancellationToken).ConfigureAwait(false);
                    if (attempt > 1)
                    {
                        LogInfo($"OCR succeeded on attempt {attempt}/{maxRetries}");
                    }
                    return (text, confidence);
                }
                catch (HttpRequestException ex)
                {
                    if (attempt < maxRetries)
                    {
                        LogWarn($"OCR attempt {attempt}/{maxRetries} failed: {ex.Message}. Retrying in {retryDelay}ms...");
                        await Task.Delay(retryDelay, cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        LogError($"OCR failed after {maxRetries} attempts: {ex.Message}", ex);
                        return (string.Empty, 0);
                    }
                }
                catch (TaskCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    LogWarn("OCR request cancelled");
                    return (string.Empty, 0);
                }
                catch (TaskCanceledException)
                {
                    if (attempt < maxRetries)
                    {
                        LogWarn($"OCR attempt {attempt}/{maxRetries} timed out. Retrying in {retryDelay}ms...");
                        await Task.Delay(retryDelay, cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        LogError($"OCR timed out after {maxRetries} attempts");
                        return (string.Empty, 0);
                    }
                }
                catch (Exception ex)
                {
                    if (attempt < maxRetries)
                    {
                        LogWarn($"OCR attempt {attempt}/{maxRetries} encountered error: {ex.Message}. Retrying in {retryDelay}ms...");
                        await Task.Delay(retryDelay, cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        LogError($"OCR failed after {maxRetries} attempts: {ex.Message}", ex);
                        return (string.Empty, 0);
                    }
                }
            }

            return (string.Empty, 0);
        }

        private async Task<(string text, double confidence)> PerformOcrInternal(string imagePath, CancellationToken cancellationToken = default)
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

                var optionsDict = new Dictionary<string, object>
                {
                    { "languages", new[] { "eng" } },
                    { "dpi", 300 }
                };
                
                var options = JsonSerializer.Serialize(optionsDict);
                content.Add(new StringContent(options), "options");

                LogDebug($"Sending POST request to {endpoint}...");
                
                using (var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
                using (var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token))
                using (var response = await _httpClient.PostAsync(endpoint, content, linkedCts.Token).ConfigureAwait(false))
                {
                    LogDebug($"OCR response status: {response.StatusCode}");

                    if (!response.IsSuccessStatusCode)
                    {
                        LogWarn($"OCR API returned error: {response.StatusCode}");
                        return (string.Empty, 0);
                    }

                    var responseText = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

                    var (text, confidence) = ParseOcrResponse(responseText);

                    if (confidence > 0)
                    {
                        _ocrTextConfidences.Add(confidence);
                    }

                    if (Configuration.OcrMinimumConfidence > 0 && confidence > 0 && confidence < Configuration.OcrMinimumConfidence)
                    {
                        LogDebug($"OCR result rejected due to low confidence: {confidence:F2} < {Configuration.OcrMinimumConfidence:F2}");
                        return (string.Empty, 0);
                    }

                    if (confidence > 0)
                    {
                        LogDebug($"OCR confidence: {confidence:F2}");
                    }

                    return (text, confidence);
                }
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
                                
                                if (confidence == 0 && !string.IsNullOrWhiteSpace(text))
                                {
                                    confidence = CalculateSyntheticConfidence(text);
                                    LogDebug($"OCR server provided no confidence, calculated synthetic: {confidence:F2}");
                                }
                                else if (confidence > 0)
                                {
                                    LogDebug($"OCR server provided confidence: {confidence:F2}");
                                }
                                
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
            
            if (!string.IsNullOrWhiteSpace(sanitized))
            {
                var syntheticConf = CalculateSyntheticConfidence(sanitized);
                LogDebug($"Using fallback parsing with synthetic confidence: {syntheticConf:F2}");
                return (sanitized, syntheticConf);
            }
            
            return (sanitized, 0);
        }
        
        private double CalculateSyntheticConfidence(string text)
        {
            double conf = 0.85;
            
            int length = text.Length;
            if (length < 5)
                conf -= 0.15;
            else if (length < 20)
                conf -= 0.05;
            else if (length > 200)
                conf += 0.05;
            
            var words = text.Split(new[] { ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length >= 3)
                conf += 0.03;
            if (words.Length >= 10)
                conf += 0.02;
            
            int letterCount = text.Count(c => char.IsLetter(c));
            double letterRatio = length > 0 ? (double)letterCount / length : 0;
            if (letterRatio > 0.7)
                conf += 0.03;
            else if (letterRatio < 0.3)
                conf -= 0.10;
            
            var lowerText = text.ToLowerInvariant();
            var creditIndicators = new[] { "directed", "produced", "written", "cast", "music", "editor", "starring" };
            if (creditIndicators.Any(w => lowerText.Contains(w)))
                conf += 0.05;
            
            int specialCount = text.Count(c => !char.IsLetterOrDigit(c) && !char.IsWhiteSpace(c));
            double specialRatio = length > 0 ? (double)specialCount / length : 0;
            if (specialRatio > 0.3)
                conf -= 0.10;
            
            return Math.Max(0.50, Math.Min(0.95, conf));
        }
        
        private string SanitizeOcrText(string text)
        {
            if (string.IsNullOrEmpty(text))
                return string.Empty;
            
            var cleaned = new System.Text.StringBuilder(text.Length);
            foreach (char c in text)
            {
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

        private List<string> FindKeywordMatchesFuzzy(string text, List<string> keywords, int maxDistance = 2)
        {
            var matches = new List<string>();
            var lowerText = text.ToLowerInvariant();
            var words = lowerText.Split(new[] { ' ', '\n', '\r', '\t', ',', '.', ';', ':', '-' }, StringSplitOptions.RemoveEmptyEntries);

            foreach (var keyword in keywords)
            {
                var lowerKeyword = keyword.ToLowerInvariant();

                if (lowerText.Contains(lowerKeyword))
                {
                    matches.Add(keyword);
                    continue;
                }

                foreach (var word in words)
                {
                    if (OcrOptimizations.LevenshteinDistance(word, lowerKeyword) <= maxDistance)
                    {
                        matches.Add(keyword);
                        LogDebug($"Fuzzy match: '{word}' ≈ '{keyword}' (distance: {OcrOptimizations.LevenshteinDistance(word, lowerKeyword)})");
                        break;
                    }
                }
            }

            return matches.Distinct().ToList();
        }

        private bool DetectCreditStructure(string text, int minimumLines = 4)
        {
            var lines = text.Split('\n').Where(l => !string.IsNullOrWhiteSpace(l)).ToArray();

            if (lines.Length < minimumLines)
                return false;

            int upperCaseLines = 0;
            int mixedCaseLines = 0;

            foreach (var line in lines)
            {
                var letters = line.Where(char.IsLetter).ToArray();
                if (letters.Length == 0) continue;

                var trimmedLine = line.Trim();
                if (trimmedLine.Length < 3) continue;

                if (letters.All(char.IsUpper))
                    upperCaseLines++;
                else if (letters.Any(char.IsUpper) && letters.Any(char.IsLower))
                    mixedCaseLines++;
            }

            return (upperCaseLines >= 2 && mixedCaseLines >= 2) || 
                   (upperCaseLines >= minimumLines / 2);
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
                        if (Configuration.OcrDensityRequireKeyword && !CheckKeywordRequirement(detectionScores, currentTimestamp))
                        {
                            LogDebug($"Density detected at {FormatTime(currentTimestamp)} but no keywords found within {Configuration.OcrDensityKeywordWindowSeconds}s window - rejected");
                            return false;
                        }
                        
                        if (Configuration.OcrDensityRequireTemporalConsistency && !CheckTemporalConsistency(history, currentTimestamp))
                        {
                            LogDebug($"Density detected at {FormatTime(currentTimestamp)} but temporal consistency requirement not met (need {Configuration.OcrDensityMinimumDurationSeconds}s sustained) - rejected");
                            return false;
                        }
                        
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
            var minDuration = Configuration.OcrDensityMinimumDurationSeconds;
            var threshold = Configuration.OcrCharacterDensityThreshold;
            
            var relevantFrames = history
                .Where(h => h.timestamp <= currentTimestamp && h.timestamp >= currentTimestamp - minDuration)
                .OrderBy(h => h.timestamp)
                .ToList();
            
            if (relevantFrames.Count == 0)
                return false;
            
            var timeSpan = currentTimestamp - relevantFrames.First().timestamp;
            
            if (timeSpan < minDuration * 0.8)
                return false;
            
            var framesAboveThreshold = relevantFrames.Count(f => f.charCount >= threshold);
            
            var requiredRatio = 0.6;
            var actualRatio = (double)framesAboveThreshold / relevantFrames.Count;
            
            return actualRatio >= requiredRatio;
        }
        
        private bool CheckStyleConsistency(List<(double timestamp, int charCount)> history, double currentTimestamp, int currentCharCount)
        {
            var lookbackSeconds = 10.0;
            var threshold = Configuration.OcrCharacterDensityThreshold;
            
            var recentFrames = history
                .Where(h => h.timestamp <= currentTimestamp && 
                           h.timestamp >= currentTimestamp - lookbackSeconds &&
                           h.charCount >= threshold)
                .ToList();
            
            if (recentFrames.Count < 3)
                return false;
            
            var charCounts = recentFrames.Select(f => (double)f.charCount).ToList();
            var mean = charCounts.Average();
            
            if (mean == 0)
                return false;
            
            var variance = charCounts.Select(x => Math.Pow(x - mean, 2)).Average();
            var stdDev = Math.Sqrt(variance);
            var coefficientOfVariation = stdDev / mean;
            
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

        private void CalculateDynamicConfidence(int detectionMatches)
        {
            double confidence = 0.70;

            if (_ocrTextConfidences.Count > 0)
            {
                var avgOcrConfidence = _ocrTextConfidences.Average();
                confidence += avgOcrConfidence * 0.15;
                LogDebug($"OCR text confidence contribution: {avgOcrConfidence:F2} -> +{(avgOcrConfidence * 0.15):F2}");
            }

            if (_totalFramesProcessed > 0)
            {
                var matchDensity = Math.Min(1.0, (double)_totalKeywordMatches / _totalFramesProcessed);
                confidence += matchDensity * 0.10;
                LogDebug($"Keyword match density: {matchDensity:F2} ({_totalKeywordMatches}/{_totalFramesProcessed}) -> +{(matchDensity * 0.10):F2}");
            }

            var matchFactor = Math.Min(1.0, detectionMatches / 10.0);
            confidence += matchFactor * 0.05;
            LogDebug($"Detection matches: {detectionMatches} -> +{(matchFactor * 0.05):F2}");

            _calculatedConfidence = Math.Max(0.70, Math.Min(0.98, confidence));
            
            LogInfo($"Dynamic confidence calculated: {_calculatedConfidence:F2} (Base: 0.70, OCR: {(_ocrTextConfidences.Count > 0 ? _ocrTextConfidences.Average() : 0):F2}, Density: {(_totalFramesProcessed > 0 ? (double)_totalKeywordMatches / _totalFramesProcessed : 0):F2}, Matches: {detectionMatches})");
        }
    }
}
