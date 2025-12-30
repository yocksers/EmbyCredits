using System;
using System.Threading;
using System.Threading.Tasks;

namespace EmbyCredits.Services.DetectionMethods
{

    public interface IDetectionMethod : IDisposable
    {

        string MethodName { get; }

        double Confidence { get; }

        int Priority { get; }

        bool IsEnabled { get; }

        Task<double> DetectCredits(string videoPath, double duration, CancellationToken cancellationToken = default);
        
        string GetLastError();
    }
}
