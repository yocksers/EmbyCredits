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
            view.querySelectorAll('.creditsDetectorForm').forEach(form => {
                form.addEventListener('submit', (e) => {
                    e.preventDefault();
                    dataManager.saveData(this, view);
                    return false;
                });
            });

            view.querySelector('#btnResetToDefaults').addEventListener('click', () => {
                dataManager.resetToDefaults(view);
            });

            view.querySelector('#btnBrowseTempFolder').addEventListener('click', () => {
                dataManager.browseTempFolder(view);
            });

            view.querySelector('#btnBrowseBackupFolder').addEventListener('click', () => {
                dataManager.browseBackupFolder(view);
            });

            view.querySelector('#btnResetAllExceptFolders').addEventListener('click', () => {
                dataManager.resetAllExceptFolders(view);
            });

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
                markersManager.displayMarkers(this, view);
            });

            view.querySelector('#btnShowSeasonValidation').addEventListener('click', () => {
                markersManager.showSeasonValidation(this, view);
            });

            view.querySelector('#btnExportBackup').addEventListener('click', () => {
                backupManager.exportBackup(view);
            });

            view.querySelector('#btnImportBackup').addEventListener('click', () => {
                backupManager.importBackup(view);
            });

            const ocrCheckbox = view.querySelector('#chkEnableOcrDetection');
            const hashCheckbox = view.querySelector('#chkEnableHashDetection');
            
            if (ocrCheckbox && hashCheckbox) {
                const updateCollapsibleColors = () => {
                    const ocrHeaders = view.querySelectorAll('.collapsible-header[data-detection-type="ocr"]');
                    const hashHeaders = view.querySelectorAll('.collapsible-header[data-detection-type="hash"]');
                    
                    ocrHeaders.forEach(header => {
                        if (ocrCheckbox.checked) {
                            header.style.backgroundColor = '#52B54B';
                        } else {
                            header.style.backgroundColor = '#808080';
                        }
                    });
                    
                    hashHeaders.forEach(header => {
                        if (hashCheckbox.checked) {
                            header.style.backgroundColor = '#52B54B';
                        } else {
                            header.style.backgroundColor = '#808080';
                        }
                    });
                };
                
                ocrCheckbox.addEventListener('change', (e) => {
                    if (e.target.checked) {
                        hashCheckbox.checked = false;
                    }
                    updateCollapsibleColors();
                });

                hashCheckbox.addEventListener('change', (e) => {
                    if (e.target.checked) {
                        ocrCheckbox.checked = false;
                    }
                    updateCollapsibleColors();
                });
                
                // Listen for data loaded event to update colors
                view.addEventListener('detectionMethodLoaded', () => {
                    updateCollapsibleColors();
                });
                
                updateCollapsibleColors();
            }

            view.querySelector('#btnBulkExportSeries').addEventListener('click', () => {
                backupManager.openBulkExportModal(view);
            });

            view.querySelector('#btnCloseBulkExportModal').addEventListener('click', () => {
                backupManager.closeBulkExportModal(view);
            });

            view.querySelector('#btnCancelBulkExport').addEventListener('click', () => {
                backupManager.closeBulkExportModal(view);
            });

            view.querySelector('#selectBulkExportLibrary').addEventListener('change', () => {
                const libraryId = view.querySelector('#selectBulkExportLibrary').value;
                backupManager.loadBulkExportSeriesList(view, libraryId);
            });

            view.querySelector('#btnSelectAllSeries').addEventListener('click', () => {
                backupManager.selectAllSeries(view);
            });

            view.querySelector('#btnDeselectAllSeries').addEventListener('click', () => {
                backupManager.deselectAllSeries(view);
            });

            view.querySelector('#btnConfirmBulkExport').addEventListener('click', () => {
                backupManager.confirmBulkExport(view);
            });

            view.querySelector('#chkManualSkipExistingMarkers').addEventListener('change', () => {
                dataManager.saveData(this, view);
            });

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
                    const defaults = 'associate producer,based on,cast,casting,cinematography,co-producer,composer,costume design,created by,credits,developed by,directed by,director of photography,editing,editor,end credits,ende,executive producer,fim,fin,fine,guest starring,music by,produced by,producer,production company,production design,screenplay,series producer,sound,special thanks,starring,story by,the end,visual effects,written by,끝,終';
                    view.querySelector('#txtOcrDetectionKeywords').value = defaults;
                    this.updateKeywordDisplay(view);
                    
                    require(['configurationpage?name=CreditsDetectorConfigurationDataManager'], (dataManager) => {
                        dataManager.saveData(this, view);
                    });
                }
            });

            view.querySelector('#btnSaveOcrEnhancements').addEventListener('click', () => {
                dataManager.saveData(this, view);
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
            
            require(['configurationpage?name=CreditsDetectorConfigurationDataManager'], (dataManager) => {
                dataManager.saveData(this, view);
            });
        }

        updateKeywordDisplay(view) {
            const displayArea = view.querySelector('#keywordDisplayArea');
            const hiddenInput = view.querySelector('#txtOcrDetectionKeywords');
            const keywords = hiddenInput.value.split(',').map(k => k.trim()).filter(k => k.length > 0);
            
            keywords.sort((a, b) => a.toLowerCase().localeCompare(b.toLowerCase()));
            
            displayArea.innerHTML = '';
            
            if (keywords.length === 0) {
                displayArea.innerHTML = '<div style="opacity: 0.5; width: 100%; text-align: center; padding: 1.5em;">No keywords configured</div>';
                return;
            }
            
            keywords.forEach(keyword => {
                const chip = document.createElement('div');
                chip.className = 'raised';
                chip.style.cssText = 'display: inline-flex; align-items: center; gap: 0.5em; padding: 0.5em 0.75em; border-radius: 4px; font-size: 0.9em; background-color: #52B54B; color: white;';
                
                const text = document.createElement('span');
                text.textContent = keyword;
                chip.appendChild(text);
                
                const removeBtn = document.createElement('button');
                removeBtn.type = 'button';
                removeBtn.innerHTML = '<i class="md-icon" style="font-size: 18px;">close</i>';
                removeBtn.style.cssText = 'background: rgba(0,0,0,0.2); border: none; border-radius: 50%; width: 22px; height: 22px; display: flex; align-items: center; justify-content: center; cursor: pointer; padding: 0; transition: background 0.2s; color: white;';
                removeBtn.title = 'Remove keyword';
                removeBtn.addEventListener('mouseenter', () => {
                    removeBtn.style.background = 'rgba(0,0,0,0.4)';
                });
                removeBtn.addEventListener('mouseleave', () => {
                    removeBtn.style.background = 'rgba(0,0,0,0.2)';
                });
                removeBtn.addEventListener('click', () => {
                    this.removeKeyword(view, keyword);
                });
                chip.appendChild(removeBtn);
                
                displayArea.appendChild(chip);
            });
        }

        enforceDropdownStyles(view) {
            // Ensure dropdown styles are properly set
            const selects = view.querySelectorAll('select.emby-select');
            selects.forEach(select => {
                if (!select.style.maxWidth && select.id !== 'selectOcrSearchStartUnit') {
                    select.style.maxWidth = '500px';
                }
            });
        }

        onResume(options) {
            super.onResume(options);
            const view = this.view;

            // Always reload partials to ensure fresh content and proper initialization
            // This prevents CSS/component corruption from other plugins
            console.log('Credits Detector: Loading partials...');
            loader.loadPagePartials(view).then(() => {
                console.log('Credits Detector: Partials loaded, initializing page...');
                
                // Ensure event bindings happen after DOM is fully populated
                setTimeout(() => {
                    events.bindTabNavigation(view);
                    this.bindEventListeners(view);
                    
                    // Initialize collapsible sections
                    utils.initializeCollapsibleSections(view);
                    
                    // Listen for keywords loaded event
                    view.addEventListener('keywordsLoaded', () => {
                        this.updateKeywordDisplay(view);
                    });
                    
                    dataManager.loadData(this, view);
                    
                    // Enforce dropdown styles after load
                    this.enforceDropdownStyles(view);
                        
                        // Load donate image
                        const donateImg = view.querySelector('#donateImage');
                        if (donateImg && !donateImg.src) {
                            fetch(ApiClient.getUrl('CreditsDetector/Images/donate.png'), {
                                headers: {
                                    'X-Emby-Token': ApiClient.accessToken()
                                },
                                cache: 'force-cache'
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
                    }, 50);
                }).catch(error => {
                    console.error('Error loading partials:', error);
                    toast({ type: 'error', text: 'Failed to load configuration page. Please refresh the page.' });
                });
        }
        
        onPause() {
            super.onPause();
            
            if (this.progressInterval) {
                clearInterval(this.progressInterval);
                this.progressInterval = null;
            }
            if (this.progressHideTimeout) {
                clearTimeout(this.progressHideTimeout);
                this.progressHideTimeout = null;
            }
        }
    };
});
