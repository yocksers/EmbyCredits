namespace EmbyCredits
{
    public partial class DetectionRule
    {
        public bool? EnableAnimeDetection { get; set; }
        public AnimeDetectionMethod? AnimeDetectionMethod { get; set; }

        public int? BlackFrameMinimumPercentage { get; set; }
        public int? BlackFrameThreshold { get; set; }
        public double? BlackFrameMinimumDensity { get; set; }
        public double? BlackFrameMaxCreditsDuration { get; set; }
        public double? BlackFrameMaxSceneMergeGap { get; set; }
        public bool? BlackFrameScanAllFrames { get; set; }
        public bool? BlackFrameAutoFallbackToAllFrames { get; set; }
        public bool? BlackFrameRefineCreditsBoundary { get; set; }
        public double? BlackFrameMinDuration { get; set; }
    }
}
