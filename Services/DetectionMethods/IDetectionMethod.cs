using System.Threading.Tasks;

namespace EmbyCredits.Services.DetectionMethods
{
    /// <summary>
    /// Interface for all credit detection methods
    /// </summary>
    public interface IDetectionMethod
    {
        /// <summary>
        /// Name of the detection method (for logging and correlation)
        /// </summary>
        string MethodName { get; }

        /// <summary>
        /// Base confidence level for this detection method (0.0 - 1.0)
        /// </summary>
        double Confidence { get; }

        /// <summary>
        /// Priority level (1 = highest priority)
        /// </summary>
        int Priority { get; }

        /// <summary>
        /// Whether this method is enabled in configuration
        /// </summary>
        bool IsEnabled { get; }

        /// <summary>
        /// Detect credits in a video file
        /// </summary>
        /// <param name="videoPath">Path to the video file</param>
        /// <param name="duration">Duration of the video in seconds</param>
        /// <returns>Timestamp of credits start in seconds, or 0 if not detected</returns>
        Task<double> DetectCredits(string videoPath, double duration);
    }
}
