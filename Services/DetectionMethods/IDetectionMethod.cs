using System.Threading.Tasks;

namespace EmbyCredits.Services.DetectionMethods
{

    public interface IDetectionMethod
    {

        string MethodName { get; }

        double Confidence { get; }

        int Priority { get; }

        bool IsEnabled { get; }

        Task<double> DetectCredits(string videoPath, double duration);
    }
}
