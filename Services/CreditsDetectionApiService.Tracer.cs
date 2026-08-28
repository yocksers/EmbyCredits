using System;
using System.Linq;
using EmbyCredits.Api;

namespace EmbyCredits.Services
{
    public partial class CreditsDetectionApiService
    {
        public object Get(GetTracerEpisodesRequest request)
        {
            try
            {
                var tracer = Plugin.TracerService;
                if (tracer == null)
                    return new { Success = false, Message = "Tracer service not available" };

                var entries = tracer.GetAll().Select(e => new
                {
                    e.EpisodeId,
                    e.SeriesName,
                    e.SeasonNumber,
                    e.EpisodeNumber,
                    e.EpisodeName,
                    AddedUtc = e.AddedUtc.ToString("o")
                }).ToList();

                var detected = tracer.GetAllDetected().Select(e => new
                {
                    e.EpisodeId,
                    e.SeriesName,
                    e.SeasonNumber,
                    e.EpisodeNumber,
                    e.EpisodeName,
                    AddedUtc = e.AddedUtc.ToString("o"),
                    DetectedUtc = e.DetectedUtc?.ToString("o")
                }).ToList();

                var failed = tracer.GetAllFailed().Select(e => new
                {
                    e.EpisodeId,
                    e.SeriesName,
                    e.SeasonNumber,
                    e.EpisodeNumber,
                    e.EpisodeName,
                    AddedUtc = e.AddedUtc.ToString("o"),
                    FailedUtc = e.FailedUtc?.ToString("o"),
                    e.FailureReason
                }).ToList();

                return new { Success = true, Count = entries.Count, Episodes = entries, Detected = detected, Failed = failed };
            }
            catch (Exception ex)
            {
                _logger?.ErrorException("Error getting tracer episodes", ex);
                return new { Success = false, Message = ex.Message };
            }
        }

        public object Post(DismissTracerEpisodeRequest request)
        {
            try
            {
                if (string.IsNullOrEmpty(request.EpisodeId))
                    return new { Success = false, Message = "EpisodeId is required" };

                Plugin.TracerService?.Remove(request.EpisodeId);
                return new { Success = true };
            }
            catch (Exception ex)
            {
                _logger?.ErrorException("Error dismissing tracer episode", ex);
                return new { Success = false, Message = ex.Message };
            }
        }

        public object Post(ClearTracerListRequest request)
        {
            try
            {
                Plugin.TracerService?.Clear();
                return new { Success = true };
            }
            catch (Exception ex)
            {
                _logger?.ErrorException("Error clearing tracer list", ex);
                return new { Success = false, Message = ex.Message };
            }
        }

        public object Post(ClearDetectedTracerListRequest request)
        {
            try
            {
                Plugin.TracerService?.ClearDetected();
                return new { Success = true };
            }
            catch (Exception ex)
            {
                _logger?.ErrorException("Error clearing detected tracer list", ex);
                return new { Success = false, Message = ex.Message };
            }
        }

        public object Post(ClearFailedTracerListRequest request)
        {
            try
            {
                Plugin.TracerService?.ClearFailed();
                return new { Success = true };
            }
            catch (Exception ex)
            {
                _logger?.ErrorException("Error clearing failed tracer list", ex);
                return new { Success = false, Message = ex.Message };
            }
        }
    }
}
