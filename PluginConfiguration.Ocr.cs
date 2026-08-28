namespace EmbyCredits
{
    public partial class PluginConfiguration
    {
        public OcrEngine OcrEngine { get; set; } = OcrEngine.Tesseract;
        public string OcrEndpoint { get; set; } = "http://localhost:8884";
        public string LocalTesseractPath { get; set; } = "";
        public string OcrDetectionKeywords { get; set; } = "associate producer,based on,cast,casting,cinematography,co-producer,composer,costume design,created by,credits,developed by,directed by,director of photography,editing,editor,end credits,ende,executive producer,fim,fin,fine,guest starring,music by,produced by,producer,production company,production design,screenplay,series producer,sound,special thanks,starring,story by,the end,visual effects,written by,끝,終,キャスト,スタッフ,監督,脚本,音楽,製作,制作,プロデューサー,原作,演出,撮影,編集,おわり,提供,協力,出演";

        public string OcrLanguages { get; set; } = "eng+jpn";
        public int OcrPageSegmentationMode { get; set; } = 3;
        public int OcrEngineMode { get; set; } = 3;
        public bool OcrPreserveInterwordSpaces { get; set; } = true;

        public bool OcrEnableEpisodeComparison { get; set; } = true;
        public double OcrEpisodeComparisonTolerance { get; set; } = 20.0;
        public int OcrEpisodeComparisonMinimumEpisodes { get; set; } = 4;
        public string OcrSearchStartUnit { get; set; } = "minutes";
        public double OcrSearchStartValue { get; set; } = 3.0;
        public double OcrDetectionSearchStart { get; set; } = 0.65;
        public double OcrMinutesFromEnd { get; set; } = 3.0;

        public double OcrFrameRate { get; set; } = 0.5;
        public int OcrMinimumMatches { get; set; } = 1;
        public int OcrMaxFramesToProcess { get; set; } = 0;
        public double OcrMaxAnalysisDuration { get; set; } = 600.0;
        public double OcrStopSecondsFromEnd { get; set; } = 20.0;
        public int OcrJpegQuality { get; set; } = 92;
        public int OcrMaxResolutionHeight { get; set; } = 1080;
        public int OcrDelayBetweenFramesMs { get; set; } = 0;

        public bool OcrEnableParallelProcessing { get; set; } = false;
        public int OcrParallelBatchSize { get; set; } = 4;
        public int OcrDelayBetweenBatchesMs { get; set; } = 200;
        public bool OcrEnableSmartFrameSkipping { get; set; } = true;
        public int OcrConsecutiveMatchesForEarlyStop { get; set; } = 3;
        public double OcrMinimumConfidence { get; set; } = 0.0;

        public bool OcrEnableCharacterDensityDetection { get; set; } = true;
        public int OcrCharacterDensityThreshold { get; set; } = 20;
        public int OcrCharacterDensityConsecutiveFrames { get; set; } = 3;
        public bool OcrCharacterDensityPrimaryMethod { get; set; } = true;
        public bool OcrDensityRequireKeyword { get; set; } = true;
        public double OcrDensityKeywordWindowSeconds { get; set; } = 10.0;
        public bool OcrDensityRequireTemporalConsistency { get; set; } = true;
        public double OcrDensityMinimumDurationSeconds { get; set; } = 15.0;
        public bool OcrDensityRequireStyleConsistency { get; set; } = true;
        public double OcrDensityStyleConsistencyThreshold { get; set; } = 0.7;

        public int OcrRetryAttempts { get; set; } = 5;
        public int OcrRetryDelayMs { get; set; } = 2000;

        public string OcrFfmpegPreInputArgs { get; set; } = "";
        public int OcrFfmpegThreads { get; set; } = 0;
        public int OcrFfmpegFilterThreads { get; set; } = 0;
        public bool OcrEnableHardwareAcceleration { get; set; } = false;
        public string OcrHardwareAccelerationType { get; set; } = "none";
        public string OcrHardwareDevice { get; set; } = "";
        public bool OcrUseHardwareOutputFormat { get; set; } = true;
        public bool OcrUseHardwareFilters { get; set; } = true;
        public bool OcrUseDirectMemoryPipeline { get; set; } = true;

        public bool OcrEnableImagePreprocessing { get; set; } = false;
        public double OcrContrastEnhancement { get; set; } = 1.5;
        public double OcrBrightnessAdjustment { get; set; } = 0.05;
        public bool OcrEnableSharpening { get; set; } = false;
        public double OcrSharpenAmount { get; set; } = 1.0;

        public bool OcrEnableRoiDetection { get; set; } = false;
        public string OcrRoiRegion { get; set; } = "full";

        public bool OcrEnableFuzzyMatching { get; set; } = false;
        public int OcrFuzzyMatchMaxDistance { get; set; } = 2;

        public bool OcrEnableScrollingDetection { get; set; } = false;
        public int OcrScrollingMinFrames { get; set; } = 5;
        public double OcrScrollingOverlapThreshold { get; set; } = 0.3;

        public bool OcrEnableAdaptiveFrameRate { get; set; } = false;
        public double OcrAdaptiveFrameRateMin { get; set; } = 0.25;
        public bool OcrAdaptiveSamplingEnabled { get; set; } = false;
        public double OcrAdaptiveCoarseIntervalSeconds { get; set; } = 5.0;
        public double OcrAdaptiveRefinementRadiusSeconds { get; set; } = 10.0;

        public bool OcrEnableCreditStructureDetection { get; set; } = false;
        public int OcrMinimumStructureLines { get; set; } = 4;

        public bool OcrEnableConcurrentFiles { get; set; } = false;
        public int OcrConcurrentFiles { get; set; } = 2;
    }
}
