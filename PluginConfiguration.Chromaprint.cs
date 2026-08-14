namespace EmbyCredits
{
    public partial class PluginConfiguration
    {
        public int ChromaprintDetectionPriority { get; set; } = 1;
        public bool ChromaprintUseAudioFingerprinting { get; set; } = false;
        public bool ChromaprintEnableBlackFrameFallback { get; set; } = true;
        public int ChromaprintFingerprintDuration { get; set; } = 360;
        public double ChromaprintFingerprintSimilarityThreshold { get; set; } = 0.90;

        public bool ChromaprintEnableEpisodeComparison { get; set; } = true;
        public double ChromaprintEpisodeComparisonTolerance { get; set; } = 15.0;
        public int ChromaprintEpisodeComparisonMinimumEpisodes { get; set; } = 4;

        public int ChromaprintMinDuration { get; set; } = 10;
        public int ChromaprintMaxDuration { get; set; } = 300;
        public double ChromaprintSimilarityThreshold { get; set; } = 0.85;
        public int ChromaprintMinEpisodeCount { get; set; } = 4;
        public double ChromaprintAnalysisPercent { get; set; } = 10.0;

        public double ChromaprintBlackFrameThreshold { get; set; } = 0.05;
        public double ChromaprintBlackFrameMinDuration { get; set; } = 0.5;

        public bool ChromaprintUseSilenceDetection { get; set; } = true;
        public int ChromaprintSilenceThreshold { get; set; } = -50;
        public double ChromaprintSilenceMinDuration { get; set; } = 0.5;
        public double ChromaprintSilenceSearchWindow { get; set; } = 30.0;

        public double ChromaprintMinConfidence { get; set; } = 0.85;
        public double ChromaprintMinimumScoreFloor { get; set; } = 0.55;
        public double ChromaprintStopSecondsFromEnd { get; set; } = 20.0;

        public bool ChromaprintLowerProcessPriority { get; set; } = false;
        public int ChromaprintFfmpegThreads { get; set; } = 2;
        public int ChromaprintParallelSessions { get; set; } = 2;
        public int ChromaprintDelayBetweenOperationsMs { get; set; } = 0;
    }
}
