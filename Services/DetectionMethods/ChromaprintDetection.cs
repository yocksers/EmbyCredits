using MediaBrowser.Model.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EmbyCredits.Services.Utilities;

namespace EmbyCredits.Services.DetectionMethods
{
    public class ChromaprintDetection : BaseDetectionMethod
    {
        public override string MethodName => "Chromaprint Audio Fingerprint Detection";
        
        private double _calculatedConfidence = 0.90;
        public override double Confidence => _calculatedConfidence;
        
        private Dictionary<string, double> _batchConfidenceScores = new Dictionary<string, double>();
        
        public override int Priority => Configuration.ChromaprintDetectionPriority;
        public override bool IsEnabled => Configuration.DetectionMode == DetectionMode.HashOnly || 
                                          Configuration.DetectionMode == DetectionMode.HashWithOcrFallback ||
                                          Configuration.DetectionMode == DetectionMode.OcrWithHashFallback;

        private static readonly DetectionTimestampCache _cache = new DetectionTimestampCache();

        public ChromaprintDetection(ILogger logger, PluginConfiguration configuration)
            : base(logger, configuration)
        {
        }

        public static void ClearSeriesCache(string seriesId, int seasonNumber) =>
            _cache.ClearSeries(seriesId, seasonNumber);

        public static void ClearAllCache() =>
            _cache.ClearAll();

        public override Task<double> DetectCredits(string videoPath, double duration, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(0.0);
        }

        public Task<double> DetectCreditsWithContext(string videoPath, double duration, string episodeId, string seriesId, int? seasonNumber, int? episodeNumber, CancellationToken cancellationToken = default)
        {
            LastError = "Chromaprint requires batch comparison across episodes; no batch result was available for this episode";
            return Task.FromResult(0.0);
        }

        public async Task<Dictionary<string, double>> DetectCreditsForSeason(List<(string EpisodeId, string VideoPath, double Duration)> episodes, string seriesId, int seasonNumber, CancellationToken cancellationToken = default)
        {
            var results = new Dictionary<string, double>();
            _batchConfidenceScores.Clear();
            
            try
            {
                Logger.Info($"[{MethodName}] Processing {episodes.Count} episodes in batch");
                LogInfo($"Processing {episodes.Count} episodes in batch");
                
                var analysisPercent = Configuration.ChromaprintAnalysisPercent / 100.0;
                var minIntroDuration = Configuration.ChromaprintMinDuration;
                var maxIntroDuration = Configuration.ChromaprintMaxDuration;
                
                LogInfo("Attempting chromaprint audio fingerprinting...");
                var fingerprintResults = await DetectCreditsUsingChromaprint(episodes, analysisPercent, minIntroDuration, maxIntroDuration, cancellationToken);
                
                var analyzedEpisodes = new HashSet<string>(fingerprintResults.Keys);
                
                foreach (var kvp in fingerprintResults)
                {
                    results[kvp.Key] = kvp.Value;
                    LogInfo($"Episode {kvp.Key}: Credits detected at {FormatTime(kvp.Value)} using chromaprint");
                }
                
                LogInfo($"Chromaprint successfully analyzed {analyzedEpisodes.Count}/{episodes.Count} episodes");
                
                if (analyzedEpisodes.Count < episodes.Count)
                {
                    LogInfo($"Chromaprint completed. {episodes.Count - analyzedEpisodes.Count} episodes will use fallback detection methods.");
                }
                
                LogInfo($"Batch processing complete: {results.Count}/{episodes.Count} episodes analyzed successfully");
            }
            catch (Exception ex)
            {
                Logger.Error($"[{MethodName}] Error in batch processing: {ex}");
            }
            
            return results;
        }
        
        public double GetBatchConfidence(string episodeId)
        {
            return _batchConfidenceScores.TryGetValue(episodeId, out var confidence) ? confidence : 0.90;
        }
        
        private async Task<Dictionary<string, double>> DetectCreditsUsingChromaprint(
            List<(string EpisodeId, string VideoPath, double Duration)> episodes,
            double analysisPercent,
            double minIntroDuration,
            double maxIntroDuration,
            CancellationToken cancellationToken)
        {
            var results = new Dictionary<string, double>();
            
            try
            {
                var episodeFingerprints = new ConcurrentDictionary<string, (uint[] fingerprint, double startTime)>();

                var maxConcurrentFingerprints = Math.Max(1, Configuration.ChromaprintParallelSessions);
                using var fpSemaphore = new SemaphoreSlim(maxConcurrentFingerprints, maxConcurrentFingerprints);

                var fingerprintTasks = episodes.Select(async episode =>
                {
                    if (cancellationToken.IsCancellationRequested)
                        return;

                    var stopSecondsFromEnd = Configuration.ChromaprintStopSecondsFromEnd;
                    var analysisEndTime = Math.Max(0, episode.Duration - stopSecondsFromEnd);
                    var analysisStartTime = Math.Max(0, episode.Duration * (1.0 - analysisPercent));

                    try
                    {
                        await fpSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }

                    try
                    {
                        LogDebug($"Generating chromaprint for episode {episode.EpisodeId} from {FormatTime(analysisStartTime)} to {FormatTime(analysisEndTime)} (duration: {analysisEndTime - analysisStartTime:F1}s, excluding last {stopSecondsFromEnd}s)");
                        var fingerprint = await Task.Run(() => FFmpegHelper.GenerateChromaprint(episode.VideoPath, analysisStartTime, analysisEndTime, Logger, Configuration.ChromaprintFfmpegThreads), cancellationToken).ConfigureAwait(false);

                        if (fingerprint.Length > 0)
                        {
                            episodeFingerprints[episode.EpisodeId] = (fingerprint, analysisStartTime);
                            LogDebug($"Episode {episode.EpisodeId}: Generated {fingerprint.Length} fingerprint points");
                        }
                        else
                        {
                            LogDebug($"Episode {episode.EpisodeId}: Failed to generate fingerprint");
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn($"Failed to generate fingerprint for {episode.EpisodeId}: {ex.Message}");
                    }
                    finally
                    {
                        fpSemaphore.Release();
                    }
                }).ToList();

                await Task.WhenAll(fingerprintTasks).ConfigureAwait(false);
                
                if (episodeFingerprints.Count >= 2)
                {
                    LogInfo($"Comparing {episodeFingerprints.Count} episode fingerprints using subsequence matching...");
                    
                    var episodeOffsets = new Dictionary<string, List<int>>(episodeFingerprints.Count);
                    var episodeMatchScores = new Dictionary<string, List<double>>(episodeFingerprints.Count);
                    var episodeBestPeer = new Dictionary<string, (string peerId, int peerOffset, double score)>();
                    var allMatchScores = new List<double>();
                    
                    var episodeList = episodeFingerprints.Keys.ToList();
                    for (int i = 0; i < episodeList.Count; i++)
                    {
                        var ep1 = episodeList[i];
                        if (!episodeOffsets.ContainsKey(ep1))
                        {
                            episodeOffsets[ep1] = new List<int>();
                            episodeMatchScores[ep1] = new List<double>();
                        }
                        
                        for (int j = i + 1; j < episodeList.Count; j++)
                        {
                            var ep2 = episodeList[j];
                            if (!episodeOffsets.ContainsKey(ep2))
                            {
                                episodeOffsets[ep2] = new List<int>();
                                episodeMatchScores[ep2] = new List<double>();
                            }
                            
                            var fp1 = episodeFingerprints[ep1].fingerprint;
                            var fp2 = episodeFingerprints[ep2].fingerprint;
                            
                            var match = FFmpegHelper.FindBestMatchingSubsequence(fp1, fp2, minWindowSize: 100);
                            
                            LogDebug($"Comparison {ep1} <-> {ep2}: score={match.score:F3}, offset1={match.offsetFp1}, offset2={match.offsetFp2}");
                            allMatchScores.Add(match.score);
                            
                            episodeOffsets[ep1].Add(match.offsetFp1);
                            episodeOffsets[ep2].Add(match.offsetFp2);
                            episodeMatchScores[ep1].Add(match.score);
                            episodeMatchScores[ep2].Add(match.score);

                            if (!episodeBestPeer.TryGetValue(ep1, out var bp1) || match.score > bp1.score)
                                episodeBestPeer[ep1] = (ep2, match.offsetFp2, match.score);
                            if (!episodeBestPeer.TryGetValue(ep2, out var bp2) || match.score > bp2.score)
                                episodeBestPeer[ep2] = (ep1, match.offsetFp1, match.score);
                        }
                    }
                    
                    var dynamicThreshold = CalculateDynamicThreshold(allMatchScores);
                    LogInfo($"Dynamic match threshold calculated: {dynamicThreshold:F3} (from {allMatchScores.Count} comparisons)");
                    
                    foreach (var epId in episodeList)
                    {
                        if (episodeMatchScores.ContainsKey(epId))
                        {
                            var scores = episodeMatchScores[epId];
                            var offsets = episodeOffsets[epId];
                            var filtered = new List<int>();
                            var filteredScores = new List<double>();
                            
                            for (int i = 0; i < scores.Count; i++)
                            {
                                if (scores[i] >= dynamicThreshold)
                                {
                                    filtered.Add(offsets[i]);
                                    filteredScores.Add(scores[i]);
                                }
                            }
                            
                            episodeOffsets[epId] = filtered;
                            episodeMatchScores[epId] = filteredScores;
                            
                            if (filtered.Count != offsets.Count)
                            {
                                LogDebug($"Episode {epId}: Filtered {offsets.Count - filtered.Count} low-confidence matches");
                            }
                        }
                    }
                    
                    foreach (var episode in episodes)
                    {
                        if (!episodeFingerprints.ContainsKey(episode.EpisodeId))
                            continue;
                        
                        var epId = episode.EpisodeId;
                        var startTime = episodeFingerprints[epId].startTime;
                        
                        if (!episodeOffsets.ContainsKey(epId) || episodeOffsets[epId].Count == 0)
                        {
                            LogDebug($"Episode {epId}: No high-confidence matches found");
                            continue;
                        }
                        
                        var rawOffsets = episodeOffsets[epId];
                        var scores = episodeMatchScores[epId];
                        
                        var (filteredOffsets, filteredScores) = FilterOutliersIQR(rawOffsets, scores);
                        
                        if (filteredOffsets.Count == 0)
                        {
                            LogDebug($"Episode {epId}: All offsets filtered as outliers");
                            continue;
                        }
                        
                        if (filteredOffsets.Count < rawOffsets.Count)
                        {
                            LogDebug($"Episode {epId}: Removed {rawOffsets.Count - filteredOffsets.Count} outlier offset(s) using IQR method");
                        }
                        
                        var weightedMedianOffset = CalculateWeightedMedian(filteredOffsets, filteredScores);
                        
                        var coarseOffsetSeconds = weightedMedianOffset * 0.13;
                        var coarseCreditsStartTime = startTime + coarseOffsetSeconds;
                        
                        LogDebug($"Episode {epId}: Coarse detection - {filteredOffsets.Count} matches, weighted median offset={weightedMedianOffset} points ({coarseOffsetSeconds:F1}s)");
                        
                        uint[]? referenceFingerprint = null;
                        var referenceMatchOffset = 0;
                        if (episodeBestPeer.TryGetValue(epId, out var bestPeer) &&
                            episodeFingerprints.TryGetValue(bestPeer.peerId, out var peerFpData))
                        {
                            referenceFingerprint = peerFpData.fingerprint;
                            referenceMatchOffset = bestPeer.peerOffset;
                        }

                        var refinedCreditsStartTime = referenceFingerprint != null
                            ? await RefineTimestampWithFineGrainedPass(
                                episode.VideoPath,
                                coarseCreditsStartTime,
                                referenceFingerprint,
                                referenceMatchOffset,
                                cancellationToken)
                            : 0;
                        
                        var finalTimestamp = refinedCreditsStartTime > 0 ? refinedCreditsStartTime : coarseCreditsStartTime;
                        var creditsDuration = episode.Duration - finalTimestamp;
                        
                        if (refinedCreditsStartTime > 0 && Math.Abs(refinedCreditsStartTime - coarseCreditsStartTime) > 0.5)
                        {
                            LogInfo($"Episode {epId}: Fine-grained refinement adjusted timestamp: {FormatTime(coarseCreditsStartTime)} -> {FormatTime(refinedCreditsStartTime)} (delta: {Math.Abs(refinedCreditsStartTime - coarseCreditsStartTime):F1}s)");
                        }
                        
                        LogDebug($"Episode {epId}: Final credits at {FormatTime(finalTimestamp)}, duration={creditsDuration:F1}s");
                        
                        if (creditsDuration >= minIntroDuration && creditsDuration <= maxIntroDuration)
                        {
                            results[epId] = finalTimestamp;
                            
                            double avgScore = filteredScores.Count > 0 ? filteredScores.Average() : 0.90;
                            _batchConfidenceScores[epId] = avgScore;
                            
                            LogInfo($"Episode {epId}: Credits at {FormatTime(finalTimestamp)} (matched with {filteredOffsets.Count} episodes, confidence: {avgScore:F3})");
                        }
                        else
                        {
                            LogDebug($"Episode {epId}: Rejected - credits duration {creditsDuration:F1}s outside range {minIntroDuration}-{maxIntroDuration}");
                        }
                    }
                }
                else
                {
                    LogDebug("Not enough episodes with valid fingerprints for comparison");
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Error in chromaprint detection: {ex}");
            }
            
            return results;
        }
        
        private void CleanupCache()
        {
            _cache.EnsureCleanedIfNeeded();
        }

        private async Task<double> DetectBlackFrameTransition(string videoPath, double startTime, double duration, CancellationToken cancellationToken)
        {
            try
            {
                var blackThreshold = Configuration.ChromaprintBlackFrameThreshold;
                var minDuration = Configuration.ChromaprintBlackFrameMinDuration;
                
                LogDebug($"Detecting black frames (threshold: {blackThreshold}, min duration: {minDuration}s)");
                
                var tempFolder = GetTempFolder();
                var ffmpegPath = FFmpegHelper.GetFfmpegPath();
                
                if (string.IsNullOrWhiteSpace(ffmpegPath))
                {
                    LogWarn("FFmpeg not found, skipping black frame detection");
                    return 0;
                }
                
                var endTime = duration - Configuration.ChromaprintStopSecondsFromEnd;
                var analysisDuration = endTime - startTime;
                
                if (analysisDuration <= 0)
                {
                    LogDebug("Analysis duration too short, skipping black frame detection");
                    return 0;
                }
                
                var normalizedVideoPath = FFmpegHelper.ResolveInputPath(videoPath);

                var chromaBlackStartInfo = new ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                };
                if (Configuration.ChromaprintFfmpegThreads > 0)
                {
                    chromaBlackStartInfo.ArgumentList.Add("-threads");
                    chromaBlackStartInfo.ArgumentList.Add(Configuration.ChromaprintFfmpegThreads.ToString(CultureInfo.InvariantCulture));
                }
                chromaBlackStartInfo.ArgumentList.Add("-ss");
                chromaBlackStartInfo.ArgumentList.Add(startTime.ToString(CultureInfo.InvariantCulture));
                chromaBlackStartInfo.ArgumentList.Add("-t");
                chromaBlackStartInfo.ArgumentList.Add(analysisDuration.ToString(CultureInfo.InvariantCulture));
                chromaBlackStartInfo.ArgumentList.Add("-i");
                chromaBlackStartInfo.ArgumentList.Add(normalizedVideoPath);
                chromaBlackStartInfo.ArgumentList.Add("-vf");
                chromaBlackStartInfo.ArgumentList.Add($"blackdetect=d={minDuration.ToString(CultureInfo.InvariantCulture)}:pix_th={blackThreshold.ToString(CultureInfo.InvariantCulture)}");
                chromaBlackStartInfo.ArgumentList.Add("-an");
                chromaBlackStartInfo.ArgumentList.Add("-f");
                chromaBlackStartInfo.ArgumentList.Add("null");
                chromaBlackStartInfo.ArgumentList.Add("-");
                
                Logger.Info($"[{MethodName}] Executing FFmpeg black detection: {ffmpegPath} -ss {startTime} -t {analysisDuration} -i <path> -vf blackdetect...");
                
                using (var process = new Process { StartInfo = chromaBlackStartInfo })
                {
                    if (Configuration.ChromaprintLowerProcessPriority)
                    {
                        CpuThrottler.SetProcessPriority(process, Configuration);
                    }
                    
                    var output = new List<string>();
                    DataReceivedEventHandler errorHandler = (sender, e) =>
                    {
                        if (!string.IsNullOrEmpty(e.Data))
                        {
                            output.Add(e.Data);
                        }
                    };
                    
                    try
                    {
                        process.ErrorDataReceived += errorHandler;
                        
                        process.Start();
                        
                        if (Configuration.ChromaprintLowerProcessPriority)
                        {
                            CpuThrottler.SetProcessPriority(process, Configuration);
                        }
                        
                        process.BeginErrorReadLine();
                        
                        await process.WaitForExitAsync(cancellationToken);
                    
                        if (Configuration.ChromaprintDelayBetweenOperationsMs > 0)
                        {
                            await Task.Delay(Configuration.ChromaprintDelayBetweenOperationsMs, cancellationToken);
                        }
                    }
                    finally
                    {
                        try
                        {
                            process.ErrorDataReceived -= errorHandler;
                            process.CancelErrorRead();
                        }
                        catch { }
                    }
                    
                    var blackFrames = new List<double>();
                    foreach (var line in output)
                    {
                        if (line.Contains("blackdetect") && line.Contains("black_start:"))
                        {
                            var match = System.Text.RegularExpressions.Regex.Match(line, @"black_start:(\d+\.?\d*)");
                            if (match.Success && double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var blackStart))
                            {
                                var adjustedTime = startTime + blackStart;
                                blackFrames.Add(adjustedTime);
                                LogDebug($"Found black frame at {FormatTime(adjustedTime)}");
                            }
                        }
                    }
                    
                    output.Clear();
                    output.TrimExcess();
                    output = null;
                    
                    if (blackFrames.Count > 0)
                    {
                        var result = blackFrames.First();
                        blackFrames.Clear();
                        blackFrames.TrimExcess();
                        return result;
                    }
                    
                    blackFrames.Clear();
                    blackFrames.TrimExcess();
                }
                
                return 0;
            }
            catch (Exception ex)
            {
                LogWarn($"Error detecting black frames: {ex.Message}");
                return 0;
            }
        }

        private string GetTempFolder()
        {
            if (!string.IsNullOrWhiteSpace(Configuration.TempFolderPath) && Directory.Exists(Configuration.TempFolderPath))
            {
                return Configuration.TempFolderPath;
            }
            return Path.GetTempPath();
        }

        private double CalculateDynamicThreshold(List<double> scores)
        {
            if (scores.Count == 0)
                return 0.75;
            
            if (scores.Count < 3)
                return scores.Min() * 0.95;
            
            var sortedScores = scores.OrderBy(s => s).ToList();
            var mean = scores.Average();
            var variance = scores.Select(s => Math.Pow(s - mean, 2)).Average();
            var stdDev = Math.Sqrt(variance);
            
            var threshold = Math.Max(mean - stdDev, sortedScores[sortedScores.Count / 4]);

            var minimumFloor = Math.Max(mean - 2 * stdDev, Configuration.ChromaprintMinimumScoreFloor);
            threshold = Math.Max(minimumFloor, Math.Min(0.85, threshold));
            
            LogDebug($"Threshold calculation: mean={mean:F3}, stdDev={stdDev:F3}, Q1={sortedScores[sortedScores.Count / 4]:F3}, minimumFloor={minimumFloor:F3}, final threshold={threshold:F3}");
            
            return threshold;
        }
        
        private (List<int> filteredOffsets, List<double> filteredScores) FilterOutliersIQR(List<int> offsets, List<double> scores)
        {
            if (offsets.Count <= 3)
                return (offsets, scores);
            
            var sortedOffsets = offsets.OrderBy(o => o).ToList();
            var n = sortedOffsets.Count;
            
            var q1Index = n / 4;
            var q3Index = (3 * n) / 4;
            var q1 = sortedOffsets[q1Index];
            var q3 = sortedOffsets[q3Index];
            var iqr = q3 - q1;
            
            var lowerBound = q1 - 1.5 * iqr;
            var upperBound = q3 + 1.5 * iqr;
            
            LogDebug($"IQR outlier detection: Q1={q1}, Q3={q3}, IQR={iqr}, bounds=[{lowerBound}, {upperBound}]");
            
            var filtered = new List<int>();
            var filteredScores = new List<double>();
            
            for (int i = 0; i < offsets.Count; i++)
            {
                if (offsets[i] >= lowerBound && offsets[i] <= upperBound)
                {
                    filtered.Add(offsets[i]);
                    filteredScores.Add(scores[i]);
                }
                else
                {
                    LogDebug($"Filtered outlier: offset={offsets[i]} (outside bounds)");
                }
            }
            
            return (filtered, filteredScores);
        }
        
        private double CalculateWeightedMedian(List<int> offsets, List<double> weights)
        {
            if (offsets.Count == 0)
                return 0;
            
            if (offsets.Count == 1)
                return offsets[0];
            
            var combined = offsets.Zip(weights, (o, w) => (offset: o, weight: w))
                .OrderBy(x => x.offset)
                .ToList();
            
            var totalWeight = weights.Sum();
            var cumulativeWeight = 0.0;
            var halfWeight = totalWeight / 2.0;
            
            for (int i = 0; i < combined.Count; i++)
            {
                cumulativeWeight += combined[i].weight;
                if (cumulativeWeight >= halfWeight)
                {
                    LogDebug($"Weighted median: offset={combined[i].offset} (weight={combined[i].weight:F3}, cumulative={cumulativeWeight:F3}/{totalWeight:F3})");
                    return combined[i].offset;
                }
            }
            
            return combined[combined.Count / 2].offset;
        }
        
        private async Task<double> RefineTimestampWithFineGrainedPass(
            string videoPath,
            double coarseTimestamp,
            uint[] referenceFingerprint,
            int referenceMatchOffset,
            CancellationToken cancellationToken)
        {
            try
            {
                var searchWindowBefore = 5.0;
                var searchWindowAfter = 5.0;
                var fineGrainedDuration = 15.0;
                
                var fineStartTime = Math.Max(0, coarseTimestamp - searchWindowBefore);
                var fineEndTime = coarseTimestamp + searchWindowAfter;
                
                LogDebug($"Fine-grained pass: searching {FormatTime(fineStartTime)} to {FormatTime(fineEndTime)} around coarse detection at {FormatTime(coarseTimestamp)}");
                
                var fineFingerprint = FFmpegHelper.GenerateChromaprint(videoPath, fineStartTime, fineStartTime + fineGrainedDuration, Logger, Configuration.ChromaprintFfmpegThreads);
                
                if (fineFingerprint.Length == 0)
                {
                    LogDebug("Fine-grained pass: Failed to generate fingerprint");
                    return 0;
                }
                
                return await Task.Run(() =>
                {
                    var coarseOffsetInFine = (int)((coarseTimestamp - fineStartTime) / 0.13);
                    var searchRange = 40;
                    
                    var startSearch = Math.Max(0, coarseOffsetInFine - searchRange);
                    var endSearch = Math.Min(fineFingerprint.Length - 50, coarseOffsetInFine + searchRange);

                    var scoredCandidates = new List<(int offset, double score)>();

                    for (int offset = startSearch; offset <= endSearch; offset += 2)
                    {
                        if (cancellationToken.IsCancellationRequested)
                            return 0;
                            
                        var windowSize = Math.Min(50, fineFingerprint.Length - offset);
                        if (windowSize < 20)
                            continue;

                        // Search ±10 positions in reference to absorb stride-10 alignment error from coarse pass.
                        var refBase = referenceMatchOffset + (offset - coarseOffsetInFine);
                        var localBest = 0.0;
                        for (int refDelta = -10; refDelta <= 10; refDelta++)
                        {
                            var refPos = refBase + refDelta;
                            if (refPos < 0 || refPos + windowSize > referenceFingerprint.Length)
                                continue;

                            int matchCount = 0;
                            for (int i = 0; i < windowSize; i++)
                            {
                                var xorResult = fineFingerprint[offset + i] ^ referenceFingerprint[refPos + i];
                                matchCount += 32 - System.Numerics.BitOperations.PopCount(xorResult);
                            }
                            var sc = (double)matchCount / (windowSize * 32);
                            if (sc > localBest)
                                localBest = sc;
                        }

                        if (localBest > 0)
                            scoredCandidates.Add((offset, localBest));
                    }

                    if (scoredCandidates.Count == 0)
                        return 0;

                    var peakScore = scoredCandidates.Max(s => s.score);

                    if (peakScore <= 0.80)
                    {
                        LogDebug($"Fine-grained pass: Peak score {peakScore:F3} below threshold, keeping coarse result");
                        return 0;
                    }

                    var onsetThreshold = peakScore * 0.97;
                    var onsetOffset = -1;
                    var onsetScore = 0.0;
                    foreach (var (off, score) in scoredCandidates)
                    {
                        if (score >= onsetThreshold)
                        {
                            onsetOffset = off;
                            onsetScore = score;
                            break;
                        }
                    }

                    if (onsetOffset < 0)
                        return 0;

                    var refinedTimestamp = fineStartTime + onsetOffset * 0.13;
                    LogDebug($"Fine-grained pass: Onset at offset {onsetOffset} (score={onsetScore:F3}, peak={peakScore:F3}), refined timestamp={FormatTime(refinedTimestamp)}");
                    return refinedTimestamp;
                    
                }, cancellationToken);
            }
            catch (Exception ex)
            {
                LogWarn($"Fine-grained refinement failed: {ex.Message}");
                return 0;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
            }
            base.Dispose(disposing);
        }
    }
}
