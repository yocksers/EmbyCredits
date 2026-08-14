namespace EmbyCredits
{
    public partial class DetectionRule
    {
        public double? ChromaprintAnalysisPercent { get; set; }
        public int? ChromaprintMinDuration { get; set; }
        public int? ChromaprintMaxDuration { get; set; }
        public int? ChromaprintFingerprintDuration { get; set; }
        public double? ChromaprintSimilarityThreshold { get; set; }
        public bool? ChromaprintEnableEpisodeComparison { get; set; }
        public double? ChromaprintEpisodeComparisonTolerance { get; set; }
        public int? ChromaprintEpisodeComparisonMinimumEpisodes { get; set; }
        public double? ChromaprintStopSecondsFromEnd { get; set; }
    }
}
