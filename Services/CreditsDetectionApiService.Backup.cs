using MediaBrowser.Model.Entities;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using EmbyCredits.Api;
using EmbyCredits.Services;
using EmbyCredits.Services.Utilities;

namespace EmbyCredits.Services
{
    public partial class CreditsDetectionApiService
    {
        public object Get(GetBackupExportProgressRequest request)
        {
            try
            {
                var progress = Plugin.BackupExportProgress;

                return new
                {
                    Success = true,
                    IsRunning = progress.IsRunning,
                    TotalItems = progress.TotalItems,
                    ProcessedItems = progress.ProcessedItems,
                    SuccessfulItems = progress.SuccessfulItems,
                    FailedItems = progress.FailedItems,
                    CurrentItem = progress.CurrentItem,
                    PercentComplete = progress.PercentComplete,
                    EstimatedTimeRemainingSeconds = progress.EstimatedTimeRemaining?.TotalSeconds
                };
            }
            catch (Exception ex)
            {
                _logger?.ErrorException("Error getting backup export progress", ex);
                return new { Success = false, Message = ex.Message };
            }
        }

        public object Get(GetBackupImportProgressRequest request)
        {
            try
            {
                var progress = Plugin.BackupImportProgress;

                return new
                {
                    Success = true,
                    IsRunning = progress.IsRunning,
                    TotalItems = progress.TotalItems,
                    ProcessedItems = progress.ProcessedItems,
                    SuccessfulItems = progress.SuccessfulItems,
                    FailedItems = progress.FailedItems,
                    CurrentItem = progress.CurrentItem,
                    PercentComplete = progress.PercentComplete,
                    EstimatedTimeRemainingSeconds = progress.EstimatedTimeRemaining?.TotalSeconds
                };
            }
            catch (Exception ex)
            {
                _logger?.ErrorException("Error getting backup import progress", ex);
                return new { Success = false, Message = ex.Message };
            }
        }

        public async Task<object> Post(ExportCreditsBackupRequest request)
        {
            try
            {
                if (Plugin.Instance?.Configuration?.EnableDetailedLogging == true)
                {
                    _logger?.Info("Credits backup export requested");
                }

                var backupService = Plugin.CreditsBackupService;
                if (backupService == null)
                {
                    return new { Success = false, Message = "Backup service not initialized" };
                }

                var result = await backupService.ExportCreditsMarkers(
                    request.LibraryIds,
                    request.SeriesIds
                );

                if (!result.Success || string.IsNullOrEmpty(result.JsonData))
                {
                    return new { Success = false, Message = result.Message };
                }

                var bytes = System.Text.Encoding.UTF8.GetBytes(result.JsonData);
                var stream = new MemoryStream(bytes);
                stream.Position = 0;
                return stream;
            }
            catch (Exception ex)
            {
                _logger?.ErrorException("Error exporting credits backup", ex);
                return new { Success = false, Message = ex.Message };
            }
        }

        public async Task<object> Post(ImportCreditsBackupRequest request)
        {
            try
            {
                if (Plugin.Instance?.Configuration?.EnableDetailedLogging == true)
                {
                    _logger?.Info("Credits backup import requested");
                }

                if (string.IsNullOrEmpty(request.JsonData))
                {
                    return new { Success = false, Message = "No backup data provided" };
                }

                if (request.JsonData.Length > MaxImportBytes)
                {
                    return new { Success = false, Message = $"Import data exceeds maximum allowed size of {MaxImportBytes / (1024 * 1024)} MB" };
                }

                var backupService = Plugin.CreditsBackupService;
                if (backupService == null)
                {
                    return new { Success = false, Message = "Backup service not initialized" };
                }

                var result = await backupService.ImportCreditsMarkers(
                    request.JsonData,
                    request.OverwriteExisting
                );

                return new
                {
                    Success = result.Success,
                    Message = result.Message,
                    ItemsImported = result.ItemsImported,
                    ItemsSkipped = result.ItemsSkipped,
                    ItemsNotFound = result.ItemsNotFound
                };
            }
            catch (Exception ex)
            {
                _logger?.ErrorException("Error importing credits backup", ex);
                return new { Success = false, Message = ex.Message };
            }
        }

        public object Post(ClearZeroCreditsMarkersRequest request)
        {
            try
            {
                if (Plugin.ClearZeroCreditsProgress.IsRunning)
                {
                    return new { Success = false, Message = "A clear-markers scan is already running" };
                }

                if (Plugin.Instance?.Configuration?.EnableDetailedLogging == true)
                {
                    _logger?.Info("Clear invalid (0:00) credits markers requested");
                }

                var backupService = Plugin.CreditsBackupService;
                if (backupService == null)
                {
                    return new { Success = false, Message = "Backup service not initialized" };
                }

                _ = Task.Run(async () =>
                {
                    try
                    {
                        await backupService.ClearZeroCreditsMarkers().ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger?.ErrorException("Error clearing invalid credits markers", ex);
                    }
                });

                return new { Success = true, Message = "Started" };
            }
            catch (Exception ex)
            {
                _logger?.ErrorException("Error starting clear invalid credits markers task", ex);
                return new { Success = false, Message = ex.Message };
            }
        }

        public object Get(GetClearZeroCreditsProgressRequest request)
        {
            try
            {
                var progress = Plugin.ClearZeroCreditsProgress;

                return new
                {
                    Success = progress.FailedItems == 0,
                    IsRunning = progress.IsRunning,
                    TotalItems = progress.TotalItems,
                    ProcessedItems = progress.ProcessedItems,
                    ClearedCount = progress.SuccessfulItems,
                    CurrentItem = progress.CurrentItem,
                    PercentComplete = progress.PercentComplete
                };
            }
            catch (Exception ex)
            {
                _logger?.ErrorException("Error getting clear invalid credits markers progress", ex);
                return new { Success = false, Message = ex.Message };
            }
        }

        public async Task<object> Post(BulkExportToFolderRequest request)
        {
            try
            {
                var config = Plugin.Instance?.Configuration;
                if (config == null)
                    return new { Success = false, Message = "Plugin configuration not available" };

                if (string.IsNullOrWhiteSpace(config.BackupFolderPath))
                    return new { Success = false, Message = "Backup folder path is not configured. Please set it in Settings before using bulk export." };

                var backupService = Plugin.CreditsBackupService;
                if (backupService == null)
                    return new { Success = false, Message = "Backup service not initialized" };

                var maxBackups = config.MaxScheduledBackups > 0 ? config.MaxScheduledBackups : 10;

                var result = await backupService.ExportCreditsMarkers(
                    null,
                    request.SeriesIds,
                    CancellationToken.None,
                    config.BackupFolderPath,
                    maxBackups);

                return new
                {
                    Success = result.Success,
                    Message = result.Message,
                    TotalEpisodes = result.TotalEpisodes,
                    EpisodesWithCredits = result.EpisodesWithCredits,
                    FolderPath = config.BackupFolderPath
                };
            }
            catch (Exception ex)
            {
                _logger?.ErrorException("Error during bulk export to folder", ex);
                return new { Success = false, Message = ex.Message };
            }
        }

        public async Task<object> Get(ExportSeriesCreditsRequest request)
        {
            try
            {
                if (string.IsNullOrEmpty(request.SeriesId))
                {
                    return new { Success = false, Message = "SeriesId is required" };
                }

                if (Plugin.Instance?.Configuration?.EnableDetailedLogging == true)
                {
                    _logger?.Info($"Single series credits export requested for SeriesId: {request.SeriesId}");
                }

                var series = ItemLookupHelper.ResolveSeries(_libraryManager, request.SeriesId, _logger);
                if (series == null)
                {
                    return new { Success = false, Message = "Series not found" };
                }

                var backupService = Plugin.CreditsBackupService;
                if (backupService == null)
                {
                    return new { Success = false, Message = "Backup service not initialized" };
                }

                var result = await backupService.ExportCreditsMarkers(
                    null,
                    new List<string> { request.SeriesId }
                );

                if (!result.Success || string.IsNullOrEmpty(result.JsonData))
                {
                    return new { Success = false, Message = result.Message };
                }

                var bytes = System.Text.Encoding.UTF8.GetBytes(result.JsonData);
                var stream = new MemoryStream(bytes);
                stream.Position = 0;
                return ToStaticResult(stream);
            }
            catch (Exception ex)
            {
                _logger?.ErrorException("Error exporting series credits", ex);
                return new { Success = false, Message = ex.Message };
            }
        }

        public async Task<object> Post(ImportSeriesCreditsRequest request)
        {
            try
            {
                if (string.IsNullOrEmpty(request.SeriesId))
                {
                    return new { Success = false, Message = "SeriesId is required" };
                }

                if (string.IsNullOrEmpty(request.JsonData))
                {
                    return new { Success = false, Message = "No backup data provided" };
                }

                if (request.JsonData.Length > MaxImportBytes)
                {
                    return new { Success = false, Message = $"Import data exceeds maximum allowed size of {MaxImportBytes / (1024 * 1024)} MB" };
                }

                _logger?.Info($"Single series credits import requested for SeriesId: {request.SeriesId}");

                var series = ItemLookupHelper.ResolveSeries(_libraryManager, request.SeriesId, _logger);
                if (series == null)
                {
                    return new { Success = false, Message = "Series not found" };
                }

                var backupService = Plugin.CreditsBackupService;
                if (backupService == null)
                {
                    return new { Success = false, Message = "Backup service not initialized" };
                }

                var result = await backupService.ImportCreditsMarkers(
                    request.JsonData,
                    request.OverwriteExisting
                );

                return new
                {
                    Success = result.Success,
                    Message = result.Message,
                    ItemsImported = result.ItemsImported,
                    ItemsSkipped = result.ItemsSkipped,
                    ItemsNotFound = result.ItemsNotFound
                };
            }
            catch (Exception ex)
            {
                _logger?.ErrorException("Error importing series credits", ex);
                return new { Success = false, Message = ex.Message };
            }
        }
    }
}
