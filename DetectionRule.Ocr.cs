namespace EmbyCredits
{
    public partial class DetectionRule
    {
        public OcrEngine? OcrEngine { get; set; }
        public double? OcrSearchStartValue { get; set; }
        public double? OcrMinutesFromEnd { get; set; }
        public double? OcrFrameRate { get; set; }
        public int? OcrMinimumMatches { get; set; }
        public int? OcrMaxFramesToProcess { get; set; }
        public double? OcrMaxAnalysisDuration { get; set; }
        public double? OcrStopSecondsFromEnd { get; set; }
        public string? OcrDetectionKeywords { get; set; }

        public bool? OcrEnableEpisodeComparison { get; set; }
        public double? OcrEpisodeComparisonTolerance { get; set; }
        public int? OcrEpisodeComparisonMinimumEpisodes { get; set; }

        public bool? OcrEnableCharacterDensityDetection { get; set; }
        public int? OcrCharacterDensityThreshold { get; set; }
        public int? OcrCharacterDensityConsecutiveFrames { get; set; }
        public bool? OcrCharacterDensityPrimaryMethod { get; set; }
        public bool? OcrDensityRequireKeyword { get; set; }
        public double? OcrDensityKeywordWindowSeconds { get; set; }
        public bool? OcrDensityRequireTemporalConsistency { get; set; }
        public double? OcrDensityMinimumDurationSeconds { get; set; }
        public bool? OcrDensityRequireStyleConsistency { get; set; }
        public double? OcrDensityStyleConsistencyThreshold { get; set; }

        public string? OcrLanguages { get; set; }
        public int? OcrPageSegmentationMode { get; set; }
        public int? OcrEngineMode { get; set; }
        public bool? OcrPreserveInterwordSpaces { get; set; }
        public double? OcrMinimumConfidence { get; set; }
        public bool? OcrEnableSmartFrameSkipping { get; set; }
        public int? OcrConsecutiveMatchesForEarlyStop { get; set; }

        public bool? OcrEnableImagePreprocessing { get; set; }
        public double? OcrContrastEnhancement { get; set; }
        public double? OcrBrightnessAdjustment { get; set; }
        public bool? OcrEnableSharpening { get; set; }
        public double? OcrSharpenAmount { get; set; }

        public bool? OcrEnableRoiDetection { get; set; }
        public string? OcrRoiRegion { get; set; }

        public bool? OcrEnableFuzzyMatching { get; set; }
        public int? OcrFuzzyMatchMaxDistance { get; set; }

        public bool? OcrEnableScrollingDetection { get; set; }
        public int? OcrScrollingMinFrames { get; set; }
        public double? OcrScrollingOverlapThreshold { get; set; }

        public bool? OcrEnableAdaptiveFrameRate { get; set; }
        public double? OcrAdaptiveFrameRateMin { get; set; }
        public bool? OcrAdaptiveSamplingEnabled { get; set; }
        public double? OcrAdaptiveCoarseIntervalSeconds { get; set; }
        public double? OcrAdaptiveRefinementRadiusSeconds { get; set; }

        public bool? OcrEnableCreditStructureDetection { get; set; }
        public int? OcrMinimumStructureLines { get; set; }
    }
}
