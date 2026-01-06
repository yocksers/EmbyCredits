using MediaBrowser.Controller.Notifications;
using MediaBrowser.Model.Logging;
using Emby.Notifications;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace EmbyCredits.Services
{
    public class NotificationService
    {
        private readonly ILogger _logger;
        private readonly INotificationManager _notificationManager;
        private readonly PluginConfiguration _configuration;

        public NotificationService(ILogger logger, INotificationManager notificationManager, PluginConfiguration configuration)
        {
            _logger = logger;
            _notificationManager = notificationManager;
            _configuration = configuration;
        }

        public void SendScheduledTaskCompletionNotification(int successCount, int failedCount, int totalProcessed, List<string> failedEpisodes, List<string> successfulSeries, TimeSpan duration)
        {
            if (!_configuration.EnableScheduledTaskNotifications)
            {
                return;
            }

            if (_configuration.NotifyOnSuccessOnly && failedCount > 0)
            {
                return;
            }

            if (totalProcessed < _configuration.MinimumEpisodesForNotification)
            {
                return;
            }

            try
            {
                var severity = failedCount > 0 ? LogSeverity.Warn : LogSeverity.Info;
                var title = "Credits Detection Complete";
                var description = BuildNotificationDescription(successCount, failedCount, totalProcessed, failedEpisodes, successfulSeries, duration);

                var request = new NotificationRequest
                {
                    Title = title,
                    Description = description,
                    Severity = severity,
                    Date = DateTimeOffset.Now
                };

                _notificationManager.SendNotification(request);
                _logger.Info($"Sent credits detection completion notification: {successCount} successful, {failedCount} failed");
            }
            catch (Exception ex)
            {
                _logger.ErrorException("Failed to send notification", ex);
            }
        }

        public void SendAutoDetectionNotification(int successCount, int failedCount, int totalProcessed, List<string> failedEpisodes, List<string> successfulSeries, TimeSpan duration)
        {
            if (!_configuration.EnableAutoDetectionNotifications)
            {
                return;
            }

            if (_configuration.NotifyOnSuccessOnly && failedCount > 0)
            {
                return;
            }

            if (totalProcessed < _configuration.MinimumEpisodesForNotification)
            {
                return;
            }

            try
            {
                var severity = failedCount > 0 ? LogSeverity.Warn : LogSeverity.Info;
                var title = "Auto-Detection Complete";
                var description = BuildNotificationDescription(successCount, failedCount, totalProcessed, failedEpisodes, successfulSeries, duration);

                var request = new NotificationRequest
                {
                    Title = title,
                    Description = description,
                    Severity = severity,
                    Date = DateTimeOffset.Now
                };

                _notificationManager.SendNotification(request);
                _logger.Info($"Sent auto-detection completion notification: {successCount} successful, {failedCount} failed");
            }
            catch (Exception ex)
            {
                _logger.ErrorException("Failed to send auto-detection notification", ex);
            }
        }

        private string BuildNotificationDescription(int successCount, int failedCount, int totalProcessed, List<string> failedEpisodes, List<string> successfulSeries, TimeSpan duration)
        {
            var sb = new StringBuilder();
            
            sb.AppendLine($"Credits detection task completed in {FormatDuration(duration)}");
            sb.AppendLine();
            sb.AppendLine($"✓ Successfully processed: {successCount} episodes");
            
            if (successfulSeries != null && successfulSeries.Any())
            {
                sb.AppendLine();
                sb.AppendLine("TV Shows processed:");
                var sortedSeries = successfulSeries.OrderBy(s => s).ToList();
                var displayCount = Math.Min(15, sortedSeries.Count);
                for (int i = 0; i < displayCount; i++)
                {
                    sb.AppendLine($"  • {sortedSeries[i]}");
                }
                
                if (sortedSeries.Count > 15)
                {
                    sb.AppendLine($"  ... and {sortedSeries.Count - 15} more");
                }
            }
            
            if (failedCount > 0)
            {
                sb.AppendLine($"✗ Failed: {failedCount} episodes");
                
                if (failedEpisodes != null && failedEpisodes.Any())
                {
                    sb.AppendLine();
                    sb.AppendLine("Failed episodes:");
                    var displayCount = Math.Min(10, failedEpisodes.Count);
                    for (int i = 0; i < displayCount; i++)
                    {
                        sb.AppendLine($"  • {failedEpisodes[i]}");
                    }
                    
                    if (failedEpisodes.Count > 10)
                    {
                        sb.AppendLine($"  ... and {failedEpisodes.Count - 10} more");
                    }
                }
            }
            
            sb.AppendLine();
            sb.AppendLine($"Total processed: {totalProcessed} episodes");

            return sb.ToString();
        }

        private string FormatDuration(TimeSpan duration)
        {
            if (duration.TotalHours >= 1)
            {
                return $"{(int)duration.TotalHours}h {duration.Minutes}m";
            }
            else if (duration.TotalMinutes >= 1)
            {
                return $"{(int)duration.TotalMinutes}m {duration.Seconds}s";
            }
            else
            {
                return $"{duration.Seconds}s";
            }
        }
    }
}
