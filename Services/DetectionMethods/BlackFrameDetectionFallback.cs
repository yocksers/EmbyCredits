using MediaBrowser.Model.Logging;

namespace EmbyCredits.Services.DetectionMethods
{
    public class BlackFrameDetectionFallback : BlackFrameDetection
    {
        public override string MethodName => "BlackFrame (Fallback)";

        public BlackFrameDetectionFallback(ILogger logger, PluginConfiguration configuration)
            : base(logger, configuration)
        {
        }
    }
}
