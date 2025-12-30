define(['baseView', 'loading', 'toast', 'emby-input', 'emby-button', 'emby-checkbox', 
    'configurationpage?name=CreditsDetectorConfigurationLoader', 
    'configurationpage?name=CreditsDetectorConfigurationEvents',
    'configurationpage?name=CreditsDetectorConfigurationUtils',
    'configurationpage?name=CreditsDetectorConfigurationDataManager',
    'configurationpage?name=CreditsDetectorConfigurationSeriesManager',
    'configurationpage?name=CreditsDetectorConfigurationProcessingActions',
    'configurationpage?name=CreditsDetectorConfigurationProgressMonitor',
    'configurationpage?name=CreditsDetectorConfigurationMarkersManager',
    'configurationpage?name=CreditsDetectorConfigurationBackupManager'
], function (BaseView, loading, toast, embyInput, embyButton, embyCheckbox, loader, events, utils, dataManager, seriesManager, processingActions, progressMonitor, markersManager, backupManager) {
    'use strict';

    return class extends BaseView {
        constructor(view, params) {
            super(view, params);
            this.partialsLoaded = false;
            this.progressInterval = null;
            this.progressHideTimeout = null;
        }

        bindEventListeners(view) {
            // Form submission - delegate to DataManager
            view.querySelector('.creditsDetectorForm').addEventListener('submit', (e) => {
                e.preventDefault();
                dataManager.saveData(this, view);
                return false;
            });

            // Data actions
            view.querySelector('#btnResetToDefaults').addEventListener('click', () => {
                dataManager.resetToDefaults(view);
            });

            view.querySelector('#btnBrowseTempFolder').addEventListener('click', () => {
                dataManager.browseTempFolder(view);
            });

            // Processing actions
            view.querySelector('#btnProcessSeries').addEventListener('click', () => {
                processingActions.processSeries(this, view);
            });

            view.querySelector('#btnQueueAllSeries').addEventListener('click', () => {
                processingActions.queueAllSeries(this, view);
            });

            view.querySelector('#btnCancelProcessing').addEventListener('click', () => {
                processingActions.cancelProcessing(this, view);
            });

            view.querySelector('#btnClearQueue').addEventListener('click', () => {
                processingActions.clearQueue(view);
            });

            view.querySelector('#btnDryRun').addEventListener('click', () => {
                processingActions.startDryRun(this, view, false);
            });

            view.querySelector('#btnDryRunDebug').addEventListener('click', () => {
                processingActions.startDryRun(this, view, true);
            });

            view.querySelector('#btnTestOcrConnection').addEventListener('click', () => {
                processingActions.testOcrConnection(view);
            });

            view.querySelector('#btnClearProcessedFiles').addEventListener('click', () => {
                if (confirm('Are you sure you want to clear the processed files list? This will cause all episodes to be processed again on the next run.')) {
                    ApiClient.ajax({
                        type: 'POST',
                        url: ApiClient.getUrl('CreditsDetector/ClearProcessedFiles')
                    }).then(() => {
                        toast({ type: 'success', text: 'Processed files list cleared successfully' });
                    }).catch(error => {
                        console.error('Error clearing processed files:', error);
                        toast({ type: 'error', text: 'Failed to clear processed files list' });
                    });
                }
            });

            // Series/Episode selection - delegate to SeriesManager
            view.querySelector('#selectLibraryFilter').addEventListener('change', () => {
                const libraryId = view.querySelector('#selectLibraryFilter').value;
                seriesManager.loadSeriesList(view, libraryId);
            });

            view.querySelector('#selectSeries').addEventListener('change', () => {
                const seriesId = view.querySelector('#selectSeries').value;
                seriesManager.loadEpisodesForSeries(view, seriesId);
            });

            view.querySelector('#selectSeriesForMarkers').addEventListener('change', () => {
                const seriesId = view.querySelector('#selectSeriesForMarkers').value;
                markersManager.displayMarkers(view);
            });

            // Backup/Restore - delegate to BackupManager
            view.querySelector('#btnExportBackup').addEventListener('click', () => {
                backupManager.exportBackup();
            });

            view.querySelector('#btnImportBackup').addEventListener('click', () => {
                backupManager.importBackup(view);
            });
        }

        onResume(options) {
            super.onResume(options);
            const view = this.view;

            // Load all HTML partials first
            if (!this.partialsLoaded) {
                loader.loadPagePartials(view).then(() => {
                    this.partialsLoaded = true;
                    events.bindTabNavigation(view);
                    this.bindEventListeners(view);
                    dataManager.loadData(this, view);
                    
                    // Load donate image
                    const donateImg = view.querySelector('#donateImage');
                    if (donateImg && !donateImg.src) {
                        fetch(ApiClient.getUrl('CreditsDetector/Images/donate.png'), {
                            headers: {
                                'X-Emby-Token': ApiClient.accessToken()
                            }
                        })
                        .then(response => response.blob())
                        .then(blob => {
                            const objectUrl = URL.createObjectURL(blob);
                            donateImg.src = objectUrl;
                        })
                        .catch(error => {
                            console.error('Error loading donate image:', error);
                        });
                    }
                }).catch(error => {
                    console.error('Error loading partials:', error);
                    toast({ type: 'error', text: 'Failed to load configuration page' });
                });
            } else {
                dataManager.loadData(this, view);
            }
        }
    };
});
