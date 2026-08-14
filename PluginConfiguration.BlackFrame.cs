namespace EmbyCredits
{
    public partial class PluginConfiguration
    {
        public bool EnableAnimeDetection { get; set; } = true;
        public AnimeDetectionMethod AnimeDetectionMethod { get; set; } = AnimeDetectionMethod.BlackFrame;

        public int BlackFrameMinimumPercentage { get; set; } = 85;
        public int BlackFrameThreshold { get; set; } = 28;
        public double BlackFrameMinDuration { get; set; } = 0.5;
        public double BlackFrameMinCreditsDuration { get; set; } = 15.0;
        public double BlackFrameMinimumDensity { get; set; } = 0.50;
        public double BlackFrameMaxCreditsDuration { get; set; } = 450.0;
        public double BlackFrameMaxSceneMergeGap { get; set; } = 20.0;
        public bool BlackFrameScanAllFrames { get; set; } = false;
        public bool BlackFrameAutoFallbackToAllFrames { get; set; } = true;
        public bool BlackFrameRefineCreditsBoundary { get; set; } = true;
        public int BlackFrameParallelSessions { get; set; } = 1;
        public int BlackFrameFfmpegThreads { get; set; } = 2;
    }
}
