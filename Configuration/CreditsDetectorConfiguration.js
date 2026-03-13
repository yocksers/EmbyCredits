define(['baseView', 'loading', 'toast', 'emby-input', 'emby-button', 'emby-checkbox', 
    'configurationpage?name=CreditsDetectorConfigurationLoader', 
    'configurationpage?name=CreditsDetectorConfigurationEvents',
    'configurationpage?name=CreditsDetectorConfigurationUtils',
    'configurationpage?name=CreditsDetectorConfigurationDataManager',
    'configurationpage?name=CreditsDetectorConfigurationSeriesManager',
    'configurationpage?name=CreditsDetectorConfigurationProcessingActions',
    'configurationpage?name=CreditsDetectorConfigurationProgressMonitor',
    'configurationpage?name=CreditsDetectorConfigurationMarkersManager',
    'configurationpage?name=CreditsDetectorConfigurationBackupManager',
    'configurationpage?name=CreditsDetectorConfigurationRulesManager',
    'configurationpage?name=CreditsDetectorConfigurationAutoSkipManager',
    'configurationpage?name=CreditsDetectorConfigurationChapterEditManager'
], function (BaseView, loading, toast, embyInput, embyButton, embyCheckbox, loader, events, utils, dataManager, seriesManager, processingActions, progressMonitor, markersManager, backupManager, rulesManager, autoSkipManager, chapterEditManager) {
    'use strict';

    return class extends BaseView {
        constructor(view, params) {
            super(view, params);
            this.partialsLoaded = false;
            this.progressInterval = null;
            this.progressHideTimeout = null;
            this.lastProgress = null;
        }

        bindEventListeners(view) {
            view.querySelectorAll('.creditsDetectorForm').forEach(form => {
                form.addEventListener('submit', (e) => {
                    e.preventDefault();
                    dataManager.saveData(this, view);
                    return false;
                });
            });

            const btnResetToDefaults = view.querySelector('#btnResetToDefaults');
            if (btnResetToDefaults) {
                btnResetToDefaults.addEventListener('click', () => {
                    dataManager.resetToDefaults(view);
                });
            }

            const btnBrowseTempFolder = view.querySelector('#btnBrowseTempFolder');
            if (btnBrowseTempFolder) {
                btnBrowseTempFolder.addEventListener('click', () => {
                    dataManager.browseTempFolder(view);
                });
            }

            const btnBrowseBackupFolder = view.querySelector('#btnBrowseBackupFolder');
            if (btnBrowseBackupFolder) {
                btnBrowseBackupFolder.addEventListener('click', () => {
                    dataManager.browseBackupFolder(view);
                });
            }

            const btnBrowseLogFolder = view.querySelector('#btnBrowseLogFolder');
            if (btnBrowseLogFolder) {
                btnBrowseLogFolder.addEventListener('click', () => {
                    dataManager.browseLogFolder(view);
                });
            }

            const btnResetAllExceptFolders = view.querySelector('#btnResetAllExceptFolders');
            if (btnResetAllExceptFolders) {
                btnResetAllExceptFolders.addEventListener('click', () => {
                    dataManager.resetAllExceptFolders(view);
                });
            }

            const btnProcessSeries = view.querySelector('#btnProcessSeries');
            if (btnProcessSeries) {
                btnProcessSeries.addEventListener('click', () => {
                    processingActions.processSeries(this, view);
                });
            }

            const btnSaveDetectionSettings = view.querySelector('#btnSaveDetectionSettings');
            if (btnSaveDetectionSettings) {
                btnSaveDetectionSettings.addEventListener('click', () => {
                    dataManager.saveDetectionSettings(view);
                });
            }

            const btnQueueAllSeries = view.querySelector('#btnQueueAllSeries');
            if (btnQueueAllSeries) {
                btnQueueAllSeries.addEventListener('click', () => {
                    processingActions.queueAllSeries(this, view);
                });
            }

            const btnCancelProcessing = view.querySelector('#btnCancelProcessing');
            if (btnCancelProcessing) {
                btnCancelProcessing.addEventListener('click', () => {
                    processingActions.cancelProcessing(this, view);
                });
            }

            const btnClearQueue = view.querySelector('#btnClearQueue');
            if (btnClearQueue) {
                btnClearQueue.addEventListener('click', () => {
                    processingActions.clearQueue(view);
                });
            }

            const btnDryRun = view.querySelector('#btnDryRun');
            if (btnDryRun) {
                btnDryRun.addEventListener('click', () => {
                    processingActions.startDryRun(this, view, false);
                });
            }

            const btnDryRunDebug = view.querySelector('#btnDryRunDebug');
            if (btnDryRunDebug) {
                btnDryRunDebug.addEventListener('click', () => {
                    processingActions.startDryRun(this, view, true);
                });
            }

            const btnTestOcrConnection = view.querySelector('#btnTestOcrConnection');
            if (btnTestOcrConnection) {
                btnTestOcrConnection.addEventListener('click', () => {
                    processingActions.testOcrConnection(view);
                });
            }

            const selectLibraryFilter = view.querySelector('#selectLibraryFilter');
            if (selectLibraryFilter) {
                selectLibraryFilter.addEventListener('change', () => {
                    const libraryId = view.querySelector('#selectLibraryFilter').value;
                    seriesManager.loadSeriesList(view, libraryId);
                });
            }

            const selectSeries = view.querySelector('#selectSeries');
            if (selectSeries) {
                selectSeries.addEventListener('change', () => {
                    const seriesId = view.querySelector('#selectSeries').value;
                    seriesManager.loadEpisodesForSeries(view, seriesId);
                });
            }

            const selectSeriesForMarkers = view.querySelector('#selectSeriesForMarkers');
            if (selectSeriesForMarkers) {
                selectSeriesForMarkers.addEventListener('change', () => {
                    const seriesId = view.querySelector('#selectSeriesForMarkers').value;
                    markersManager.displayMarkers(this, view);
                });
            }

            const btnShowSeasonValidation = view.querySelector('#btnShowSeasonValidation');
            if (btnShowSeasonValidation) {
                btnShowSeasonValidation.addEventListener('click', () => {
                    markersManager.showSeasonValidation(this, view);
                });
            }

            const btnExportBackup = view.querySelector('#btnExportBackup');
            if (btnExportBackup) {
                btnExportBackup.addEventListener('click', () => {
                    backupManager.exportBackup(view);
                });
            }

            const btnImportBackup = view.querySelector('#btnImportBackup');
            if (btnImportBackup) {
                btnImportBackup.addEventListener('click', () => {
                    backupManager.importBackup(view);
                });
            }

            // Initialize chapter editor if its elements are present
            const chapterBrowserList = view.querySelector('#chapterBrowserList');
            if (chapterBrowserList) {
                chapterEditManager.init(view);
            }

            const detectionModeSelect = view.querySelector('#selectDetectionMode');
            
            if (detectionModeSelect) {
                const updateCollapsibleColors = () => {
                    const mode = detectionModeSelect.value;
                    const ocrHeaders = view.querySelectorAll('.collapsible-header[data-detection-type="ocr"]');
                    const hashHeaders = view.querySelectorAll('.collapsible-header[data-detection-type="hash"]');
                    
                    // For fallback modes, both OCR and Hash settings are used
                    const isFallbackMode = mode === 'OcrWithHashFallback' || mode === 'HashWithOcrFallback';
                    
                    ocrHeaders.forEach(header => {
                        if (mode === 'OcrOnly' || mode === 'OcrWithHashFallback' || mode === 'HashWithOcrFallback') {
                            header.style.backgroundColor = '#52B54B';
                        } else {
                            header.style.backgroundColor = '#808080';
                        }
                    });
                    
                    hashHeaders.forEach(header => {
                        if (mode === 'HashOnly' || mode === 'HashWithOcrFallback' || mode === 'OcrWithHashFallback') {
                            header.style.backgroundColor = '#52B54B';
                        } else {
                            header.style.backgroundColor = '#808080';
                        }
                    });
                };
                
                detectionModeSelect.addEventListener('change', () => {
                    updateCollapsibleColors();
                });
                
                // Listen for data loaded event to update colors
                view.addEventListener('detectionMethodLoaded', () => {
                    updateCollapsibleColors();
                });
                
                updateCollapsibleColors();
            }

            const btnBulkExportSeries = view.querySelector('#btnBulkExportSeries');
            if (btnBulkExportSeries) {
                btnBulkExportSeries.addEventListener('click', () => {
                    backupManager.openBulkExportModal(view);
                });
            }

            const btnCloseBulkExportModal = view.querySelector('#btnCloseBulkExportModal');
            if (btnCloseBulkExportModal) {
                btnCloseBulkExportModal.addEventListener('click', () => {
                    backupManager.closeBulkExportModal(view);
                });
            }

            const btnCancelBulkExport = view.querySelector('#btnCancelBulkExport');
            if (btnCancelBulkExport) {
                btnCancelBulkExport.addEventListener('click', () => {
                    backupManager.closeBulkExportModal(view);
                });
            }

            const selectBulkExportLibrary = view.querySelector('#selectBulkExportLibrary');
            if (selectBulkExportLibrary) {
                selectBulkExportLibrary.addEventListener('change', () => {
                    const libraryId = view.querySelector('#selectBulkExportLibrary').value;
                    backupManager.loadBulkExportSeriesList(view, libraryId);
                });
            }

            const btnSelectAllSeries = view.querySelector('#btnSelectAllSeries');
            if (btnSelectAllSeries) {
                btnSelectAllSeries.addEventListener('click', () => {
                    backupManager.selectAllSeries(view);
                });
            }

            const btnDeselectAllSeries = view.querySelector('#btnDeselectAllSeries');
            if (btnDeselectAllSeries) {
                btnDeselectAllSeries.addEventListener('click', () => {
                    backupManager.deselectAllSeries(view);
                });
            }

            const btnConfirmBulkExport = view.querySelector('#btnConfirmBulkExport');
            if (btnConfirmBulkExport) {
                btnConfirmBulkExport.addEventListener('click', () => {
                    backupManager.confirmBulkExport(view);
                });
            }

            const chkManualSkipExistingMarkers = view.querySelector('#chkManualSkipExistingMarkers');
            if (chkManualSkipExistingMarkers) {
                chkManualSkipExistingMarkers.addEventListener('change', () => {
                    dataManager.saveData(this, view);
                });
            }

            const btnAddKeyword = view.querySelector('#btnAddKeyword');
            if (btnAddKeyword) {
                btnAddKeyword.addEventListener('click', () => {
                    this.addKeyword(view);
                });
            }

            const txtNewKeyword = view.querySelector('#txtNewKeyword');
            if (txtNewKeyword) {
                txtNewKeyword.addEventListener('keypress', (e) => {
                    if (e.key === 'Enter') {
                        e.preventDefault();
                        this.addKeyword(view);
                    }
                });
            }

            const btnResetKeywords = view.querySelector('#btnResetKeywords');
            if (btnResetKeywords) {
                btnResetKeywords.addEventListener('click', () => {
                    if (confirm('Reset keywords to defaults? This will replace all current keywords.')) {
                        const defaults = 'associate producer,based on,cast,casting,cinematography,co-producer,composer,costume design,created by,credits,developed by,directed by,director of photography,editing,editor,end credits,ende,executive producer,fim,fin,fine,guest starring,music by,produced by,producer,production company,production design,screenplay,series producer,sound,special thanks,starring,story by,the end,visual effects,written by,끝,終,キャスト,スタッフ,監督,脚本,音楽,製作,制作,プロデューサー,原作,演出,撮影,編集,おわり,提供,協力,出演';
                        view.querySelector('#txtOcrDetectionKeywords').value = defaults;
                        this.updateKeywordDisplay(view);
                        
                        require(['configurationpage?name=CreditsDetectorConfigurationDataManager'], (dataManager) => {
                            dataManager.saveData(this, view);
                        });
                    }
                });
            }

            const btnSaveOcrEnhancements = view.querySelector('#btnSaveOcrEnhancements');
            if (btnSaveOcrEnhancements) {
                btnSaveOcrEnhancements.addEventListener('click', () => {
                    dataManager.saveData(this, view);
                });
            }

            const btnSaveAutoSkip = view.querySelector('#btnSaveAutoSkip');
            if (btnSaveAutoSkip) {
                btnSaveAutoSkip.addEventListener('click', () => {
                    dataManager.saveData(this, view);
                });
            }

            const txtAutoSkipSearch = view.querySelector('#txtAutoSkipSearch');
            if (txtAutoSkipSearch) {
                txtAutoSkipSearch.addEventListener('input', () => {
                    autoSkipManager.filterSeries(view, txtAutoSkipSearch.value);
                });
            }

            const btnAutoSkipSelectAll = view.querySelector('#btnAutoSkipSelectAll');
            if (btnAutoSkipSelectAll) {
                btnAutoSkipSelectAll.addEventListener('click', () => {
                    autoSkipManager.selectAll(view);
                });
            }

            const btnAutoSkipDeselectAll = view.querySelector('#btnAutoSkipDeselectAll');
            if (btnAutoSkipDeselectAll) {
                btnAutoSkipDeselectAll.addEventListener('click', () => {
                    autoSkipManager.deselectAll(view);
                });
            }

            const selectOcrEngine = view.querySelector('#selectOcrEngine');
            if (selectOcrEngine) {
                selectOcrEngine.addEventListener('change', () => {
                    this.toggleOcrEngineFields(view);
                });
                this.toggleOcrEngineFields(view);
            }
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

            loader.loadPagePartials(view).then(() => {
                
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
                    
                    // Restore last progress if available (only if not currently running)
                    if (this.lastProgress && !this.lastProgress.IsRunning) {
                        setTimeout(() => {
                            progressMonitor.updateProgressUI(view, this.lastProgress);
                            progressMonitor.updateResults(view, this.lastProgress, this.isDryRun);
                            const container = view.querySelector('#progressContainer');
                            if (container) container.style.display = 'block';
                        }, 100);
                    }
                        
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
        
        toggleOcrEngineFields(view) {
            const selectOcrEngine = view.querySelector('#selectOcrEngine');
            const lblOcrEndpoint = view.querySelector('#lblOcrEndpoint');
            const descOcrEndpoint = view.querySelector('#descOcrEndpoint');
            const txtOcrEndpoint = view.querySelector('#txtOcrEndpoint');
            
            if (selectOcrEngine) {
                const selectedEngine = selectOcrEngine.value;
                const currentValue = txtOcrEndpoint ? txtOcrEndpoint.value : '';
                
                if (selectedEngine === 'PaddleOCR') {
                    if (lblOcrEndpoint) lblOcrEndpoint.textContent = 'PaddleOCR API Endpoint';
                    if (descOcrEndpoint) descOcrEndpoint.innerHTML = 'URL of your PaddleOCR API (e.g., <code>http://localhost:8866</code> or <code>http://192.168.1.100:8866</code>)';
                    if (txtOcrEndpoint && (currentValue === 'http://localhost:8884' || currentValue === '')) {
                        txtOcrEndpoint.value = 'http://localhost:8866';
                    }
                } else {
                    if (lblOcrEndpoint) lblOcrEndpoint.textContent = 'Tesseract OCR API Endpoint';
                    if (descOcrEndpoint) descOcrEndpoint.innerHTML = 'URL of your Tesseract OCR API (e.g., <code>http://localhost:8884</code> or <code>http://192.168.1.100:8884</code>)';
                    if (txtOcrEndpoint && (currentValue === 'http://localhost:8866' || currentValue === '')) {
                        txtOcrEndpoint.value = 'http://localhost:8884';
                    }
                }
            }
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
