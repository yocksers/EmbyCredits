define(['loading', 'toast'], function (loading, toast) {
    'use strict';

    const pluginId = "b1a65a73-a620-432a-9f5b-285038031c26";

    function loadData(instance, view) {
        loading.show();
        ApiClient.getPluginConfiguration(pluginId).then(config => {
            instance.config = config;

            view.querySelector('#chkEnableOcrDetection').checked = config.EnableOcrDetection !== false;
            view.querySelector('#chkEnableHashDetection').checked = config.EnableChromaprintDetection || false;
            view.querySelector('#chkEnableAutoDetection').checked = config.EnableAutoDetection || false;
            view.querySelector('#chkUseEpisodeComparison').checked = config.UseEpisodeComparison || false;
            view.querySelector('#chkEnableFailedEpisodeFallback').checked = config.EnableFailedEpisodeFallback || false;
            view.querySelector('#txtMinimumSuccessRateForFallback').value = config.MinimumSuccessRateForFallback || 0.5;
            view.querySelector('#chkEnableDetailedLogging').checked = config.EnableDetailedLogging || false;
            view.querySelector('#chkScheduledTaskOnlyProcessMissing').checked = config.ScheduledTaskOnlyProcessMissing !== false;
            view.querySelector('#chkManualSkipExistingMarkers').checked = config.ManualSkipExistingMarkers || false;

            view.querySelector('#chkPreventConcurrentPluginProcessing').checked = config.PreventConcurrentPluginProcessing !== false;
            view.querySelector('#chkLowerThreadPriority').checked = config.LowerThreadPriority || false;
            view.querySelector('#chkLowerProcessPriority').checked = config.LowerProcessPriority || false;
            view.querySelector('#txtCpuUsageLimit').value = config.CpuUsageLimit !== undefined ? config.CpuUsageLimit : 100;
            view.querySelector('#txtCpuThrottleDelayMs').value = config.CpuThrottleDelayMs !== undefined ? config.CpuThrottleDelayMs : 100;
            view.querySelector('#txtDelayBetweenEpisodesMs').value = config.DelayBetweenEpisodesMs || 0;
            view.querySelector('#txtTempFolderPath').value = config.TempFolderPath || '';

            view.querySelector('#txtOcrEndpoint').value = config.OcrEndpoint || 'http://localhost:8884';
            view.querySelector('#txtOcrDetectionKeywords').value = config.OcrDetectionKeywords || 'directed by,produced by,executive producer,written by,cast,credits,fin,ende,終,끝,fim,fine';
                        view.querySelector('#txtOcrRetryAttempts').value = config.OcrRetryAttempts !== undefined ? config.OcrRetryAttempts : 5;
            view.querySelector('#txtOcrRetryDelayMs').value = config.OcrRetryDelayMs !== undefined ? config.OcrRetryDelayMs : 2000;
                        // Load unified search start setting (with legacy fallback)
            const searchStartUnit = config.OcrSearchStartUnit || 'minutes';
            let searchStartValue;
            if (config.OcrSearchStartValue !== undefined) {
                searchStartValue = config.OcrSearchStartValue;
            } else if (config.OcrMinutesFromEnd > 0) {
                // Legacy: had minutes from end
                searchStartValue = config.OcrMinutesFromEnd;
            } else {
                // Legacy: using percentage
                searchStartValue = (config.OcrDetectionSearchStart || 0.65) * 100;
            }
            
            view.querySelector('#selectOcrSearchStartUnit').value = searchStartUnit;
            view.querySelector('#txtOcrSearchStartValue').value = searchStartValue;
            updateSearchStartDescription(view, searchStartUnit);
            
            view.querySelector('#txtOcrFrameRate').value = config.OcrFrameRate || 0.5;
            view.querySelector('#txtOcrMinimumMatches').value = config.OcrMinimumMatches || 1;
            view.querySelector('#txtOcrMaxFramesToProcess').value = config.OcrMaxFramesToProcess || 0;
            view.querySelector('#txtOcrMaxAnalysisDuration').value = config.OcrMaxAnalysisDuration || 600;
            view.querySelector('#txtOcrStopSecondsFromEnd').value = config.OcrStopSecondsFromEnd || 20;
            view.querySelector('#txtOcrJpegQuality').value = config.OcrJpegQuality || 92;
            view.querySelector('#selectOcrMaxResolutionHeight').value = config.OcrMaxResolutionHeight !== undefined ? config.OcrMaxResolutionHeight : 1080;
            view.querySelector('#txtOcrDelayBetweenFramesMs').value = config.OcrDelayBetweenFramesMs || 0;

            view.querySelector('#txtOcrFfmpegThreads').value = config.OcrFfmpegThreads || 0;
            view.querySelector('#txtOcrFfmpegFilterThreads').value = config.OcrFfmpegFilterThreads || 0;
            view.querySelector('#chkOcrEnableHardwareAcceleration').checked = config.OcrEnableHardwareAcceleration || false;
            view.querySelector('#selectOcrHardwareAccelerationType').value = config.OcrHardwareAccelerationType || 'none';
            view.querySelector('#txtOcrHardwareDevice').value = config.OcrHardwareDevice || '';
            view.querySelector('#chkOcrUseHardwareOutputFormat').checked = config.OcrUseHardwareOutputFormat !== false;
            view.querySelector('#chkOcrUseHardwareFilters').checked = config.OcrUseHardwareFilters !== false;
            view.querySelector('#chkOcrUseDirectMemoryPipeline').checked = config.OcrUseDirectMemoryPipeline !== false;
            view.querySelector('#txtOcrFfmpegPreInputArgs').value = config.OcrFfmpegPreInputArgs || '';

            view.querySelector('#chkOcrEnableParallelProcessing').checked = config.OcrEnableParallelProcessing || false;
            view.querySelector('#txtOcrParallelBatchSize').value = config.OcrParallelBatchSize || 4;
            view.querySelector('#txtOcrDelayBetweenBatchesMs').value = config.OcrDelayBetweenBatchesMs ?? 200;
            view.querySelector('#chkOcrEnableSmartFrameSkipping').checked = config.OcrEnableSmartFrameSkipping !== false;
            view.querySelector('#txtOcrConsecutiveMatchesForEarlyStop').value = config.OcrConsecutiveMatchesForEarlyStop || 3;
            view.querySelector('#txtOcrMinimumConfidence').value = config.OcrMinimumConfidence || 0;

            // Character Density Detection settings
            view.querySelector('#chkOcrEnableCharacterDensityDetection').checked = config.OcrEnableCharacterDensityDetection !== false;
            view.querySelector('#txtOcrCharacterDensityThreshold').value = config.OcrCharacterDensityThreshold || 20;
            view.querySelector('#txtOcrCharacterDensityConsecutiveFrames').value = config.OcrCharacterDensityConsecutiveFrames || 3;
            view.querySelector('#chkOcrCharacterDensityPrimaryMethod').checked = config.OcrCharacterDensityPrimaryMethod !== false;
            
            // Density Detection Filters
            view.querySelector('#chkOcrDensityRequireKeyword').checked = config.OcrDensityRequireKeyword !== false;
            view.querySelector('#txtOcrDensityKeywordWindowSeconds').value = config.OcrDensityKeywordWindowSeconds || 10;
            view.querySelector('#chkOcrDensityRequireTemporalConsistency').checked = config.OcrDensityRequireTemporalConsistency !== false;
            view.querySelector('#txtOcrDensityMinimumDurationSeconds').value = config.OcrDensityMinimumDurationSeconds || 15;
            view.querySelector('#chkOcrDensityRequireStyleConsistency').checked = config.OcrDensityRequireStyleConsistency !== false;
            view.querySelector('#txtOcrDensityStyleConsistencyThreshold').value = config.OcrDensityStyleConsistencyThreshold || 0.7;

            // OCR Enhancements - Image Preprocessing
            view.querySelector('#chkOcrEnableImagePreprocessing').checked = config.OcrEnableImagePreprocessing || false;
            view.querySelector('#txtOcrContrastEnhancement').value = config.OcrContrastEnhancement || 1.5;
            view.querySelector('#txtOcrBrightnessAdjustment').value = config.OcrBrightnessAdjustment || 0.05;
            view.querySelector('#chkOcrEnableSharpening').checked = config.OcrEnableSharpening || false;
            view.querySelector('#txtOcrSharpenAmount').value = config.OcrSharpenAmount || 1.0;

            // Region of Interest
            view.querySelector('#chkOcrEnableRoiDetection').checked = config.OcrEnableRoiDetection || false;
            view.querySelector('#selectOcrRoiRegion').value = config.OcrRoiRegion || 'full';

            // Fuzzy Keyword Matching
            view.querySelector('#chkOcrEnableFuzzyMatching').checked = config.OcrEnableFuzzyMatching || false;
            view.querySelector('#txtOcrFuzzyMatchMaxDistance').value = config.OcrFuzzyMatchMaxDistance || 2;

            // Scrolling Pattern Detection
            view.querySelector('#chkOcrEnableScrollingDetection').checked = config.OcrEnableScrollingDetection || false;
            view.querySelector('#txtOcrScrollingMinFrames').value = config.OcrScrollingMinFrames || 5;
            view.querySelector('#txtOcrScrollingOverlapThreshold').value = config.OcrScrollingOverlapThreshold || 0.3;

            // Adaptive Frame Rate
            view.querySelector('#chkOcrEnableAdaptiveFrameRate').checked = config.OcrEnableAdaptiveFrameRate || false;
            view.querySelector('#txtOcrAdaptiveFrameRateMin').value = config.OcrAdaptiveFrameRateMin || 0.25;

            // Credit Structure Detection
            view.querySelector('#chkOcrEnableCreditStructureDetection').checked = config.OcrEnableCreditStructureDetection || false;
            view.querySelector('#txtOcrMinimumStructureLines').value = config.OcrMinimumStructureLines || 4;

            view.querySelector('#chkBackupImportOverwriteExisting').checked = config.BackupImportOverwriteExisting || false;
            view.querySelector('#txtBackupFolderPath').value = config.BackupFolderPath || '';
            view.querySelector('#txtMaxScheduledBackups').value = config.MaxScheduledBackups !== null && config.MaxScheduledBackups !== undefined ? config.MaxScheduledBackups : 10;

            view.querySelector('#chkEnableScheduledTaskNotifications').checked = config.EnableScheduledTaskNotifications || false;
            view.querySelector('#chkEnableAutoDetectionNotifications').checked = config.EnableAutoDetectionNotifications || false;
            view.querySelector('#chkNotifyOnSuccessOnly').checked = config.NotifyOnSuccessOnly || false;
            view.querySelector('#txtMinimumEpisodesForNotification').value = config.MinimumEpisodesForNotification !== null && config.MinimumEpisodesForNotification !== undefined ? config.MinimumEpisodesForNotification : 1;

            view.querySelector('#chkEnableThumbnailGeneration').checked = config.EnableThumbnailGeneration !== false;
            view.querySelector('#txtThumbnailWidth').value = config.ThumbnailWidth || 320;
            view.querySelector('#txtThumbnailQuality').value = config.ThumbnailQuality || 85;

            view.querySelector('#txtChromaprintMinDuration').value = config.ChromaprintMinDuration || 30;
            view.querySelector('#txtChromaprintMaxDuration').value = config.ChromaprintMaxDuration || 300;
            view.querySelector('#txtChromaprintAnalysisPercent').value = config.ChromaprintAnalysisPercent || 25;
            view.querySelector('#txtChromaprintBlackFrameThreshold').value = config.ChromaprintBlackFrameThreshold || 0.05;
            view.querySelector('#txtChromaprintBlackFrameMinDuration').value = config.ChromaprintBlackFrameMinDuration || 0.5;
            view.querySelector('#txtChromaprintSilenceThreshold').value = config.ChromaprintSilenceThreshold || -60;
            view.querySelector('#txtChromaprintSilenceMinDuration').value = config.ChromaprintSilenceMinDuration || 0.5;
            view.querySelector('#txtChromaprintMinConfidence').value = config.ChromaprintMinConfidence || 0.85;
            view.querySelector('#txtChromaprintStopSecondsFromEnd').value = config.ChromaprintStopSecondsFromEnd || 20;

            // Load libraries and series/episode dropdowns
            require(['configurationpage?name=CreditsDetectorConfigurationSeriesManager'], (seriesManager) => {
                seriesManager.loadLibraries(view, config);
                seriesManager.loadLibraryFilter(view);
                seriesManager.loadSeriesList(view);
            });
            
            setupUnitChangeListener(view);
            setupHardwareAccelerationListeners(view);

            setTimeout(() => {
                const event = new CustomEvent('keywordsLoaded');
                view.dispatchEvent(event);
            }, 100);

            // Trigger detection method color update after data is loaded
            setTimeout(() => {
                const event = new CustomEvent('detectionMethodLoaded');
                view.dispatchEvent(event);
            }, 100);

            return ApiClient.getJSON(ApiClient.getUrl('CreditsDetector/GetProgress')).catch(err => {
                console.warn('Progress endpoint not available:', err);
                return { IsRunning: false };
            });
        }).then(progress => {
            if (progress && progress.IsRunning) {
                view.querySelector('#progressContainer').style.display = 'block';
                require(['configurationpage?name=CreditsDetectorConfigurationProgressMonitor'], (progressMonitor) => {
                    progressMonitor.startProgressPolling(instance, view);
                });
            }
            loading.hide();
        }).catch(error => {
            loading.hide();
            console.error('Error loading configuration:', error);
            toast({ type: 'error', text: 'Error loading configuration' });
        });
    }

    function saveData(instance, view) {
        loading.show();

        instance.config.EnableAutoDetection = view.querySelector('#chkEnableAutoDetection').checked;
        instance.config.UseEpisodeComparison = view.querySelector('#chkUseEpisodeComparison').checked;
        instance.config.EnableFailedEpisodeFallback = view.querySelector('#chkEnableFailedEpisodeFallback').checked;
        instance.config.MinimumSuccessRateForFallback = Number.parseFloat(view.querySelector('#txtMinimumSuccessRateForFallback').value) || 0.5;
        instance.config.EnableDetailedLogging = view.querySelector('#chkEnableDetailedLogging').checked;
        instance.config.ScheduledTaskOnlyProcessMissing = view.querySelector('#chkScheduledTaskOnlyProcessMissing').checked;
        instance.config.ManualSkipExistingMarkers = view.querySelector('#chkManualSkipExistingMarkers').checked;

        instance.config.PreventConcurrentPluginProcessing = view.querySelector('#chkPreventConcurrentPluginProcessing').checked;
        instance.config.LowerThreadPriority = view.querySelector('#chkLowerThreadPriority').checked;
        instance.config.LowerProcessPriority = view.querySelector('#chkLowerProcessPriority').checked;
        instance.config.CpuUsageLimit = Number.parseInt(view.querySelector('#txtCpuUsageLimit').value, 10) || 100;
        instance.config.CpuThrottleDelayMs = Number.parseInt(view.querySelector('#txtCpuThrottleDelayMs').value, 10) || 100;
        instance.config.DelayBetweenEpisodesMs = Number.parseInt(view.querySelector('#txtDelayBetweenEpisodesMs').value, 10) || 0;
        instance.config.TempFolderPath = view.querySelector('#txtTempFolderPath').value || '';

        instance.config.EnableOcrDetection = view.querySelector('#chkEnableOcrDetection').checked;
        instance.config.EnableHashDetection = view.querySelector('#chkEnableHashDetection').checked;
        instance.config.OcrEndpoint = view.querySelector('#txtOcrEndpoint').value || 'http://localhost:8884';
        instance.config.OcrDetectionKeywords = view.querySelector('#txtOcrDetectionKeywords').value || 'directed by,produced by,executive producer,written by,cast,credits,fin,ende,終,끝,fim,fine';
                instance.config.OcrRetryAttempts = Number.parseInt(view.querySelector('#txtOcrRetryAttempts').value, 10) || 5;
        instance.config.OcrRetryDelayMs = Number.parseInt(view.querySelector('#txtOcrRetryDelayMs').value, 10) || 2000;
                // Save unified search start setting
        instance.config.OcrSearchStartUnit = view.querySelector('#selectOcrSearchStartUnit').value || 'minutes';
        instance.config.OcrSearchStartValue = Number.parseFloat(view.querySelector('#txtOcrSearchStartValue').value) || 3;
        
        // Update legacy properties for backward compatibility
        if (instance.config.OcrSearchStartUnit === 'minutes') {
            instance.config.OcrMinutesFromEnd = instance.config.OcrSearchStartValue;
            instance.config.OcrDetectionSearchStart = 0.65; // fallback value
        } else {
            instance.config.OcrMinutesFromEnd = 0;
            instance.config.OcrDetectionSearchStart = instance.config.OcrSearchStartValue / 100;
        }
        
        instance.config.OcrFrameRate = Number.parseFloat(view.querySelector('#txtOcrFrameRate').value) || 0.5;
        instance.config.OcrMinimumMatches = Number.parseInt(view.querySelector('#txtOcrMinimumMatches').value, 10) || 2;
        instance.config.OcrMaxFramesToProcess = Number.parseInt(view.querySelector('#txtOcrMaxFramesToProcess').value, 10) || 0;
        instance.config.OcrMaxAnalysisDuration = Number.parseFloat(view.querySelector('#txtOcrMaxAnalysisDuration').value) || 600;
        instance.config.OcrStopSecondsFromEnd = Number.parseFloat(view.querySelector('#txtOcrStopSecondsFromEnd').value) || 20;
        instance.config.OcrJpegQuality = Number.parseInt(view.querySelector('#txtOcrJpegQuality').value, 10) || 92;
        instance.config.OcrMaxResolutionHeight = Number.parseInt(view.querySelector('#selectOcrMaxResolutionHeight').value, 10) || 1080;
        instance.config.OcrDelayBetweenFramesMs = Number.parseInt(view.querySelector('#txtOcrDelayBetweenFramesMs').value, 10) || 0;

        instance.config.OcrFfmpegThreads = Number.parseInt(view.querySelector('#txtOcrFfmpegThreads').value, 10) || 0;
        instance.config.OcrFfmpegFilterThreads = Number.parseInt(view.querySelector('#txtOcrFfmpegFilterThreads').value, 10) || 0;
        instance.config.OcrEnableHardwareAcceleration = view.querySelector('#chkOcrEnableHardwareAcceleration').checked;
        instance.config.OcrHardwareAccelerationType = view.querySelector('#selectOcrHardwareAccelerationType').value || 'none';
        instance.config.OcrHardwareDevice = view.querySelector('#txtOcrHardwareDevice').value || '';
        instance.config.OcrUseHardwareOutputFormat = view.querySelector('#chkOcrUseHardwareOutputFormat').checked;
        instance.config.OcrUseHardwareFilters = view.querySelector('#chkOcrUseHardwareFilters').checked;
        instance.config.OcrUseDirectMemoryPipeline = view.querySelector('#chkOcrUseDirectMemoryPipeline').checked;
        instance.config.OcrFfmpegPreInputArgs = view.querySelector('#txtOcrFfmpegPreInputArgs').value || '';

        instance.config.OcrEnableParallelProcessing = view.querySelector('#chkOcrEnableParallelProcessing').checked;
        instance.config.OcrParallelBatchSize = Number.parseInt(view.querySelector('#txtOcrParallelBatchSize').value, 10) || 4;
        instance.config.OcrDelayBetweenBatchesMs = Number.parseInt(view.querySelector('#txtOcrDelayBetweenBatchesMs').value, 10) || 200;
        instance.config.OcrEnableSmartFrameSkipping = view.querySelector('#chkOcrEnableSmartFrameSkipping').checked;
        instance.config.OcrConsecutiveMatchesForEarlyStop = Number.parseInt(view.querySelector('#txtOcrConsecutiveMatchesForEarlyStop').value, 10) || 3;
        instance.config.OcrMinimumConfidence = Number.parseFloat(view.querySelector('#txtOcrMinimumConfidence').value) || 0;

        // Character Density Detection settings
        instance.config.OcrEnableCharacterDensityDetection = view.querySelector('#chkOcrEnableCharacterDensityDetection').checked;
        instance.config.OcrCharacterDensityThreshold = Number.parseInt(view.querySelector('#txtOcrCharacterDensityThreshold').value, 10) || 20;
        instance.config.OcrCharacterDensityConsecutiveFrames = Number.parseInt(view.querySelector('#txtOcrCharacterDensityConsecutiveFrames').value, 10) || 3;
        instance.config.OcrCharacterDensityPrimaryMethod = view.querySelector('#chkOcrCharacterDensityPrimaryMethod').checked;
        
        // Density Detection Filters
        instance.config.OcrDensityRequireKeyword = view.querySelector('#chkOcrDensityRequireKeyword').checked;
        instance.config.OcrDensityKeywordWindowSeconds = Number.parseFloat(view.querySelector('#txtOcrDensityKeywordWindowSeconds').value) || 10;
        instance.config.OcrDensityRequireTemporalConsistency = view.querySelector('#chkOcrDensityRequireTemporalConsistency').checked;
        instance.config.OcrDensityMinimumDurationSeconds = Number.parseFloat(view.querySelector('#txtOcrDensityMinimumDurationSeconds').value) || 15;
        instance.config.OcrDensityRequireStyleConsistency = view.querySelector('#chkOcrDensityRequireStyleConsistency').checked;
        instance.config.OcrDensityStyleConsistencyThreshold = Number.parseFloat(view.querySelector('#txtOcrDensityStyleConsistencyThreshold').value) || 0.7;

        // OCR Enhancements - Image Preprocessing
        instance.config.OcrEnableImagePreprocessing = view.querySelector('#chkOcrEnableImagePreprocessing').checked;
        instance.config.OcrContrastEnhancement = Number.parseFloat(view.querySelector('#txtOcrContrastEnhancement').value) || 1.5;
        instance.config.OcrBrightnessAdjustment = Number.parseFloat(view.querySelector('#txtOcrBrightnessAdjustment').value) || 0.05;
        instance.config.OcrEnableSharpening = view.querySelector('#chkOcrEnableSharpening').checked;
        instance.config.OcrSharpenAmount = Number.parseFloat(view.querySelector('#txtOcrSharpenAmount').value) || 1.0;

        // Region of Interest
        instance.config.OcrEnableRoiDetection = view.querySelector('#chkOcrEnableRoiDetection').checked;
        instance.config.OcrRoiRegion = view.querySelector('#selectOcrRoiRegion').value || 'full';

        // Fuzzy Keyword Matching
        instance.config.OcrEnableFuzzyMatching = view.querySelector('#chkOcrEnableFuzzyMatching').checked;
        instance.config.OcrFuzzyMatchMaxDistance = Number.parseInt(view.querySelector('#txtOcrFuzzyMatchMaxDistance').value, 10) || 2;

        // Scrolling Pattern Detection
        instance.config.OcrEnableScrollingDetection = view.querySelector('#chkOcrEnableScrollingDetection').checked;
        instance.config.OcrScrollingMinFrames = Number.parseInt(view.querySelector('#txtOcrScrollingMinFrames').value, 10) || 5;
        instance.config.OcrScrollingOverlapThreshold = Number.parseFloat(view.querySelector('#txtOcrScrollingOverlapThreshold').value) || 0.3;

        // Adaptive Frame Rate
        instance.config.OcrEnableAdaptiveFrameRate = view.querySelector('#chkOcrEnableAdaptiveFrameRate').checked;
        instance.config.OcrAdaptiveFrameRateMin = Number.parseFloat(view.querySelector('#txtOcrAdaptiveFrameRateMin').value) || 0.25;

        // Credit Structure Detection
        instance.config.OcrEnableCreditStructureDetection = view.querySelector('#chkOcrEnableCreditStructureDetection').checked;
        instance.config.OcrMinimumStructureLines = Number.parseInt(view.querySelector('#txtOcrMinimumStructureLines').value, 10) || 4;

        instance.config.BackupImportOverwriteExisting = view.querySelector('#chkBackupImportOverwriteExisting').checked;
        instance.config.BackupFolderPath = view.querySelector('#txtBackupFolderPath').value || '';
        instance.config.MaxScheduledBackups = Number.parseInt(view.querySelector('#txtMaxScheduledBackups').value, 10) || 10;

        instance.config.EnableScheduledTaskNotifications = view.querySelector('#chkEnableScheduledTaskNotifications').checked;
        instance.config.EnableAutoDetectionNotifications = view.querySelector('#chkEnableAutoDetectionNotifications').checked;
        instance.config.NotifyOnSuccessOnly = view.querySelector('#chkNotifyOnSuccessOnly').checked;
        const minEpisodes = Number.parseInt(view.querySelector('#txtMinimumEpisodesForNotification').value, 10);
        instance.config.MinimumEpisodesForNotification = Number.isNaN(minEpisodes) ? 1 : minEpisodes;

        instance.config.EnableThumbnailGeneration = view.querySelector('#chkEnableThumbnailGeneration').checked;
        instance.config.ThumbnailWidth = Number.parseInt(view.querySelector('#txtThumbnailWidth').value, 10) || 320;
        instance.config.ThumbnailQuality = Number.parseInt(view.querySelector('#txtThumbnailQuality').value, 10) || 85;

        instance.config.EnableChromaprintDetection = instance.config.EnableHashDetection;
        instance.config.ChromaprintMinDuration = Number.parseInt(view.querySelector('#txtChromaprintMinDuration').value, 10) || 30;
        instance.config.ChromaprintMaxDuration = Number.parseInt(view.querySelector('#txtChromaprintMaxDuration').value, 10) || 300;
        instance.config.ChromaprintAnalysisPercent = Number.parseFloat(view.querySelector('#txtChromaprintAnalysisPercent').value) || 25;
        instance.config.ChromaprintBlackFrameThreshold = Number.parseFloat(view.querySelector('#txtChromaprintBlackFrameThreshold').value) || 0.05;
        instance.config.ChromaprintBlackFrameMinDuration = Number.parseFloat(view.querySelector('#txtChromaprintBlackFrameMinDuration').value) || 0.5;
        instance.config.ChromaprintSilenceThreshold = Number.parseInt(view.querySelector('#txtChromaprintSilenceThreshold').value, 10) || -60;
        instance.config.ChromaprintSilenceMinDuration = Number.parseFloat(view.querySelector('#txtChromaprintSilenceMinDuration').value) || 0.5;
        instance.config.ChromaprintMinConfidence = Number.parseFloat(view.querySelector('#txtChromaprintMinConfidence').value) || 0.85;
        instance.config.ChromaprintStopSecondsFromEnd = Number.parseFloat(view.querySelector('#txtChromaprintStopSecondsFromEnd').value) || 20;

        const checkboxes = view.querySelectorAll('.chkLibrary');
        const selectedLibraryIds = [];
        checkboxes.forEach(checkbox => {
            if (checkbox.checked) {
                selectedLibraryIds.push(checkbox.getAttribute('data-library-id'));
            }
        });
        instance.config.LibraryIds = selectedLibraryIds;

        ApiClient.updatePluginConfiguration(pluginId, instance.config).then(result => {
            loading.hide();
            Dashboard.processPluginConfigurationUpdateResult(result);
        }).catch(error => {
            loading.hide();
            console.error('Error saving configuration:', error);
            toast({ type: 'error', text: 'Error saving configuration' });
        });
    }

    function resetToDefaults(view) {
        const defaultKeywords = 'directed by,produced by,executive producer,written by,cast,credits,fin,ende,終,끝,fim,fine,producer,music by,cinematography,editor,editing,production design,costume design,casting,based on,story by,screenplay,associate producer,co-producer,created by,developed by,series producer,composer,director of photography,visual effects,sound,the end,end credits,starring,guest starring,special thanks,production company';

        view.querySelector('#chkEnableAutoDetection').checked = false;
        view.querySelector('#chkUseEpisodeComparison').checked = false;
        view.querySelector('#chkEnableFailedEpisodeFallback').checked = false;
        view.querySelector('#txtMinimumSuccessRateForFallback').value = 0.5;
        view.querySelector('#chkEnableDetailedLogging').checked = false;
        view.querySelector('#chkScheduledTaskOnlyProcessMissing').checked = true;

        view.querySelector('#chkLowerThreadPriority').checked = false;
        view.querySelector('#chkLowerProcessPriority').checked = false;
        view.querySelector('#txtCpuUsageLimit').value = 100;
        view.querySelector('#txtCpuThrottleDelayMs').value = 100;
        view.querySelector('#txtDelayBetweenEpisodesMs').value = 0;

        const preservedTempFolderPath = view.querySelector('#txtTempFolderPath').value;
        const preservedOcrEndpoint = view.querySelector('#txtOcrEndpoint').value;
        
        view.querySelector('#txtTempFolderPath').value = preservedTempFolderPath;
        view.querySelector('#txtOcrEndpoint').value = preservedOcrEndpoint;
        view.querySelector('#txtOcrDetectionKeywords').value = defaultKeywords;
        view.querySelector('#selectOcrSearchStartUnit').value = 'minutes';
        view.querySelector('#txtOcrSearchStartValue').value = 3;
        updateSearchStartDescription(view, 'minutes');
        view.querySelector('#txtOcrFrameRate').value = 0.5;
        view.querySelector('#txtOcrMinimumMatches').value = 1;
        view.querySelector('#txtOcrMaxFramesToProcess').value = 0;
        view.querySelector('#txtOcrMaxAnalysisDuration').value = 600;
        view.querySelector('#txtOcrStopSecondsFromEnd').value = 20;
        view.querySelector('#txtOcrJpegQuality').value = 92;
        view.querySelector('#selectOcrMaxResolutionHeight').value = 1080;
        view.querySelector('#txtOcrDelayBetweenFramesMs').value = 0;

        view.querySelector('#txtOcrFfmpegThreads').value = 0;
        view.querySelector('#txtOcrFfmpegFilterThreads').value = 0;
        view.querySelector('#chkOcrEnableHardwareAcceleration').checked = false;
        view.querySelector('#selectOcrHardwareAccelerationType').value = 'none';
        view.querySelector('#chkOcrUseHardwareOutputFormat').checked = true;
        view.querySelector('#chkOcrUseHardwareFilters').checked = true;
        view.querySelector('#chkOcrUseDirectMemoryPipeline').checked = true;
        view.querySelector('#txtOcrHardwareDevice').value = '';
        view.querySelector('#txtOcrFfmpegPreInputArgs').value = '';

        view.querySelector('#chkOcrEnableParallelProcessing').checked = false;
        view.querySelector('#txtOcrParallelBatchSize').value = 4;
        view.querySelector('#txtOcrDelayBetweenBatchesMs').value = 200;
        view.querySelector('#chkOcrEnableSmartFrameSkipping').checked = true;
        view.querySelector('#txtOcrConsecutiveMatchesForEarlyStop').value = 3;
        view.querySelector('#txtOcrMinimumConfidence').value = 0;
        
        view.querySelector('#chkOcrEnableCharacterDensityDetection').checked = true;
        view.querySelector('#txtOcrCharacterDensityThreshold').value = 20;
        view.querySelector('#txtOcrCharacterDensityConsecutiveFrames').value = 3;
        view.querySelector('#chkOcrCharacterDensityPrimaryMethod').checked = true;
        
        view.querySelector('#chkOcrDensityRequireKeyword').checked = true;
        view.querySelector('#txtOcrDensityKeywordWindowSeconds').value = 10;
        view.querySelector('#chkOcrDensityRequireTemporalConsistency').checked = true;
        view.querySelector('#txtOcrDensityMinimumDurationSeconds').value = 15;
        view.querySelector('#chkOcrDensityRequireStyleConsistency').checked = true;
        view.querySelector('#txtOcrDensityStyleConsistencyThreshold').value = 0.7;

        view.querySelector('#chkOcrEnableImagePreprocessing').checked = false;
        view.querySelector('#txtOcrContrastEnhancement').value = 1.5;
        view.querySelector('#txtOcrBrightnessAdjustment').value = 0.05;
        view.querySelector('#chkOcrEnableSharpening').checked = false;
        view.querySelector('#txtOcrSharpenAmount').value = 1.0;

        view.querySelector('#chkOcrEnableRoiDetection').checked = false;
        view.querySelector('#selectOcrRoiRegion').value = 'full';

        view.querySelector('#chkOcrEnableFuzzyMatching').checked = false;
        view.querySelector('#txtOcrFuzzyMatchMaxDistance').value = 2;

        view.querySelector('#chkOcrEnableScrollingDetection').checked = false;
        view.querySelector('#txtOcrScrollingMinFrames').value = 5;
        view.querySelector('#txtOcrScrollingOverlapThreshold').value = 0.3;

        view.querySelector('#chkOcrEnableAdaptiveFrameRate').checked = false;
        view.querySelector('#txtOcrAdaptiveFrameRateMin').value = 0.25;

        view.querySelector('#chkOcrEnableCreditStructureDetection').checked = false;
        view.querySelector('#txtOcrMinimumStructureLines').value = 4;

        toast('Settings reset to defaults (OCR endpoint and temp folder preserved)');
    }

    function browseTempFolder(view) {
        require(['directorybrowser'], function(directoryBrowser) {
            var picker = new directoryBrowser();
            picker.show({
                includeFiles: false,
                callback: function(path) {
                    if (path) {
                        view.querySelector('#txtTempFolderPath').value = path;
                    }
                    picker.close();
                }
            });
        });
    }

    function browseBackupFolder(view) {
        require(['directorybrowser'], function(directoryBrowser) {
            var picker = new directoryBrowser();
            picker.show({
                includeFiles: false,
                callback: function(path) {
                    if (path) {
                        view.querySelector('#txtBackupFolderPath').value = path;
                    }
                    picker.close();
                }
            });
        });
    }

    function updateSearchStartDescription(view, unit) {
        const descMinutes = view.querySelector('#descMinutes');
        const descPercentage = view.querySelector('#descPercentage');
        const valueInput = view.querySelector('#txtOcrSearchStartValue');
        
        if (unit === 'minutes') {
            descMinutes.style.display = '';
            descPercentage.style.display = 'none';
            valueInput.setAttribute('step', '0.5');
            valueInput.setAttribute('min', '0.5');
            valueInput.setAttribute('max', '30');
        } else {
            descMinutes.style.display = 'none';
            descPercentage.style.display = '';
            valueInput.setAttribute('step', '1');
            valueInput.setAttribute('min', '50');
            valueInput.setAttribute('max', '95');
        }
    }

    function setupUnitChangeListener(view) {
        const unitSelector = view.querySelector('#selectOcrSearchStartUnit');
        const valueInput = view.querySelector('#txtOcrSearchStartValue');
        
        unitSelector.addEventListener('change', function() {
            const newUnit = this.value;
            const currentValue = Number.parseFloat(valueInput.value) || 0;
            
            // Convert value when switching units
            if (newUnit === 'percentage') {
                // If switching from minutes to percentage, suggest 65%
                if (currentValue < 50) {
                    valueInput.value = 65;
                }
            } else {
                // If switching from percentage to minutes, suggest 3 minutes
                if (currentValue >= 50) {
                    valueInput.value = 3;
                }
            }
            
            updateSearchStartDescription(view, newUnit);
        });
    }

    function setupHardwareAccelerationListeners(view) {
        const hwAccelCheckbox = view.querySelector('#chkOcrEnableHardwareAcceleration');
        const hwTypeContainer = view.querySelector('#containerOcrHardwareAccelerationType');
        const hwDeviceContainer = view.querySelector('#containerOcrHardwareDevice');
        const hwTypeSelect = view.querySelector('#selectOcrHardwareAccelerationType');
        
        function updateHardwareAccelerationVisibility() {
            const isEnabled = hwAccelCheckbox.checked;
            hwTypeContainer.style.display = isEnabled ? '' : 'none';
            hwDeviceContainer.style.display = isEnabled ? '' : 'none';
        }
        
        updateHardwareAccelerationVisibility();
        hwAccelCheckbox.addEventListener('change', updateHardwareAccelerationVisibility);
    }

    function applyQualityPreset(view) {
        const preservedTempFolderPath = view.querySelector('#txtTempFolderPath').value;
        const preservedOcrEndpoint = view.querySelector('#txtOcrEndpoint').value;
        const preservedHwAccelType = view.querySelector('#selectOcrHardwareAccelerationType').value;
        const preservedHwDevice = view.querySelector('#txtOcrHardwareDevice').value;
        const preservedHwAccelEnabled = view.querySelector('#chkOcrEnableHardwareAcceleration').checked;

        const setIfExists = (selector, value, isCheckbox = false) => {
            const elem = view.querySelector(selector);
            if (elem) {
                if (isCheckbox) elem.checked = value;
                else elem.value = value;
            }
        };

        setIfExists('#selectOcrSearchStartUnit', 'minutes');
        setIfExists('#txtOcrSearchStartValue', 5);
        updateSearchStartDescription(view, 'minutes');
        setIfExists('#txtOcrFrameRate', 1.0);
        setIfExists('#txtOcrMinimumMatches', 2);
        setIfExists('#txtOcrMaxFramesToProcess', 0);
        setIfExists('#txtOcrMaxAnalysisDuration', 600);
        setIfExists('#txtOcrStopSecondsFromEnd', 10);
        setIfExists('#txtOcrJpegQuality', 95);
        setIfExists('#txtOcrDelayBetweenFramesMs', 0);

        setIfExists('#txtOcrFfmpegThreads', 0);
        setIfExists('#txtOcrFfmpegFilterThreads', 0);
        setIfExists('#chkOcrEnableHardwareAcceleration', preservedHwAccelEnabled, true);
        setIfExists('#selectOcrHardwareAccelerationType', preservedHwAccelType);
        setIfExists('#chkOcrUseHardwareOutputFormat', true, true);
        setIfExists('#chkOcrUseHardwareFilters', true, true);
        setIfExists('#chkOcrUseDirectMemoryPipeline', true, true);
        setIfExists('#txtOcrHardwareDevice', preservedHwDevice);
        setIfExists('#txtOcrFfmpegPreInputArgs', '');

        setIfExists('#chkOcrEnableParallelProcessing', false, true);
        setIfExists('#txtOcrParallelBatchSize', 4);
        setIfExists('#chkOcrEnableSmartFrameSkipping', false, true);
        setIfExists('#txtOcrConsecutiveMatchesForEarlyStop', 3);
        setIfExists('#txtOcrMinimumConfidence', 0.7);

        setIfExists('#chkOcrEnableCharacterDensityDetection', true, true);
        setIfExists('#txtOcrCharacterDensityThreshold', 15);
        setIfExists('#txtOcrCharacterDensityConsecutiveFrames', 5);
        setIfExists('#chkOcrCharacterDensityPrimaryMethod', true, true);

        setIfExists('#chkOcrDensityRequireKeyword', true, true);
        setIfExists('#txtOcrDensityKeywordWindowSeconds', 10);
        setIfExists('#chkOcrDensityRequireTemporalConsistency', true, true);
        setIfExists('#txtOcrDensityMinimumDurationSeconds', 20);
        setIfExists('#chkOcrDensityRequireStyleConsistency', true, true);
        setIfExists('#txtOcrDensityStyleConsistencyThreshold', 0.8);

        setIfExists('#chkOcrEnableImagePreprocessing', false, true);
        setIfExists('#txtOcrContrastEnhancement', 1.5);
        setIfExists('#txtOcrBrightnessAdjustment', 0.05);
        setIfExists('#chkOcrEnableSharpening', false, true);
        setIfExists('#txtOcrSharpenAmount', 1.0);

        setIfExists('#chkOcrEnableRoiDetection', false, true);
        setIfExists('#selectOcrRoiRegion', 'full');

        setIfExists('#chkOcrEnableFuzzyMatching', false, true);
        setIfExists('#txtOcrFuzzyMatchMaxDistance', 2);

        setIfExists('#chkOcrEnableScrollingDetection', false, true);
        setIfExists('#txtOcrScrollingMinFrames', 5);
        setIfExists('#txtOcrScrollingOverlapThreshold', 0.3);

        setIfExists('#chkOcrEnableAdaptiveFrameRate', false, true);
        setIfExists('#txtOcrAdaptiveFrameRateMin', 0.25);

        setIfExists('#chkOcrEnableCreditStructureDetection', false, true);
        setIfExists('#txtOcrMinimumStructureLines', 4);

        setIfExists('#txtTempFolderPath', preservedTempFolderPath);
        setIfExists('#txtOcrEndpoint', preservedOcrEndpoint);

        toast('Applied Best Quality preset - optimized for accuracy');
    }

    function applySpeedPreset(view) {
        const preservedTempFolderPath = view.querySelector('#txtTempFolderPath').value;
        const preservedOcrEndpoint = view.querySelector('#txtOcrEndpoint').value;
        const preservedHwAccelType = view.querySelector('#selectOcrHardwareAccelerationType').value;
        const preservedHwDevice = view.querySelector('#txtOcrHardwareDevice').value;
        const preservedHwAccelEnabled = view.querySelector('#chkOcrEnableHardwareAcceleration').checked;

        const setIfExists = (selector, value, isCheckbox = false) => {
            const elem = view.querySelector(selector);
            if (elem) {
                if (isCheckbox) elem.checked = value;
                else elem.value = value;
            }
        };

        setIfExists('#selectOcrSearchStartUnit', 'minutes');
        setIfExists('#txtOcrSearchStartValue', 2);
        updateSearchStartDescription(view, 'minutes');
        setIfExists('#txtOcrFrameRate', 0.33);
        setIfExists('#txtOcrMinimumMatches', 1);
        setIfExists('#txtOcrMaxFramesToProcess', 60);
        setIfExists('#txtOcrMaxAnalysisDuration', 180);
        setIfExists('#txtOcrStopSecondsFromEnd', 30);
        setIfExists('#txtOcrJpegQuality', 85);
        setIfExists('#txtOcrDelayBetweenFramesMs', 0);

        setIfExists('#txtOcrFfmpegThreads', 0);
        setIfExists('#txtOcrFfmpegFilterThreads', 0);
        setIfExists('#chkOcrEnableHardwareAcceleration', preservedHwAccelEnabled, true);
        setIfExists('#selectOcrHardwareAccelerationType', preservedHwAccelType);
        setIfExists('#chkOcrUseHardwareOutputFormat', true, true);
        setIfExists('#chkOcrUseHardwareFilters', true, true);
        setIfExists('#chkOcrUseDirectMemoryPipeline', true, true);
        setIfExists('#txtOcrHardwareDevice', preservedHwDevice);
        setIfExists('#txtOcrFfmpegPreInputArgs', '');

        setIfExists('#chkOcrEnableParallelProcessing', true, true);
        setIfExists('#txtOcrParallelBatchSize', 6);
        setIfExists('#chkOcrEnableSmartFrameSkipping', true, true);
        setIfExists('#txtOcrConsecutiveMatchesForEarlyStop', 2);
        setIfExists('#txtOcrMinimumConfidence', 0);

        setIfExists('#chkOcrEnableCharacterDensityDetection', true, true);
        setIfExists('#txtOcrCharacterDensityThreshold', 25);
        setIfExists('#txtOcrCharacterDensityConsecutiveFrames', 2);
        setIfExists('#chkOcrCharacterDensityPrimaryMethod', true, true);

        setIfExists('#chkOcrDensityRequireKeyword', false, true);
        setIfExists('#txtOcrDensityKeywordWindowSeconds', 10);
        setIfExists('#chkOcrDensityRequireTemporalConsistency', false, true);
        setIfExists('#txtOcrDensityMinimumDurationSeconds', 10);
        setIfExists('#chkOcrDensityRequireStyleConsistency', false, true);
        setIfExists('#txtOcrDensityStyleConsistencyThreshold', 0.7);

        setIfExists('#chkOcrEnableImagePreprocessing', false, true);
        setIfExists('#txtOcrContrastEnhancement', 1.5);
        setIfExists('#txtOcrBrightnessAdjustment', 0.05);
        setIfExists('#chkOcrEnableSharpening', false, true);
        setIfExists('#txtOcrSharpenAmount', 1.0);

        setIfExists('#chkOcrEnableRoiDetection', false, true);
        setIfExists('#selectOcrRoiRegion', 'full');

        setIfExists('#chkOcrEnableFuzzyMatching', false, true);
        setIfExists('#txtOcrFuzzyMatchMaxDistance', 2);

        setIfExists('#chkOcrEnableScrollingDetection', false, true);
        setIfExists('#txtOcrScrollingMinFrames', 5);
        setIfExists('#txtOcrScrollingOverlapThreshold', 0.3);

        setIfExists('#chkOcrEnableAdaptiveFrameRate', false, true);
        setIfExists('#txtOcrAdaptiveFrameRateMin', 0.25);

        setIfExists('#chkOcrEnableCreditStructureDetection', false, true);
        setIfExists('#txtOcrMinimumStructureLines', 4);

        setIfExists('#txtTempFolderPath', preservedTempFolderPath);
        setIfExists('#txtOcrEndpoint', preservedOcrEndpoint);

        toast('Applied Best Speed preset - optimized for fast processing');
    }

    function resetAllExceptFolders(view) {
        const preservedTempFolderPath = view.querySelector('#txtTempFolderPath').value;
        const preservedBackupFolderPath = view.querySelector('#txtBackupFolderPath').value;
        const preservedOcrEndpoint = view.querySelector('#txtOcrEndpoint').value;
        const preservedHwAccelType = view.querySelector('#selectOcrHardwareAccelerationType').value;
        const preservedHwDevice = view.querySelector('#txtOcrHardwareDevice').value;
        const preservedHwAccelEnabled = view.querySelector('#chkOcrEnableHardwareAcceleration').checked;

        const defaultKeywords = 'associate producer,based on,cast,casting,cinematography,co-producer,composer,costume design,created by,credits,developed by,directed by,director of photography,editing,editor,end credits,ende,executive producer,fim,fin,fine,guest starring,music by,produced by,producer,production company,production design,screenplay,series producer,sound,special thanks,starring,story by,the end,visual effects,written by,끝,終';

        const setIfExists = (selector, value, isCheckbox = false) => {
            const elem = view.querySelector(selector);
            if (elem) {
                if (isCheckbox) elem.checked = value;
                else elem.value = value;
            }
        };

        setIfExists('#chkEnableAutoDetection', false, true);
        setIfExists('#chkEnableDetailedLogging', false, true);
        setIfExists('#chkScheduledTaskOnlyProcessMissing', true, true);
        setIfExists('#chkPreventConcurrentPluginProcessing', true, true);
        setIfExists('#chkLowerThreadPriority', false, true);
        setIfExists('#chkLowerProcessPriority', false, true);
        setIfExists('#txtCpuUsageLimit', 100);
        setIfExists('#txtCpuThrottleDelayMs', 100);
        setIfExists('#txtDelayBetweenEpisodesMs', 0);
        setIfExists('#txtMaxScheduledBackups', 10);

        setIfExists('#txtOcrEndpoint', preservedOcrEndpoint);
        setIfExists('#txtOcrDetectionKeywords', defaultKeywords);
        setIfExists('#txtOcrRetryAttempts', 5);
        setIfExists('#txtOcrRetryDelayMs', 2000);

        setIfExists('#selectOcrSearchStartUnit', 'minutes');
        setIfExists('#txtOcrSearchStartValue', 3);
        updateSearchStartDescription(view, 'minutes');

        setIfExists('#txtOcrFrameRate', 0.5);
        setIfExists('#txtOcrMinimumMatches', 1);
        setIfExists('#txtOcrMaxFramesToProcess', 0);
        setIfExists('#txtOcrMaxAnalysisDuration', 600);
        setIfExists('#txtOcrStopSecondsFromEnd', 20);
        setIfExists('#txtOcrJpegQuality', 92);
        setIfExists('#txtOcrDelayBetweenFramesMs', 0);

        setIfExists('#txtOcrFfmpegThreads', 0);
        setIfExists('#txtOcrFfmpegFilterThreads', 0);
        setIfExists('#chkOcrEnableHardwareAcceleration', preservedHwAccelEnabled, true);
        setIfExists('#selectOcrHardwareAccelerationType', preservedHwAccelType);
        setIfExists('#chkOcrUseHardwareOutputFormat', true, true);
        setIfExists('#chkOcrUseHardwareFilters', true, true);
        setIfExists('#chkOcrUseDirectMemoryPipeline', true, true);
        setIfExists('#txtOcrHardwareDevice', preservedHwDevice);
        setIfExists('#txtOcrFfmpegPreInputArgs', '');

        setIfExists('#chkOcrEnableParallelProcessing', false, true);
        setIfExists('#txtOcrParallelBatchSize', 4);
        setIfExists('#chkOcrEnableSmartFrameSkipping', true, true);
        setIfExists('#txtOcrConsecutiveMatchesForEarlyStop', 3);
        setIfExists('#txtOcrMinimumConfidence', 0);

        setIfExists('#chkOcrEnableCharacterDensityDetection', true, true);
        setIfExists('#txtOcrCharacterDensityThreshold', 20);
        setIfExists('#txtOcrCharacterDensityConsecutiveFrames', 3);
        setIfExists('#chkOcrCharacterDensityPrimaryMethod', true, true);

        setIfExists('#chkOcrDensityRequireKeyword', true, true);
        setIfExists('#txtOcrDensityKeywordWindowSeconds', 10);
        setIfExists('#chkOcrDensityRequireTemporalConsistency', true, true);
        setIfExists('#txtOcrDensityMinimumDurationSeconds', 15);
        setIfExists('#chkOcrDensityRequireStyleConsistency', true, true);
        setIfExists('#txtOcrDensityStyleConsistencyThreshold', 0.7);

        setIfExists('#chkOcrEnableImagePreprocessing', false, true);
        setIfExists('#txtOcrContrastEnhancement', 1.5);
        setIfExists('#txtOcrBrightnessAdjustment', 0.05);
        setIfExists('#chkOcrEnableSharpening', false, true);
        setIfExists('#txtOcrSharpenAmount', 1.0);

        setIfExists('#chkOcrEnableRoiDetection', false, true);
        setIfExists('#selectOcrRoiRegion', 'full');

        setIfExists('#chkOcrEnableFuzzyMatching', false, true);
        setIfExists('#txtOcrFuzzyMatchMaxDistance', 2);

        setIfExists('#chkOcrEnableScrollingDetection', false, true);
        setIfExists('#txtOcrScrollingMinFrames', 5);
        setIfExists('#txtOcrScrollingOverlapThreshold', 0.3);

        setIfExists('#chkOcrEnableAdaptiveFrameRate', false, true);
        setIfExists('#txtOcrAdaptiveFrameRateMin', 0.25);

        setIfExists('#chkOcrEnableCreditStructureDetection', false, true);
        setIfExists('#txtOcrMinimumStructureLines', 4);

        setIfExists('#chkEnableScheduledTaskNotifications', false, true);
        setIfExists('#chkEnableAutoDetectionNotifications', false, true);
        setIfExists('#chkNotifyOnSuccessOnly', false, true);
        setIfExists('#txtMinimumEpisodesForNotification', 1);

        setIfExists('#chkManualSkipExistingMarkers', false, true);

        setIfExists('#chkEnableThumbnailGeneration', true, true);
        setIfExists('#txtThumbnailWidth', 320);
        setIfExists('#txtThumbnailQuality', 85);

        setIfExists('#txtTempFolderPath', preservedTempFolderPath);
        setIfExists('#txtBackupFolderPath', preservedBackupFolderPath);

        toast('All settings reset to defaults (folder paths preserved)');
    }

    return {
        loadData: loadData,
        saveData: saveData,
        resetToDefaults: resetToDefaults,
        browseTempFolder: browseTempFolder,
        browseBackupFolder: browseBackupFolder,
        setupUnitChangeListener: setupUnitChangeListener,
        applyQualityPreset: applyQualityPreset,
        applySpeedPreset: applySpeedPreset,
        resetAllExceptFolders: resetAllExceptFolders
    };
});
