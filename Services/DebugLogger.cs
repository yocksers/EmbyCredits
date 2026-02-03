using MediaBrowser.Model.Logging;
using System;
using System.Text;
using System.Threading.Tasks;

namespace EmbyCredits.Services
{
    public class DebugLogger : IDisposable
    {
        private readonly ILogger _logger;
        private readonly PluginConfiguration _configuration;
        private StringBuilder? _debugLog;
        private bool _isDebugMode;
        private const int MaxDebugLogSize = 10 * 1024 * 1024;
        private System.Threading.CancellationTokenSource? _cleanupCts;
        private bool _disposed = false;

        public DebugLogger(ILogger logger, PluginConfiguration configuration)
        {
            _logger = logger;
            _configuration = configuration;
        }

        public bool IsDebugMode => _isDebugMode;

        public void StartDebugMode()
        {
            _debugLog = new StringBuilder();
            _debugLog.AppendLine("=".PadRight(80, '='));
            _debugLog.AppendLine($"EMBY CREDITS DETECTION - DEBUG LOG");
            _debugLog.AppendLine($"Started: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            _debugLog.AppendLine("=".PadRight(80, '='));
            _debugLog.AppendLine();

            if (_configuration != null)
            {
                _debugLog.AppendLine("USER CONFIGURATION");
                _debugLog.AppendLine("=".PadRight(80, '='));
                _debugLog.AppendLine();

                _debugLog.AppendLine("GENERAL SETTINGS");
                _debugLog.AppendLine("-".PadRight(80, '-'));
                _debugLog.AppendLine($"  Enable Auto Detection:           {_configuration.EnableAutoDetection}");
                _debugLog.AppendLine($"  Enable Detailed Logging:          {_configuration.EnableDetailedLogging}");
                _debugLog.AppendLine($"  Manual Skip Existing Markers:     {_configuration.ManualSkipExistingMarkers}");
                _debugLog.AppendLine($"  Scheduled Task Process Missing:   {_configuration.ScheduledTaskOnlyProcessMissing}");
                _debugLog.AppendLine($"  Library IDs:                      {(_configuration.LibraryIds?.Length > 0 ? string.Join(", ", _configuration.LibraryIds) : "All")}");
                _debugLog.AppendLine($"  Temp Folder Path:                 {(!string.IsNullOrEmpty(_configuration.TempFolderPath) ? _configuration.TempFolderPath : "Default")}");
                _debugLog.AppendLine();

                _debugLog.AppendLine("DETECTION METHODS");
                _debugLog.AppendLine("-".PadRight(80, '-'));
                _debugLog.AppendLine($"  Video Pattern Detection:          {(_configuration.EnableVideoPatternDetection ? $"Yes (Priority: {_configuration.VideoPatternPriority})" : "No")}");
                _debugLog.AppendLine($"  Audio Pattern Detection:          {(_configuration.EnableAudioPatternDetection ? $"Yes (Priority: {_configuration.AudioPatternPriority})" : "No")}");
                _debugLog.AppendLine($"  Black Screen Detection:           {(_configuration.EnableBlackScreenDetection ? $"Yes (Priority: {_configuration.BlackScreenPriority})" : "No")}");
                _debugLog.AppendLine($"  Audio Silence Detection:          {(_configuration.EnableAudioSilenceDetection ? $"Yes (Priority: {_configuration.AudioSilencePriority})" : "No")}");
                _debugLog.AppendLine($"  Text Detection:                   {(_configuration.EnableTextDetection ? $"Yes (Priority: {_configuration.TextDetectionPriority})" : "No")}");
                _debugLog.AppendLine($"  Scene Change Detection:           {(_configuration.EnableSceneChangeDetection ? $"Yes (Priority: {_configuration.SceneChangePriority})" : "No")}");
                _debugLog.AppendLine($"  Keyword Detection:                {(_configuration.EnableKeywordDetection ? $"Yes (Priority: {_configuration.KeywordDetectionPriority})" : "No")}");
                _debugLog.AppendLine($"  Detection Mode:                   {_configuration.DetectionMode}");
                _debugLog.AppendLine($"  Combined Heuristic:               {(_configuration.EnableCombinedHeuristic ? $"Yes (Priority: {_configuration.CombinedHeuristicPriority})" : "No")}");
                _debugLog.AppendLine();

                _debugLog.AppendLine("DETECTION RESULT SELECTION");
                _debugLog.AppendLine("-".PadRight(80, '-'));
                _debugLog.AppendLine($"  Selection Method:                 {_configuration.DetectionResultSelection}");
                _debugLog.AppendLine($"  Use Correlation Scoring:          {_configuration.UseCorrelationScoring}");
                _debugLog.AppendLine($"  Correlation Window (seconds):     {_configuration.CorrelationWindowSeconds}");
                _debugLog.AppendLine();

                if (_configuration.EnableVideoPatternDetection)
                {
                    _debugLog.AppendLine("VIDEO PATTERN SETTINGS");
                    _debugLog.AppendLine("-".PadRight(80, '-'));
                    _debugLog.AppendLine($"  Sensitivity:                      {_configuration.VideoPatternSensitivity}");
                    _debugLog.AppendLine($"  Window Size:                      {_configuration.VideoPatternWindowSize}");
                    _debugLog.AppendLine($"  Search Start:                     {_configuration.VideoPatternSearchStart:F2}");
                    _debugLog.AppendLine();
                }

                if (_configuration.EnableAudioPatternDetection)
                {
                    _debugLog.AppendLine("AUDIO PATTERN SETTINGS");
                    _debugLog.AppendLine("-".PadRight(80, '-'));
                    _debugLog.AppendLine($"  Sensitivity:                      {_configuration.AudioPatternSensitivity}");
                    _debugLog.AppendLine($"  Window Size:                      {_configuration.AudioPatternWindowSize}");
                    _debugLog.AppendLine($"  Search Start:                     {_configuration.AudioPatternSearchStart:F2}");
                    _debugLog.AppendLine();
                }

                if (_configuration.EnableBlackScreenDetection)
                {
                    _debugLog.AppendLine("BLACK SCREEN SETTINGS");
                    _debugLog.AppendLine("-".PadRight(80, '-'));
                    _debugLog.AppendLine($"  Threshold:                        {_configuration.BlackScreenThreshold}");
                    _debugLog.AppendLine($"  Minimum Duration (seconds):       {_configuration.BlackScreenMinDuration}");
                    _debugLog.AppendLine($"  Search Start:                     {_configuration.BlackScreenSearchStart:F2}");
                    _debugLog.AppendLine();
                }

                if (_configuration.EnableAudioSilenceDetection)
                {
                    _debugLog.AppendLine("AUDIO SILENCE SETTINGS");
                    _debugLog.AppendLine("-".PadRight(80, '-'));
                    _debugLog.AppendLine($"  Threshold (dB):                   {_configuration.AudioSilenceThreshold}");
                    _debugLog.AppendLine($"  Minimum Duration (seconds):       {_configuration.AudioSilenceMinDuration:F1}");
                    _debugLog.AppendLine($"  Search Start:                     {_configuration.AudioSearchStartPosition:F2}");
                    _debugLog.AppendLine();
                }

                if (_configuration.EnableTextDetection)
                {
                    _debugLog.AppendLine("TEXT DETECTION SETTINGS");
                    _debugLog.AppendLine("-".PadRight(80, '-'));
                    _debugLog.AppendLine($"  Threshold:                        {_configuration.TextDetectionThreshold}");
                    _debugLog.AppendLine($"  Minimum Lines:                    {_configuration.TextDetectionMinLines}");
                    _debugLog.AppendLine($"  Search Start:                     {_configuration.TextDetectionSearchStart:F2}");
                    _debugLog.AppendLine();
                }

                if (_configuration.EnableSceneChangeDetection)
                {
                    _debugLog.AppendLine("SCENE CHANGE SETTINGS");
                    _debugLog.AppendLine("-".PadRight(80, '-'));
                    _debugLog.AppendLine($"  Threshold:                        {_configuration.SceneChangeThreshold}");
                    _debugLog.AppendLine($"  Search Start:                     {_configuration.SceneChangeSearchStart:F2}");
                    _debugLog.AppendLine($"  Min Deviation:                    {_configuration.SceneChangeMinDeviation:F2}");
                    _debugLog.AppendLine();
                }

                if (_configuration.EnableKeywordDetection)
                {
                    _debugLog.AppendLine("KEYWORD DETECTION SETTINGS");
                    _debugLog.AppendLine("-".PadRight(80, '-'));
                    _debugLog.AppendLine($"  Search Start:                     {_configuration.KeywordDetectionSearchStart:F2}");
                    _debugLog.AppendLine($"  Min Text Score:                   {_configuration.KeywordDetectionMinTextScore}");
                    _debugLog.AppendLine($"  Region Height:                    {_configuration.KeywordDetectionRegionHeight}");
                    _debugLog.AppendLine($"  Keywords:                         {(_configuration.KeywordDetectionKeywords?.Length > 50 ? _configuration.KeywordDetectionKeywords.Substring(0, 50) + "..." : _configuration.KeywordDetectionKeywords)}");
                    _debugLog.AppendLine();
                }

                if (_configuration.DetectionMode == DetectionMode.OcrOnly || _configuration.DetectionMode == DetectionMode.OcrWithHashFallback)
                {
                    _debugLog.AppendLine("OCR DETECTION SETTINGS");
                    _debugLog.AppendLine("-".PadRight(80, '-'));
                    _debugLog.AppendLine($"  OCR Endpoint:                     {_configuration.OcrEndpoint}");
                    _debugLog.AppendLine($"  Search Start Unit:                {_configuration.OcrSearchStartUnit}");
                    _debugLog.AppendLine($"  Search Start Value:               {_configuration.OcrSearchStartValue:F1}");
                    _debugLog.AppendLine($"  Search Start (ratio):             {_configuration.OcrDetectionSearchStart:F2}");
                    _debugLog.AppendLine($"  Minutes from End:                 {_configuration.OcrMinutesFromEnd:F1}");
                    _debugLog.AppendLine($"  Stop Seconds from End:            {_configuration.OcrStopSecondsFromEnd:F1}");
                    _debugLog.AppendLine($"  Frame Rate:                       {_configuration.OcrFrameRate:F2}");
                    _debugLog.AppendLine($"  Minimum Matches:                  {_configuration.OcrMinimumMatches}");
                    _debugLog.AppendLine($"  Max Frames to Process:            {(_configuration.OcrMaxFramesToProcess > 0 ? _configuration.OcrMaxFramesToProcess.ToString() : "Unlimited")}");
                    _debugLog.AppendLine($"  Max Analysis Duration (sec):      {_configuration.OcrMaxAnalysisDuration:F1}");
                    _debugLog.AppendLine($"  JPEG Quality:                     {_configuration.OcrJpegQuality}");
                    _debugLog.AppendLine($"  Max Resolution Height:            {_configuration.OcrMaxResolutionHeight}");
                    _debugLog.AppendLine($"  Delay Between Frames (ms):        {_configuration.OcrDelayBetweenFramesMs}");
                    _debugLog.AppendLine($"  Minimum Confidence:               {_configuration.OcrMinimumConfidence:F2}");
                    _debugLog.AppendLine();

                    _debugLog.AppendLine("OCR OPTIMIZATION SETTINGS");
                    _debugLog.AppendLine("-".PadRight(80, '-'));
                    _debugLog.AppendLine($"  Enable Parallel Processing:       {_configuration.OcrEnableParallelProcessing}");
                    if (_configuration.OcrEnableParallelProcessing)
                    {
                        _debugLog.AppendLine($"    Batch Size:                     {_configuration.OcrParallelBatchSize}");
                        _debugLog.AppendLine($"    Delay Between Batches (ms):     {_configuration.OcrDelayBetweenBatchesMs}");
                    }
                    _debugLog.AppendLine($"  Enable Smart Frame Skipping:      {_configuration.OcrEnableSmartFrameSkipping}");
                    if (_configuration.OcrEnableSmartFrameSkipping)
                    {
                        _debugLog.AppendLine($"    Consecutive Matches for Stop:   {_configuration.OcrConsecutiveMatchesForEarlyStop}");
                    }
                    _debugLog.AppendLine($"  Enable Adaptive Frame Rate:       {_configuration.OcrEnableAdaptiveFrameRate}");
                    if (_configuration.OcrEnableAdaptiveFrameRate)
                    {
                        _debugLog.AppendLine($"    Minimum Frame Rate:             {_configuration.OcrAdaptiveFrameRateMin:F2}");
                    }
                    _debugLog.AppendLine($"  Retry Attempts:                   {_configuration.OcrRetryAttempts}");
                    _debugLog.AppendLine($"  Retry Delay (ms):                 {_configuration.OcrRetryDelayMs}");
                    _debugLog.AppendLine();

                    _debugLog.AppendLine("OCR CHARACTER DENSITY SETTINGS");
                    _debugLog.AppendLine("-".PadRight(80, '-'));
                    _debugLog.AppendLine($"  Enable Character Density:         {_configuration.OcrEnableCharacterDensityDetection}");
                    if (_configuration.OcrEnableCharacterDensityDetection)
                    {
                        _debugLog.AppendLine($"    Primary Method:                 {_configuration.OcrCharacterDensityPrimaryMethod}");
                        _debugLog.AppendLine($"    Density Threshold:              {_configuration.OcrCharacterDensityThreshold}");
                        _debugLog.AppendLine($"    Consecutive Frames:             {_configuration.OcrCharacterDensityConsecutiveFrames}");
                        _debugLog.AppendLine($"    Require Keyword:                {_configuration.OcrDensityRequireKeyword}");
                        if (_configuration.OcrDensityRequireKeyword)
                        {
                            _debugLog.AppendLine($"      Keyword Window (sec):         {_configuration.OcrDensityKeywordWindowSeconds:F1}");
                        }
                        _debugLog.AppendLine($"    Require Temporal Consistency:   {_configuration.OcrDensityRequireTemporalConsistency}");
                        if (_configuration.OcrDensityRequireTemporalConsistency)
                        {
                            _debugLog.AppendLine($"      Min Duration (sec):           {_configuration.OcrDensityMinimumDurationSeconds:F1}");
                        }
                        _debugLog.AppendLine($"    Require Style Consistency:      {_configuration.OcrDensityRequireStyleConsistency}");
                        if (_configuration.OcrDensityRequireStyleConsistency)
                        {
                            _debugLog.AppendLine($"      Consistency Threshold:        {_configuration.OcrDensityStyleConsistencyThreshold:F2}");
                        }
                    }
                    _debugLog.AppendLine();

                    _debugLog.AppendLine("OCR ADVANCED FEATURES");
                    _debugLog.AppendLine("-".PadRight(80, '-'));
                    _debugLog.AppendLine($"  Enable Image Preprocessing:       {_configuration.OcrEnableImagePreprocessing}");
                    if (_configuration.OcrEnableImagePreprocessing)
                    {
                        _debugLog.AppendLine($"    Contrast Enhancement:           {_configuration.OcrContrastEnhancement:F2}");
                        _debugLog.AppendLine($"    Brightness Adjustment:          {_configuration.OcrBrightnessAdjustment:F2}");
                        _debugLog.AppendLine($"    Enable Sharpening:              {_configuration.OcrEnableSharpening}");
                        if (_configuration.OcrEnableSharpening)
                        {
                            _debugLog.AppendLine($"      Sharpen Amount:               {_configuration.OcrSharpenAmount:F2}");
                        }
                    }
                    _debugLog.AppendLine($"  Enable ROI Detection:             {_configuration.OcrEnableRoiDetection}");
                    if (_configuration.OcrEnableRoiDetection)
                    {
                        _debugLog.AppendLine($"    ROI Region:                     {_configuration.OcrRoiRegion}");
                    }
                    _debugLog.AppendLine($"  Enable Fuzzy Matching:            {_configuration.OcrEnableFuzzyMatching}");
                    if (_configuration.OcrEnableFuzzyMatching)
                    {
                        _debugLog.AppendLine($"    Max Distance:                   {_configuration.OcrFuzzyMatchMaxDistance}");
                    }
                    _debugLog.AppendLine($"  Enable Scrolling Detection:       {_configuration.OcrEnableScrollingDetection}");
                    if (_configuration.OcrEnableScrollingDetection)
                    {
                        _debugLog.AppendLine($"    Min Frames:                     {_configuration.OcrScrollingMinFrames}");
                        _debugLog.AppendLine($"    Overlap Threshold:              {_configuration.OcrScrollingOverlapThreshold:F2}");
                    }
                    _debugLog.AppendLine($"  Enable Credit Structure:          {_configuration.OcrEnableCreditStructureDetection}");
                    if (_configuration.OcrEnableCreditStructureDetection)
                    {
                        _debugLog.AppendLine($"    Min Structure Lines:            {_configuration.OcrMinimumStructureLines}");
                    }
                    _debugLog.AppendLine();

                    _debugLog.AppendLine("OCR FFMPEG SETTINGS");
                    _debugLog.AppendLine("-".PadRight(80, '-'));
                    _debugLog.AppendLine($"  FFmpeg Threads:                   {(_configuration.OcrFfmpegThreads > 0 ? _configuration.OcrFfmpegThreads.ToString() : "Auto")}");
                    _debugLog.AppendLine($"  FFmpeg Filter Threads:            {(_configuration.OcrFfmpegFilterThreads > 0 ? _configuration.OcrFfmpegFilterThreads.ToString() : "Auto")}");
                    _debugLog.AppendLine($"  Pre-Input Args:                   {(!string.IsNullOrEmpty(_configuration.OcrFfmpegPreInputArgs) ? _configuration.OcrFfmpegPreInputArgs : "None")}");
                    _debugLog.AppendLine($"  Enable Hardware Acceleration:     {_configuration.OcrEnableHardwareAcceleration}");
                    if (_configuration.OcrEnableHardwareAcceleration)
                    {
                        _debugLog.AppendLine($"    Hardware Type:                  {_configuration.OcrHardwareAccelerationType}");
                        _debugLog.AppendLine($"    Hardware Device:                {(!string.IsNullOrEmpty(_configuration.OcrHardwareDevice) ? _configuration.OcrHardwareDevice : "Default")}");
                        _debugLog.AppendLine($"    Use Hardware Output Format:     {_configuration.OcrUseHardwareOutputFormat}");
                        _debugLog.AppendLine($"    Use Hardware Filters:           {_configuration.OcrUseHardwareFilters}");
                        _debugLog.AppendLine($"    Use Direct Memory Pipeline:     {_configuration.OcrUseDirectMemoryPipeline}");
                    }
                    _debugLog.AppendLine();

                    _debugLog.AppendLine($"  OCR Keywords:                     {(_configuration.OcrDetectionKeywords?.Length > 50 ? _configuration.OcrDetectionKeywords.Substring(0, 50) + "..." : _configuration.OcrDetectionKeywords)}");
                    _debugLog.AppendLine();
                }

                if (_configuration.DetectionMode == DetectionMode.HashOnly || _configuration.DetectionMode == DetectionMode.HashWithOcrFallback)
                {
                    _debugLog.AppendLine("CHROMAPRINT DETECTION SETTINGS");
                    _debugLog.AppendLine("-".PadRight(80, '-'));
                    _debugLog.AppendLine($"  Min Duration (seconds):           {_configuration.ChromaprintMinDuration}");
                    _debugLog.AppendLine($"  Max Duration (seconds):           {_configuration.ChromaprintMaxDuration}");
                    _debugLog.AppendLine($"  Similarity Threshold:             {_configuration.ChromaprintSimilarityThreshold:F2}");
                    _debugLog.AppendLine($"  Min Episode Count:                {_configuration.ChromaprintMinEpisodeCount}");
                    _debugLog.AppendLine($"  Analysis Percent:                 {_configuration.ChromaprintAnalysisPercent:F1}%");
                    _debugLog.AppendLine($"  Black Frame Threshold:            {_configuration.ChromaprintBlackFrameThreshold:F2}");
                    _debugLog.AppendLine($"  Black Frame Min Duration:         {_configuration.ChromaprintBlackFrameMinDuration:F1}");
                    _debugLog.AppendLine($"  Silence Threshold (dB):           {_configuration.ChromaprintSilenceThreshold}");
                    _debugLog.AppendLine($"  Silence Min Duration:             {_configuration.ChromaprintSilenceMinDuration:F1}");
                    _debugLog.AppendLine($"  Min Confidence:                   {_configuration.ChromaprintMinConfidence:F2}");
                    _debugLog.AppendLine($"  Stop Seconds from End:            {_configuration.ChromaprintStopSecondsFromEnd:F1}");
                    _debugLog.AppendLine($"  Lower Process Priority:           {_configuration.ChromaprintLowerProcessPriority}");
                    _debugLog.AppendLine($"  FFmpeg Threads:                   {(_configuration.ChromaprintFfmpegThreads > 0 ? _configuration.ChromaprintFfmpegThreads.ToString() : "Auto")}");
                    _debugLog.AppendLine($"  Delay Between Operations (ms):    {_configuration.ChromaprintDelayBetweenOperationsMs}");
                    _debugLog.AppendLine();
                }

                if (_configuration.EnableCombinedHeuristic)
                {
                    _debugLog.AppendLine("COMBINED HEURISTIC SETTINGS");
                    _debugLog.AppendLine("-".PadRight(80, '-'));
                    _debugLog.AppendLine($"  Minutes from End:                 {_configuration.CombinedMinutesFromEnd:F1}");
                    _debugLog.AppendLine($"  Search Start:                     {_configuration.CombinedSearchStart:F2}");
                    _debugLog.AppendLine($"  Frame Rate:                       {_configuration.CombinedFrameRate:F1}");
                    _debugLog.AppendLine($"  Use Keywords:                     {_configuration.CombinedUseKeywords}");
                    _debugLog.AppendLine($"  Use Text Density:                 {_configuration.CombinedUseTextDensity}");
                    _debugLog.AppendLine($"  Use Darkness:                     {_configuration.CombinedUseDarkness}");
                    _debugLog.AppendLine($"  Keyword Weight:                   {_configuration.CombinedKeywordWeight:F2}");
                    _debugLog.AppendLine($"  Text Density Weight:              {_configuration.CombinedTextDensityWeight:F2}");
                    _debugLog.AppendLine($"  Darkness Weight:                  {_configuration.CombinedDarknessWeight:F2}");
                    _debugLog.AppendLine($"  Score Threshold:                  {_configuration.CombinedScoreThreshold:F2}");
                    _debugLog.AppendLine($"  Min Sustained (seconds):          {_configuration.CombinedMinSustainedSeconds:F1}");
                    _debugLog.AppendLine();
                }

                _debugLog.AppendLine("PERFORMANCE SETTINGS");
                _debugLog.AppendLine("-".PadRight(80, '-'));
                _debugLog.AppendLine($"  CPU Usage Limit (%):              {_configuration.CpuUsageLimit}");
                _debugLog.AppendLine($"  CPU Throttle Delay (ms):          {_configuration.CpuThrottleDelayMs}");
                _debugLog.AppendLine($"  Delay Between Episodes (ms):      {_configuration.DelayBetweenEpisodesMs}");
                _debugLog.AppendLine($"  Lower Thread Priority:            {_configuration.LowerThreadPriority}");
                _debugLog.AppendLine($"  Lower Process Priority:           {_configuration.LowerProcessPriority}");
                _debugLog.AppendLine($"  Prevent Concurrent Plugin Proc:   {_configuration.PreventConcurrentPluginProcessing}");
                _debugLog.AppendLine();

                _debugLog.AppendLine("THUMBNAIL SETTINGS");
                _debugLog.AppendLine("-".PadRight(80, '-'));
                _debugLog.AppendLine($"  Enable Thumbnail Generation:      {_configuration.EnableThumbnailGeneration}");
                if (_configuration.EnableThumbnailGeneration)
                {
                    _debugLog.AppendLine($"    Thumbnail Width:                {_configuration.ThumbnailWidth}");
                    _debugLog.AppendLine($"    Thumbnail Quality:              {_configuration.ThumbnailQuality}");
                }
                _debugLog.AppendLine();

                _debugLog.AppendLine("=".PadRight(80, '='));
                _debugLog.AppendLine();
            }

            _isDebugMode = true;
            _logger.Info("Debug mode started - all operations will be logged");
        }

        public void LogInfo(string message)
        {
            _logger.Info(message);
            if (_isDebugMode && _debugLog != null)
            {
                TruncateIfNeeded();
                _debugLog.AppendLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [INFO] {message}");
            }
        }

        public void LogDebug(string message)
        {
            if (_configuration?.EnableDetailedLogging == true)
                _logger.Debug(message);
            if (_isDebugMode && _debugLog != null)
            {
                TruncateIfNeeded();
                _debugLog.AppendLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [DEBUG] {message}");
            }
        }

        public void LogWarn(string message)
        {
            _logger.Warn(message);
            if (_isDebugMode && _debugLog != null)
            {
                TruncateIfNeeded();
                _debugLog.AppendLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [WARN] {message}");
            }
        }

        public void LogError(string message, Exception? ex = null)
        {
            if (ex != null)
                _logger.ErrorException(message, ex);
            else
                _logger.Error(message);

            if (_isDebugMode && _debugLog != null)
            {
                TruncateIfNeeded();
                _debugLog.AppendLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [ERROR] {message}");
                if (ex != null)
                {
                    _debugLog.AppendLine($"Exception: {ex.GetType().Name}: {ex.Message}");
                    _debugLog.AppendLine($"StackTrace: {ex.StackTrace}");
                }
            }
        }

        public void LogToDebug(string level, string message)
        {
            if (_isDebugMode && _debugLog != null)
            {
                TruncateIfNeeded();
                _debugLog.AppendLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{level}] {message}");
            }
        }

        public string GetDebugLog()
        {
            var log = _debugLog?.ToString() ?? "No debug log available";
            Cleanup();
            return log;
        }

        public void Cleanup()
        {
            if (_debugLog != null)
            {
                _debugLog.Clear();
                _debugLog = null;
            }
            _isDebugMode = false;
        }

        public void ScheduleDebugLogCleanup()
        {
            _cleanupCts?.Cancel();
            _cleanupCts?.Dispose();
            _cleanupCts = new System.Threading.CancellationTokenSource();
            
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromMinutes(5), _cleanupCts.Token).ConfigureAwait(false);
                    if (_isDebugMode && !_cleanupCts.Token.IsCancellationRequested)
                    {
                        _logger.Info("Debug log auto-cleanup: Debug log was not downloaded within 5 minutes, clearing from memory");
                        Cleanup();
                    }
                }
                catch (System.Threading.Tasks.TaskCanceledException)
                {
                }
                catch (Exception ex)
                {
                    _logger.Error($"Error in debug log cleanup: {ex.Message}");
                }
            }, _cleanupCts.Token);
        }

        private void TruncateIfNeeded()
        {
            if (_debugLog != null && _debugLog.Length > MaxDebugLogSize)
            {
                var keepSize = (int)(MaxDebugLogSize * 0.8);
                var removeSize = _debugLog.Length - keepSize;
                
                // Get the kept portion
                var keptText = _debugLog.ToString(removeSize, _debugLog.Length - removeSize);
                
                // Recreate to release capacity
                _debugLog.Clear();
                _debugLog = new StringBuilder(keepSize + 1024); // Reasonable initial capacity
                _debugLog.Append($"[TRUNCATED: Removed {removeSize} characters to prevent memory growth]\n\n");
                _debugLog.Append(keptText);
                
                _logger.Info($"Debug log truncated to prevent memory growth (was {_debugLog.Length + removeSize} bytes)");
                
                GC.Collect(1, GCCollectionMode.Optimized, false);
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (_disposed)
                return;

            if (disposing)
            {
                _cleanupCts?.Cancel();
                _cleanupCts?.Dispose();
                _cleanupCts = null;
                
                Cleanup();
            }

            _disposed = true;
        }
    }
}
