define(['loading', 'toast'], function (loading, toast) {
    'use strict';

    const pluginId = "b1a65a73-a620-432a-9f5b-285038031c26";

    function loadData(instance, view) {
        loading.show();
        ApiClient.getPluginConfiguration(pluginId).then(config => {
            instance.config = config;

            view.querySelector('#chkEnableAutoDetection').checked = config.EnableAutoDetection || false;
            view.querySelector('#chkUseEpisodeComparison').checked = config.UseEpisodeComparison || false;
            view.querySelector('#chkEnableFailedEpisodeFallback').checked = config.EnableFailedEpisodeFallback || false;
            view.querySelector('#txtMinimumSuccessRateForFallback').value = config.MinimumSuccessRateForFallback || 0.5;
            view.querySelector('#chkEnableDetailedLogging').checked = config.EnableDetailedLogging || false;
            view.querySelector('#chkScheduledTaskOnlyProcessMissing').checked = config.ScheduledTaskOnlyProcessMissing !== false;
            view.querySelector('#chkManualSkipExistingMarkers').checked = config.ManualSkipExistingMarkers || false;

            view.querySelector('#txtDelayBetweenEpisodesMs').value = config.DelayBetweenEpisodesMs || 0;
            view.querySelector('#txtTempFolderPath').value = config.TempFolderPath || '';

            view.querySelector('#txtOcrEndpoint').value = config.OcrEndpoint || 'http://localhost:8884';
            view.querySelector('#txtOcrDetectionKeywords').value = config.OcrDetectionKeywords || 'directed by,produced by,executive producer,written by,cast,credits,fin,ende,終,끝,fim,fine';
            
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
            view.querySelector('#selectOcrImageFormat').value = config.OcrImageFormat || 'jpg';
            view.querySelector('#txtOcrJpegQuality').value = config.OcrJpegQuality || 92;
            view.querySelector('#txtOcrDelayBetweenFramesMs').value = config.OcrDelayBetweenFramesMs || 0;

            view.querySelector('#chkOcrEnableParallelProcessing').checked = config.OcrEnableParallelProcessing || false;
            view.querySelector('#txtOcrParallelBatchSize').value = config.OcrParallelBatchSize || 4;
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

            view.querySelector('#chkBackupImportOverwriteExisting').checked = config.BackupImportOverwriteExisting || false;

            // Load libraries and series/episode dropdowns
            require(['configurationpage?name=CreditsDetectorConfigurationSeriesManager'], (seriesManager) => {
                seriesManager.loadLibraries(view, config);
                seriesManager.loadLibraryFilter(view);
                seriesManager.loadSeriesList(view);
            });
            
            // Setup unit change listener for search start
            setupUnitChangeListener(view);

            // Trigger keyword display update on main page
            setTimeout(() => {
                const event = new CustomEvent('keywordsLoaded');
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

        instance.config.DelayBetweenEpisodesMs = Number.parseInt(view.querySelector('#txtDelayBetweenEpisodesMs').value, 10) || 0;
        instance.config.TempFolderPath = view.querySelector('#txtTempFolderPath').value || '';

        instance.config.EnableOcrDetection = true;
        instance.config.OcrEndpoint = view.querySelector('#txtOcrEndpoint').value || 'http://localhost:8884';
        instance.config.OcrDetectionKeywords = view.querySelector('#txtOcrDetectionKeywords').value || 'directed by,produced by,executive producer,written by,cast,credits,fin,ende,終,끝,fim,fine';
        
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
        instance.config.OcrImageFormat = view.querySelector('#selectOcrImageFormat').value || 'jpg';
        instance.config.OcrJpegQuality = Number.parseInt(view.querySelector('#txtOcrJpegQuality').value, 10) || 92;
        instance.config.OcrDelayBetweenFramesMs = Number.parseInt(view.querySelector('#txtOcrDelayBetweenFramesMs').value, 10) || 0;

        instance.config.OcrEnableParallelProcessing = view.querySelector('#chkOcrEnableParallelProcessing').checked;
        instance.config.OcrParallelBatchSize = Number.parseInt(view.querySelector('#txtOcrParallelBatchSize').value, 10) || 4;
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

        instance.config.BackupImportOverwriteExisting = view.querySelector('#chkBackupImportOverwriteExisting').checked;

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
        view.querySelector('#selectOcrImageFormat').value = 'jpg';
        view.querySelector('#txtOcrJpegQuality').value = 92;
        view.querySelector('#txtOcrDelayBetweenFramesMs').value = 0;

        view.querySelector('#chkOcrEnableParallelProcessing').checked = false;
        view.querySelector('#txtOcrParallelBatchSize').value = 4;
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

        toast('Settings reset to defaults (OCR endpoint and temp folder preserved)');
    }

    function browseTempFolder(view) {
        require(['directorybrowser'], (directoryBrowser) => {
            directoryBrowser.show({
                callback: (path) => {
                    if (path) {
                        view.querySelector('#txtTempFolderPath').value = path;
                    }
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
                if (currentValue > 30) {
                    valueInput.value = 3;
                }
            }
            
            updateSearchStartDescription(view, newUnit);
        });
    }

    return {
        loadData: loadData,
        saveData: saveData,
        resetToDefaults: resetToDefaults,
        browseTempFolder: browseTempFolder,
        setupUnitChangeListener: setupUnitChangeListener
    };
});
