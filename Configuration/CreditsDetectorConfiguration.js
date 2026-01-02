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

            // Keyword Manager (inline)
            view.querySelector('#btnAddKeyword').addEventListener('click', () => {
                this.addKeyword(view);
            });

            view.querySelector('#txtNewKeyword').addEventListener('keypress', (e) => {
                if (e.key === 'Enter') {
                    e.preventDefault();
                    this.addKeyword(view);
                }
            });

            view.querySelector('#btnResetKeywords').addEventListener('click', () => {
                if (confirm('Reset keywords to defaults? This will replace all current keywords.')) {
                    const defaults = 'directed by,produced by,executive producer,written by,cast,credits,fin,ende,終,끝,fim,fine';
                    view.querySelector('#txtOcrDetectionKeywords').value = defaults;
                    this.updateKeywordDisplay(view);
                    
                    // Auto-save after resetting keywords
                    require(['configurationpage?name=CreditsDetectorConfigurationDataManager'], (dataManager) => {
                        dataManager.saveData(this, view);
                    });
                }
            });
        }

        addKeyword(view) {
            const input = view.querySelector('#txtNewKeyword');
            const keyword = input.value.trim();
            
            if (!keyword) {
                toast({ type: 'warning', text: 'Please enter a keyword' });
                return;
            }
            
            const hiddenInput = view.querySelector('#txtOcrDetectionKeywords');
            const currentKeywords = hiddenInput.value.split(',').map(k => k.trim()).filter(k => k.length > 0);
            
            if (currentKeywords.some(k => k.toLowerCase() === keyword.toLowerCase())) {
                toast({ type: 'warning', text: 'This keyword already exists' });
                return;
            }
            
            currentKeywords.push(keyword);
            currentKeywords.sort((a, b) => a.toLowerCase().localeCompare(b.toLowerCase()));
            hiddenInput.value = currentKeywords.join(',');
            this.updateKeywordDisplay(view);
            input.value = '';
            input.focus();
            
            // Auto-save after adding keyword
            require(['configurationpage?name=CreditsDetectorConfigurationDataManager'], (dataManager) => {
                dataManager.saveData(this, view);
            });
        }

        removeKeyword(view, keyword) {
            const hiddenInput = view.querySelector('#txtOcrDetectionKeywords');
            const currentKeywords = hiddenInput.value.split(',').map(k => k.trim()).filter(k => k.length > 0);
            const filtered = currentKeywords.filter(k => k !== keyword);
            hiddenInput.value = filtered.join(',');
            this.updateKeywordDisplay(view);
            
            // Auto-save after removing keyword
            require(['configurationpage?name=CreditsDetectorConfigurationDataManager'], (dataManager) => {
                dataManager.saveData(this, view);
            });
        }

        updateKeywordDisplay(view) {
            const displayArea = view.querySelector('#keywordDisplayArea');
            const hiddenInput = view.querySelector('#txtOcrDetectionKeywords');
            const keywords = hiddenInput.value.split(',').map(k => k.trim()).filter(k => k.length > 0);
            
            // Sort keywords alphabetically (case-insensitive)
            keywords.sort((a, b) => a.toLowerCase().localeCompare(b.toLowerCase()));
            
            displayArea.innerHTML = '';
            
            if (keywords.length === 0) {
                displayArea.innerHTML = '<div style="color: rgba(255,255,255,0.5); width: 100%; text-align: center; padding: 1.5em;">No keywords configured</div>';
                return;
            }
            
            keywords.forEach(keyword => {
                const chip = document.createElement('div');
                chip.style.cssText = 'display: inline-flex; align-items: center; gap: 0.5em; padding: 0.5em 0.75em; background: #52B54B; border-radius: 4px; font-size: 0.9em;';
                
                const text = document.createElement('span');
                text.textContent = keyword;
                chip.appendChild(text);
                
                const removeBtn = document.createElement('button');
                removeBtn.type = 'button';
                removeBtn.innerHTML = '<i class="md-icon" style="font-size: 18px;">close</i>';
                removeBtn.style.cssText = 'background: rgba(0,0,0,0.3); border: none; border-radius: 50%; width: 22px; height: 22px; display: flex; align-items: center; justify-content: center; cursor: pointer; padding: 0; transition: background 0.2s;';
                removeBtn.title = 'Remove keyword';
                removeBtn.addEventListener('mouseenter', () => {
                    removeBtn.style.background = 'rgba(0,0,0,0.5)';
                });
                removeBtn.addEventListener('mouseleave', () => {
                    removeBtn.style.background = 'rgba(0,0,0,0.3)';
                });
                removeBtn.addEventListener('click', () => {
                    this.removeKeyword(view, keyword);
                });
                chip.appendChild(removeBtn);
                
                displayArea.appendChild(chip);
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
                    
                    // Listen for keywords loaded event
                    view.addEventListener('keywordsLoaded', () => {
                        this.updateKeywordDisplay(view);
                    });
                    
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
