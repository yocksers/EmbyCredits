define(['baseView', 'loading', 'toast', 'emby-input', 'emby-button', 'emby-checkbox'], function (BaseView, loading, toast) {
    'use strict';

    const pluginId = "b1a65a73-a620-432a-9f5b-285038031c26";

    function getPluginConfiguration() {
        return ApiClient.getPluginConfiguration(pluginId);
    }

    function updatePluginConfiguration(config) {
        return ApiClient.updatePluginConfiguration(pluginId, config);
    }

    return class extends BaseView {
        constructor(view, params) {
            super(view, params);

            view.querySelector('.playbackReporterForm').addEventListener('submit', (e) => {
                e.preventDefault();
                this.saveData(view);
                return false;
            });

            view.querySelector('#btnResetToDefaults').addEventListener('click', () => {
                this.resetToDefaults(view);
            });

            view.querySelector('#btnBrowseTempFolder').addEventListener('click', () => {
                this.browseTempFolder(view);
            });

            view.querySelector('#btnProcessSeries').addEventListener('click', () => {
                const seriesId = view.querySelector('#selectSeries').value;
                const episodeId = view.querySelector('#selectEpisode').value;

                if (episodeId) {
                    loading.show();
                    ApiClient.ajax({
                        type: 'POST',
                        url: ApiClient.getUrl('CreditsDetector/ProcessEpisode'),
                        data: JSON.stringify({ ItemId: episodeId }),
                        contentType: 'application/json'
                    }).then(response => {
                        loading.hide();
                        toast(response.Message || 'Episode queued for processing.');
                        view.querySelector('#progressContainer').style.display = 'block';
                        this.startProgressPolling(view);
                    }).catch(error => {
                        loading.hide();
                        console.error('Error processing episode:', error);
                        toast({ type: 'error', text: 'Failed to process episode. Check server logs.' });
                    });
                    return;
                } else if (!seriesId) {
                    toast({ type: 'error', text: 'Please select a TV show first.' });
                    return;
                }

                loading.show();
                ApiClient.ajax({
                    type: 'POST',
                    url: ApiClient.getUrl('CreditsDetector/ProcessSeries'),
                    contentType: 'application/json',
                    data: JSON.stringify({ SeriesId: seriesId })
                }).then(response => {
                    loading.hide();
                    toast(`Episodes queued. They will be processed one at a time.`);
                    view.querySelector('#progressContainer').style.display = 'block';
                    this.startProgressPolling(view);
                }).catch(error => {
                    loading.hide();
                    console.error('Error processing series:', error);
                    toast({ type: 'error', text: 'Failed to start processing. Check server logs.' });
                });
            });

            view.querySelector('#btnViewMarkers').addEventListener('click', () => {
                const seriesId = view.querySelector('#selectSeriesForMarkers').value;
                if (!seriesId) {
                    toast({ type: 'error', text: 'Please select a TV show first.' });
                    return;
                }

                loading.show();
                ApiClient.getJSON(ApiClient.getUrl('CreditsDetector/GetSeriesMarkers', { SeriesId: seriesId })).then(response => {
                    loading.hide();
                    if (response.Success) {
                        this.displayMarkers(view, response);
                    } else {
                        toast({ type: 'error', text: response.Message || 'Failed to load markers' });
                    }
                }).catch(error => {
                    loading.hide();
                    console.error('Error loading markers:', error);
                    toast({ type: 'error', text: 'Failed to load markers. Check server logs.' });
                });
            });

            view.querySelector('#selectSeries').addEventListener('change', (e) => {
                const seriesId = e.target.value;
                const episodeSelect = view.querySelector('#selectEpisode');

                episodeSelect.innerHTML = '<option value="">-- Loading Episodes... --</option>';

                if (!seriesId) {
                    episodeSelect.innerHTML = '<option value="">-- Select Show First --</option>';
                    return;
                }

                loading.show();
                ApiClient.getJSON(ApiClient.getUrl('Items', {
                    ParentId: seriesId,
                    IncludeItemTypes: 'Episode',
                    Recursive: true,
                    Fields: 'ParentId,SeasonName,IndexNumber,ParentIndexNumber',
                    SortBy: 'ParentIndexNumber,IndexNumber',
                    SortOrder: 'Ascending'
                })).then(result => {
                    loading.hide();

                    episodeSelect.innerHTML = '<option value="">-- All Episodes --</option>';

                    result.Items.forEach(episode => {
                        const option = document.createElement('option');
                        option.value = episode.Id;
                        const seasonEp = `S${(episode.ParentIndexNumber || 0).toString().padStart(2, '0')}E${(episode.IndexNumber || 0).toString().padStart(2, '0')}`;
                        option.textContent = `${seasonEp} - ${episode.Name || 'Unknown'}`;
                        episodeSelect.appendChild(option);
                    });
                }).catch(error => {
                    loading.hide();
                    console.error('Error loading episodes:', error);
                    episodeSelect.innerHTML = '<option value="">-- Error Loading Episodes --</option>';
                    toast({ type: 'error', text: 'Failed to load episodes.' });
                });
            });

            view.querySelector('#selectLibraryFilter').addEventListener('change', (e) => {
                this.loadSeriesList(view, e.target.value);
            });

            view.querySelector('#btnQueueAll').addEventListener('click', () => {
                loading.show();
                ApiClient.ajax({
                    type: 'POST',
                    url: ApiClient.getUrl('CreditsDetector/TriggerDetection')
                }).then(response => {
                    loading.hide();
                    toast(response.Message || 'All episodes queued for processing.');
                    view.querySelector('#progressContainer').style.display = 'block';
                    this.startProgressPolling(view);
                }).catch(error => {
                    loading.hide();
                    console.error('Error queueing all series:', error);
                    toast({ type: 'error', text: 'Failed to queue all series. Check server logs.' });
                });
            });

            view.querySelector('#btnCancelProcessing').addEventListener('click', () => {
                if (confirm('Are you sure you want to cancel the current processing?')) {
                    loading.show();
                    ApiClient.ajax({
                        type: 'POST',
                        url: ApiClient.getUrl('CreditsDetector/CancelDetection')
                    }).then(response => {
                        loading.hide();
                        toast(response.Message || 'Cancellation requested.');
                    }).catch(error => {
                        loading.hide();
                        console.error('Error cancelling processing:', error);
                        toast({ type: 'error', text: 'Failed to cancel processing.' });
                    });
                }
            });

            view.querySelector('#btnClearQueue').addEventListener('click', () => {
                if (confirm('Are you sure you want to clear the processing queue? This will reset all processing state.')) {
                    loading.show();
                    ApiClient.ajax({
                        type: 'POST',
                        url: ApiClient.getUrl('CreditsDetector/ClearQueue')
                    }).then(response => {
                        loading.hide();
                        const message = response.Message || 'Queue cleared.';
                        toast(message);
                        console.log('Clear queue response:', response);
                    }).catch(error => {
                        loading.hide();
                        console.error('Error clearing queue:', error);
                        toast({ type: 'error', text: 'Failed to clear queue.' });
                    });
                }
            });

            view.querySelector('#btnDryRun').addEventListener('click', () => {
                const seriesId = view.querySelector('#selectSeries').value;
                const episodeId = view.querySelector('#selectEpisode').value;

                if (episodeId) {
                    loading.show();
                    ApiClient.ajax({
                        type: 'POST',
                        url: ApiClient.getUrl('CreditsDetector/DryRunSeries'),
                        data: JSON.stringify({ EpisodeId: episodeId }),
                        contentType: 'application/json'
                    }).then(response => {
                        loading.hide();
                        toast(response.Message || 'Dry run started for episode.');
                        view.querySelector('#progressContainer').style.display = 'block';
                        this.startProgressPolling(view);
                    }).catch(error => {
                        loading.hide();
                        console.error('Error starting dry run:', error);
                        toast({ type: 'error', text: 'Failed to start dry run. Check server logs.' });
                    });
                    return;
                } else if (!seriesId) {
                    toast({ type: 'error', text: 'Please select a TV show or episode first.' });
                    return;
                }

                loading.show();
                ApiClient.ajax({
                    type: 'POST',
                    url: ApiClient.getUrl('CreditsDetector/DryRunSeries'),
                    contentType: 'application/json',
                    data: JSON.stringify({ SeriesId: seriesId })
                }).then(response => {
                    loading.hide();
                    toast(response.Message || 'Dry run started. No markers will be saved.');
                    view.querySelector('#progressContainer').style.display = 'block';
                    this.startProgressPolling(view);
                }).catch(error => {
                    loading.hide();
                    console.error('Error starting dry run:', error);
                    toast({ type: 'error', text: 'Failed to start dry run. Check server logs.' });
                });
            });

            view.querySelector('#btnDryRun').addEventListener('click', () => {
                const seriesId = view.querySelector('#selectSeries').value;
                const episodeId = view.querySelector('#selectEpisode').value;

                if (episodeId) {
                    loading.show();
                    ApiClient.ajax({
                        type: 'POST',
                        url: ApiClient.getUrl('CreditsDetector/DryRunSeries'),
                        data: JSON.stringify({ EpisodeId: episodeId }),
                        contentType: 'application/json'
                    }).then(response => {
                        loading.hide();
                        toast(response.Message || 'Dry run started for episode.');
                        view.querySelector('#progressContainer').style.display = 'block';
                        this.startProgressPolling(view);
                    }).catch(error => {
                        loading.hide();
                        console.error('Error starting dry run:', error);
                        toast({ type: 'error', text: 'Failed to start dry run. Check server logs.' });
                    });
                    return;
                } else if (!seriesId) {
                    toast({ type: 'error', text: 'Please select a TV show or episode first.' });
                    return;
                }

                loading.show();
                ApiClient.ajax({
                    type: 'POST',
                    url: ApiClient.getUrl('CreditsDetector/DryRunSeries'),
                    contentType: 'application/json',
                    data: JSON.stringify({ SeriesId: seriesId })
                }).then(response => {
                    loading.hide();
                    toast(response.Message || 'Dry run started. No markers will be saved.');
                    view.querySelector('#progressContainer').style.display = 'block';
                    this.startProgressPolling(view);
                }).catch(error => {
                    loading.hide();
                    console.error('Error starting dry run:', error);
                    toast({ type: 'error', text: 'Failed to start dry run. Check server logs.' });
                });
            });

            view.querySelector('#btnDryRunDebug').addEventListener('click', () => {
                const seriesId = view.querySelector('#selectSeries').value;
                const episodeId = view.querySelector('#selectEpisode').value;

                if (episodeId) {
                    loading.show();
                    ApiClient.ajax({
                        type: 'POST',
                        url: ApiClient.getUrl('CreditsDetector/DryRunSeriesDebug'),
                        data: JSON.stringify({ EpisodeId: episodeId }),
                        contentType: 'application/json'
                    }).then(response => {
                        loading.hide();
                        toast(response.Message || 'Debug dry run started for episode.');
                        view.querySelector('#progressContainer').style.display = 'block';
                        this.startProgressPolling(view, true); // Pass true to indicate debug mode
                    }).catch(error => {
                        loading.hide();
                        console.error('Error starting debug dry run:', error);
                        toast({ type: 'error', text: 'Failed to start debug dry run. Check server logs.' });
                    });
                    return;
                } else if (!seriesId) {
                    toast({ type: 'error', text: 'Please select a TV show or episode first.' });
                    return;
                }

                loading.show();
                ApiClient.ajax({
                    type: 'POST',
                    url: ApiClient.getUrl('CreditsDetector/DryRunSeriesDebug'),
                    contentType: 'application/json',
                    data: JSON.stringify({ SeriesId: seriesId })
                }).then(response => {
                    loading.hide();
                    toast(response.Message || 'Debug dry run started. Detailed logs will be captured.');
                    view.querySelector('#progressContainer').style.display = 'block';
                    this.startProgressPolling(view, true); // Pass true to indicate debug mode
                }).catch(error => {
                    loading.hide();
                    console.error('Error starting debug dry run:', error);
                    toast({ type: 'error', text: 'Failed to start debug dry run. Check server logs.' });
                });
            });

            view.querySelector('#btnTestOcrConnection').addEventListener('click', () => {
                const ocrEndpoint = view.querySelector('#txtOcrEndpoint').value;
                const successIndicator = view.querySelector('#ocrTestSuccess');

                if (!ocrEndpoint) {
                    toast({ type: 'error', text: 'Please enter an OCR endpoint URL first.' });
                    return;
                }

                if (successIndicator) {
                    successIndicator.style.display = 'none';
                }

                loading.show();
                ApiClient.ajax({
                    type: 'POST',
                    url: ApiClient.getUrl('CreditsDetector/TestOcrConnection'),
                    data: JSON.stringify({ OcrEndpoint: ocrEndpoint }),
                    contentType: 'application/json',
                    dataType: 'json'
                }).then(response => {
                    loading.hide();
                    console.log('OCR test response:', response);
                    if (response && response.Success) {
                        toast(response.Message || 'Connection successful!');

                        if (successIndicator) {
                            successIndicator.style.display = 'inline';
                        }
                    } else {
                        toast({ type: 'error', text: response && response.Message ? response.Message : 'Connection failed' });

                        if (successIndicator) {
                            successIndicator.style.display = 'none';
                        }
                    }
                }).catch(error => {
                    loading.hide();
                    console.error('Error testing OCR connection:', error);
                    toast({ type: 'error', text: 'Failed to test connection. Check console for details.' });

                    if (successIndicator) {
                        successIndicator.style.display = 'none';
                    }
                });
            });

            this.progressInterval = null;
            this.progressHideTimeout = null;
        }

        displayMarkers(view, response) {
            const markersDisplay = view.querySelector('#markersDisplay');
            const markersSeriesName = view.querySelector('#markersSeriesName');
            const markersContent = view.querySelector('#markersContent');

            markersSeriesName.textContent = `${response.SeriesName} - ${response.TotalEpisodes} Episodes`;

            let html = '<div style="display: flex; flex-direction: column; gap: 1em;">';

            response.Episodes.forEach(ep => {
                const hasMarker = ep.HasCreditsMarker;
                const markerColor = hasMarker ? '#52B54B' : '#E5A00D';

                html += `<div style="background: rgba(255,255,255,0.05); padding: 1em; border-radius: 4px; border-left: 3px solid ${markerColor};">`;
                html += `<div style="display: flex; justify-content: space-between; align-items: start; margin-bottom: 0.5em;">`;
                html += `<div>`;
                html += `<strong>${ep.SeasonEpisode}: ${ep.EpisodeName}</strong><br>`;
                html += `<span style="color: rgba(255,255,255,0.6); font-size: 0.9em;">Duration: ${ep.Duration}</span>`;
                html += `</div>`;
                html += `<div style="text-align: right;">`;
                if (hasMarker) {
                    html += `<span style="color: #52B54B; font-weight: bold;">✓ Has Credits Marker</span>`;
                } else {
                    html += `<span style="color: #E5A00D;">⚠ No Credits Marker</span>`;
                }
                html += `</div>`;
                html += `</div>`;

                if (ep.Markers && ep.Markers.length > 0) {
                    html += `<div style="margin-top: 0.75em; padding-top: 0.75em; border-top: 1px solid rgba(255,255,255,0.1);">`;
                    html += `<strong style="color: #52B54B;">Credits Markers:</strong><br>`;
                    ep.Markers.forEach(marker => {
                        html += `<div style="margin: 0.5em 0; padding: 0.5em; background: rgba(82,181,75,0.1); border-radius: 3px;">`;
                        html += `<span style="color: #52B54B; font-weight: bold;">${marker.StartTime}</span> - `;
                        html += `${marker.Name || 'Credits'} `;
                        if (marker.MarkerType) {
                            html += `<span style="color: rgba(255,255,255,0.5); font-size: 0.85em;">(${marker.MarkerType})</span>`;
                        }
                        html += `</div>`;
                    });
                    html += `</div>`;
                }

                if (ep.AllChapters && ep.AllChapters.length > 1) {
                    html += `<details style="margin-top: 0.75em; cursor: pointer;">`;
                    html += `<summary style="color: rgba(255,255,255,0.7); font-size: 0.9em;">All Chapters (${ep.AllChapters.length})</summary>`;
                    html += `<div style="margin-top: 0.5em; padding: 0.5em; background: rgba(255,255,255,0.02); border-radius: 3px;">`;
                    ep.AllChapters.forEach(chapter => {
                        const isCredits = chapter.MarkerType === 'CreditsStart' || (chapter.Name && chapter.Name.toLowerCase().includes('credit'));
                        html += `<div style="padding: 0.25em 0; color: ${isCredits ? '#52B54B' : 'rgba(255,255,255,0.7)'};">`;
                        html += `${chapter.StartTime} - ${chapter.Name || 'Unnamed'}`;
                        if (chapter.MarkerType) {
                            html += ` <span style="color: rgba(255,255,255,0.4); font-size: 0.85em;">(${chapter.MarkerType})</span>`;
                        }
                        html += `</div>`;
                    });
                    html += `</div>`;
                    html += `</details>`;
                }

                html += `</div>`;
            });

            html += '</div>';

            markersContent.innerHTML = html;
            markersDisplay.style.display = 'block';
        }

        startProgressPolling(view, isDebugMode = false) {

            if (this.progressInterval) {
                clearInterval(this.progressInterval);
            }

            if (this.progressHideTimeout) {
                clearTimeout(this.progressHideTimeout);
                this.progressHideTimeout = null;
            }

            const btnCancel = view.querySelector('#btnCancelProcessing');
            if (btnCancel) btnCancel.style.display = 'inline-block';

            this.progressInterval = setInterval(() => {
                ApiClient.getJSON(ApiClient.getUrl('CreditsDetector/GetProgress')).then(progress => {
                    if (!progress.IsRunning) {

                        clearInterval(this.progressInterval);
                        this.progressInterval = null;

                        const btnCancel = view.querySelector('#btnCancelProcessing');
                        if (btnCancel) btnCancel.style.display = 'none';

                        this.updateProgressUI(view, progress);

                        this.progressHideTimeout = setTimeout(() => {
                            const progressContainer = view.querySelector('#progressContainer');
                            if (progressContainer) progressContainer.style.display = 'none';
                            this.progressHideTimeout = null;
                        }, 10000);

                        const message = progress.CurrentItem === 'Cancelled' 
                            ? `Processing cancelled. ${progress.SuccessfulItems} succeeded, ${progress.FailedItems} failed.`
                            : progress.CurrentItem === 'Dry Run Complete'
                            ? `Dry run complete! ${progress.SuccessfulItems} detected, ${progress.FailedItems} failed. No markers were saved.`
                            : `Processing complete! ${progress.SuccessfulItems} succeeded, ${progress.FailedItems} failed.`;
                        toast(message);

                        // Download debug log if in debug mode
                        if (isDebugMode && progress.CurrentItem !== 'Cancelled') {
                            setTimeout(() => {
                                this.downloadDebugLog();
                            }, 1000);
                        }
                        return;
                    }

                    this.updateProgressUI(view, progress);
                }).catch(error => {
                    console.error('Error fetching progress:', error);
                    clearInterval(this.progressInterval);
                    this.progressInterval = null;
                });
            }, 500);
        }

        downloadDebugLog() {
            loading.show();
            fetch(ApiClient.getUrl('CreditsDetector/GetDebugLog'), {
                method: 'GET',
                headers: {
                    'X-Emby-Token': ApiClient.accessToken()
                }
            })
            .then(response => {
                if (!response.ok) {
                    throw new Error('Failed to download debug log');
                }
                return response.blob();
            })
            .then(blob => {
                loading.hide();
                const url = window.URL.createObjectURL(blob);
                const a = document.createElement('a');
                a.style.display = 'none';
                a.href = url;
                const timestamp = new Date().toISOString().replace(/[:.]/g, '-').slice(0, -5);
                a.download = `credits-detection-debug-${timestamp}.log`;
                document.body.appendChild(a);
                a.click();
                window.URL.revokeObjectURL(url);
                document.body.removeChild(a);
                toast('Debug log downloaded successfully!');
            })
            .catch(error => {
                loading.hide();
                console.error('Error downloading debug log:', error);
                toast({ type: 'error', text: 'Failed to download debug log.' });
            });
        }

        updateProgressUI(view, progress) {
            const progressBar = view.querySelector('#progressBar');
            const percentText = view.querySelector('#percentText');
            const itemProgressBar = view.querySelector('#itemProgressBar');
            const currentItem = view.querySelector('#currentItem');
            const progressCount = view.querySelector('#progressCount');
            const successCount = view.querySelector('#successCount');
            const failedCount = view.querySelector('#failedCount');
            const etaText = view.querySelector('#etaText');
            const failureDetails = view.querySelector('#failureDetails');
            const failureList = view.querySelector('#failureList');

            if (!progressBar || !percentText) return;

            const percent = progress.PercentComplete || 0;
            progressBar.style.width = percent + '%';
            percentText.textContent = percent.toFixed(0) + '%';

            const itemProgress = progress.CurrentItemProgress || 0;
            if (itemProgressBar) itemProgressBar.style.width = itemProgress + '%';

            if (currentItem) currentItem.textContent = progress.CurrentItem || 'Starting...';
            if (progressCount) progressCount.textContent = `${progress.ProcessedItems}/${progress.TotalItems}`;
            if (successCount) successCount.textContent = progress.SuccessfulItems || 0;
            if (failedCount) failedCount.textContent = progress.FailedItems || 0;

            if (etaText) {
                if (progress.EstimatedTimeRemaining) {
                    const eta = progress.EstimatedTimeRemaining;
                    const minutes = Math.floor(eta / 60);
                    const seconds = Math.floor(eta % 60);
                    etaText.textContent = minutes > 0 ? `${minutes}m ${seconds}s` : `${seconds}s`;
                } else {
                    etaText.textContent = 'Calculating...';
                }
            }

            console.log('updateProgressUI - FailureReasons:', progress.FailureReasons);
            console.log('updateProgressUI - SuccessDetails:', progress.SuccessDetails);

            if (progress.FailureReasons && Object.keys(progress.FailureReasons).length > 0) {
                console.log('Showing failure details');
                failureDetails.style.display = 'block';
                failureList.innerHTML = '';

                Object.entries(progress.FailureReasons).forEach(([episode, reason]) => {
                    const failureItem = document.createElement('div');
                    failureItem.style.cssText = 'padding: 0.5em; margin-bottom: 0.5em; border-left: 3px solid #E5A54A; background: rgba(255,255,255,0.03);';
                    failureItem.innerHTML = `<strong style="color: #e0e0e0;">${episode}</strong><br/><span style="color: #E5A54A; font-size: 0.9em;">${reason}</span>`;
                    failureList.appendChild(failureItem);
                });
            } else {
                console.log('Hiding failure details');
                failureDetails.style.display = 'none';
            }

            const successDetails = view.querySelector('#successDetails');
            const successList = view.querySelector('#successList');
            if (progress.SuccessDetails && Object.keys(progress.SuccessDetails).length > 0) {
                console.log('Showing success details');
                successDetails.style.display = 'block';
                successList.innerHTML = '';

                Object.entries(progress.SuccessDetails).forEach(([episode, timestamp]) => {
                    console.log('Success entry:', episode, '→', timestamp);
                    const successItem = document.createElement('div');
                    successItem.style.cssText = 'padding: 0.5em; margin-bottom: 0.5em; border-left: 3px solid #52b54b; background: rgba(255,255,255,0.03);';
                    successItem.innerHTML = `<strong style="color: #e0e0e0;">${episode}</strong><br/><span style="color: #52b54b; font-size: 0.9em; font-weight: bold;">Credits marker added at ${timestamp}</span>`;
                    successList.appendChild(successItem);
                });
            } else {
                console.log('Hiding success details');
                successDetails.style.display = 'none';
            }
        }

        displayDetectionResults(view, progress) {
            const failureDetails = view.querySelector('#failureDetails');
            const failureList = view.querySelector('#failureList');
            const successDetails = view.querySelector('#successDetails');
            const successList = view.querySelector('#successList');

            if (!failureDetails || !successDetails) return;

            if (progress.FailureReasons && Object.keys(progress.FailureReasons).length > 0) {
                failureDetails.style.display = 'block';
                if (failureList) failureList.innerHTML = '';

                Object.entries(progress.FailureReasons).forEach(([episode, reason]) => {
                    const failureItem = document.createElement('div');
                    failureItem.style.cssText = 'padding: 0.5em; margin-bottom: 0.5em; border-left: 3px solid #E5A54A; background: rgba(255,255,255,0.03);';
                    failureItem.innerHTML = `<strong style="color: #e0e0e0;">${episode}</strong><br/><span style="color: #E5A54A; font-size: 0.9em;">${reason}</span>`;
                    if (failureList) failureList.appendChild(failureItem);
                });
            } else {
                failureDetails.style.display = 'none';
            }

            if (progress.SuccessDetails && Object.keys(progress.SuccessDetails).length > 0) {
                successDetails.style.display = 'block';
                if (successList) successList.innerHTML = '';

                Object.entries(progress.SuccessDetails).forEach(([episode, timestamp]) => {
                    console.log('Success entry (displayDetectionResults):', episode, '→', timestamp);
                    const successItem = document.createElement('div');
                    successItem.style.cssText = 'padding: 0.5em; margin-bottom: 0.5em; border-left: 3px solid #52b54b; background: rgba(255,255,255,0.03);';
                    successItem.innerHTML = `<strong style="color: #e0e0e0;">${episode}</strong><br/><span style="color: #52b54b; font-size: 0.9em; font-weight: bold;">Credits marker added at ${timestamp}</span>`;
                    if (successList) successList.appendChild(successItem);
                });
            } else {
                successDetails.style.display = 'none';
            }
        }

        loadSeriesList(view, libraryId = '') {
            const query = {
                IncludeItemTypes: 'Series',
                Recursive: true,
                Fields: 'DateCreated,ProductionYear'
            };

            if (libraryId) {
                query.ParentId = libraryId;
            }

            loading.show();
            ApiClient.getJSON(ApiClient.getUrl('Items', query)).then(result => {
                loading.hide();
                
                const selectSeries = view.querySelector('#selectSeries');
                const selectSeriesForMarkers = view.querySelector('#selectSeriesForMarkers');

                selectSeries.innerHTML = '<option value="">-- Select a TV Show --</option>';
                selectSeriesForMarkers.innerHTML = '<option value="">-- Select a TV Show --</option>';

                // Sort by name
                const series = result.Items.sort((a, b) => (a.Name || '').localeCompare(b.Name || ''));

                series.forEach(s => {
                    const option1 = document.createElement('option');
                    option1.value = s.Id;
                    option1.textContent = s.ProductionYear ? `${s.Name} (${s.ProductionYear})` : s.Name;
                    selectSeries.appendChild(option1);

                    const option2 = document.createElement('option');
                    option2.value = s.Id;
                    option2.textContent = s.ProductionYear ? `${s.Name} (${s.ProductionYear})` : s.Name;
                    selectSeriesForMarkers.appendChild(option2);
                });
            }).catch(error => {
                loading.hide();
                console.error('Error loading series list:', error);
                toast({ type: 'error', text: 'Failed to load TV shows.' });
            });
        }

        loadLibraryFilter(view) {
            ApiClient.getJSON(ApiClient.getUrl('Library/MediaFolders')).then(response => {
                const selectLibraryFilter = view.querySelector('#selectLibraryFilter');
                
                selectLibraryFilter.innerHTML = '<option value="">-- All Libraries --</option>';

                // Filter to only TV Show and Mixed libraries
                const tvLibraries = response.Items.filter(library => {
                    return library.CollectionType === 'tvshows' || library.CollectionType === 'mixed' || !library.CollectionType;
                });

                tvLibraries.forEach(library => {
                    const option = document.createElement('option');
                    option.value = library.Id;
                    option.textContent = library.Name;
                    selectLibraryFilter.appendChild(option);
                });

                // Ensure default selection
                selectLibraryFilter.value = '';
            }).catch(error => {
                console.error('Error loading library filter:', error);
            });
        }

        loadLibraries(view, config) {
            ApiClient.getJSON(ApiClient.getUrl('Library/MediaFolders')).then(response => {
                const librariesContainer = view.querySelector('#creditsLibraries');

                librariesContainer.innerHTML = '';

                const libraryIds = config.LibraryIds || [];

                // Filter to only TV Show and Mixed libraries
                const tvLibraries = response.Items.filter(library => {
                    return library.CollectionType === 'tvshows' || library.CollectionType === 'mixed' || !library.CollectionType;
                });

                tvLibraries.forEach(library => {
                    const div = document.createElement('div');
                    div.className = 'checkboxContainer';
                    div.innerHTML = `
                        <label>
                            <input is="emby-checkbox" type="checkbox" class="chkLibrary" data-library-id="${library.Id}" ${libraryIds.includes(library.Id) ? 'checked' : ''} />
                            <span>${library.Name}</span>
                        </label>
                    `;
                    librariesContainer.appendChild(div);
                });
            }).catch(error => {
                console.error('Error loading libraries:', error);
            });
        }

        loadData(view) {
            loading.show();
            getPluginConfiguration().then(config => {
                this.config = config;

                view.querySelector('#chkEnableAutoDetection').checked = config.EnableAutoDetection || false;
                view.querySelector('#chkUseEpisodeComparison').checked = config.UseEpisodeComparison || false;
                view.querySelector('#chkEnableFailedEpisodeFallback').checked = config.EnableFailedEpisodeFallback || false;
                view.querySelector('#txtMinimumSuccessRateForFallback').value = config.MinimumSuccessRateForFallback || 0.5;
                view.querySelector('#chkEnableDetailedLogging').checked = config.EnableDetailedLogging || false;
                view.querySelector('#chkScheduledTaskOnlyProcessMissing').checked = config.ScheduledTaskOnlyProcessMissing !== false;

                view.querySelector('#txtTempFolderPath').value = config.TempFolderPath || '';

                view.querySelector('#txtOcrEndpoint').value = config.OcrEndpoint || 'http://localhost:8884';
                view.querySelector('#txtOcrDetectionKeywords').value = config.OcrDetectionKeywords || 'directed by,produced by,executive producer,written by,cast,credits,fin,ende,終,끝,fim,fine';
                view.querySelector('#txtOcrMinutesFromEnd').value = config.OcrMinutesFromEnd || 3;
                view.querySelector('#txtOcrDetectionSearchStart').value = config.OcrDetectionSearchStart || 0.65;
                view.querySelector('#txtOcrFrameRate').value = config.OcrFrameRate || 0.5;
                view.querySelector('#txtOcrMinimumMatches').value = config.OcrMinimumMatches || 1;
                view.querySelector('#txtOcrMaxFramesToProcess').value = config.OcrMaxFramesToProcess || 0;
                view.querySelector('#txtOcrMaxAnalysisDuration').value = config.OcrMaxAnalysisDuration || 600;
                view.querySelector('#txtOcrStopSecondsFromEnd').value = config.OcrStopSecondsFromEnd || 20;
                view.querySelector('#selectOcrImageFormat').value = config.OcrImageFormat || 'jpg';
                view.querySelector('#txtOcrJpegQuality').value = config.OcrJpegQuality || 92;

                // Reset Process TV Shows section to default state
                const selectLibraryFilter = view.querySelector('#selectLibraryFilter');
                const selectEpisode = view.querySelector('#selectEpisode');
                if (selectLibraryFilter) selectLibraryFilter.value = '';
                if (selectEpisode) selectEpisode.innerHTML = '<option value="">-- Select Show First --</option>';

                this.loadSeriesList(view);
                this.loadLibraryFilter(view);
                this.loadLibraries(view, config);

                ApiClient.getJSON(ApiClient.getUrl('CreditsDetector/GetProgress')).then(progress => {
                    console.log('Progress data loaded:', progress);
                    console.log('FailureReasons:', progress.FailureReasons);
                    console.log('SuccessDetails:', progress.SuccessDetails);

                    if (progress.IsRunning) {
                        view.querySelector('#progressContainer').style.display = 'block';
                        this.updateProgressUI(view, progress);
                        this.startProgressPolling(view);
                    } else {

                        console.log('Calling displayDetectionResults');
                        this.displayDetectionResults(view, progress);
                    }
                }).catch(error => {
                    console.error('Error checking progress:', error);
                });

                loading.hide();
            });
        }

        saveData(view) {
            loading.show();

            this.config.EnableAutoDetection = view.querySelector('#chkEnableAutoDetection').checked;
            this.config.UseEpisodeComparison = view.querySelector('#chkUseEpisodeComparison').checked;
            this.config.EnableFailedEpisodeFallback = view.querySelector('#chkEnableFailedEpisodeFallback').checked;
            this.config.MinimumSuccessRateForFallback = Number.parseFloat(view.querySelector('#txtMinimumSuccessRateForFallback').value) || 0.5;
            this.config.EnableDetailedLogging = view.querySelector('#chkEnableDetailedLogging').checked;
            this.config.ScheduledTaskOnlyProcessMissing = view.querySelector('#chkScheduledTaskOnlyProcessMissing').checked;

            this.config.TempFolderPath = view.querySelector('#txtTempFolderPath').value || '';

            this.config.EnableOcrDetection = true; // Always enabled
            this.config.OcrEndpoint = view.querySelector('#txtOcrEndpoint').value || 'http://localhost:8884';
            this.config.OcrDetectionKeywords = view.querySelector('#txtOcrDetectionKeywords').value || 'directed by,produced by,executive producer,written by,cast,credits,fin,ende,終,끝,fim,fine';
            this.config.OcrMinutesFromEnd = Number.parseFloat(view.querySelector('#txtOcrMinutesFromEnd').value) || 0;
            this.config.OcrDetectionSearchStart = Number.parseFloat(view.querySelector('#txtOcrDetectionSearchStart').value) || 0.65;
            this.config.OcrFrameRate = Number.parseFloat(view.querySelector('#txtOcrFrameRate').value) || 0.5;
            this.config.OcrMinimumMatches = Number.parseInt(view.querySelector('#txtOcrMinimumMatches').value, 10) || 2;
            this.config.OcrMaxFramesToProcess = Number.parseInt(view.querySelector('#txtOcrMaxFramesToProcess').value, 10) || 0;
            this.config.OcrMaxAnalysisDuration = Number.parseFloat(view.querySelector('#txtOcrMaxAnalysisDuration').value) || 600;
            this.config.OcrStopSecondsFromEnd = Number.parseFloat(view.querySelector('#txtOcrStopSecondsFromEnd').value) || 20;
            this.config.OcrImageFormat = view.querySelector('#selectOcrImageFormat').value || 'jpg';
            this.config.OcrJpegQuality = Number.parseInt(view.querySelector('#txtOcrJpegQuality').value, 10) || 92;

            const libraries = [];
            view.querySelectorAll('.chkLibrary').forEach(checkbox => {
                if (checkbox.checked) {
                    libraries.push(checkbox.getAttribute('data-library-id'));
                }
            });
            this.config.LibraryIds = libraries;

            updatePluginConfiguration(this.config).then(result => {
                loading.hide();
                Dashboard.processPluginConfigurationUpdateResult(result);
                toast('Configuration saved.');
            }).catch(() => {
                loading.hide();
                toast({ type: 'error', text: 'Error saving configuration.' });
            });
        }

        browseTempFolder(view) {
            require(['directorybrowser'], (directoryBrowser) => {
                const picker = new directoryBrowser();
                picker.show({
                    callback: (path) => {
                        if (path) {
                            view.querySelector('#txtTempFolderPath').value = path;
                        }
                        picker.close();
                    },
                    header: 'Select Temp Folder'
                });
            });
        }

        resetToDefaults(view) {
            if (!confirm('Reset all settings to default values?\n\nNote: Your OCR Endpoint and Temp Folder Path will be preserved.')) {
                return;
            }

            loading.show();

            // Preserve these values
            const preservedOcrEndpoint = view.querySelector('#txtOcrEndpoint').value;
            const preservedTempFolderPath = view.querySelector('#txtTempFolderPath').value;

            // Default values matching PluginConfiguration.cs
            const defaultKeywords = 'directed by,produced by,executive producer,written by,cast,credits,fin,ende,終,끝,fim,fine,producer,music by,cinematography,editor,editing,production design,costume design,casting,based on,story by,screenplay,associate producer,co-producer,created by,developed by,series producer,composer,director of photography,visual effects,sound,the end,end credits,starring,guest starring,special thanks,production company';

            // Set default values
            view.querySelector('#chkEnableAutoDetection').checked = false;
            view.querySelector('#chkUseEpisodeComparison').checked = false;
            view.querySelector('#chkEnableFailedEpisodeFallback').checked = false;
            view.querySelector('#txtMinimumSuccessRateForFallback').value = 0.5;
            view.querySelector('#chkEnableDetailedLogging').checked = false;
            view.querySelector('#chkScheduledTaskOnlyProcessMissing').checked = true;

            // Restore preserved values
            view.querySelector('#txtTempFolderPath').value = preservedTempFolderPath;

            view.querySelector('#txtOcrEndpoint').value = preservedOcrEndpoint; // Restore preserved value
            view.querySelector('#txtOcrDetectionKeywords').value = defaultKeywords;
            view.querySelector('#txtOcrMinutesFromEnd').value = 3;
            view.querySelector('#txtOcrDetectionSearchStart').value = 0.65;
            view.querySelector('#txtOcrFrameRate').value = 0.5;
            view.querySelector('#txtOcrMinimumMatches').value = 1;
            view.querySelector('#txtOcrMaxFramesToProcess').value = 0;
            view.querySelector('#txtOcrMaxAnalysisDuration').value = 600;
            view.querySelector('#txtOcrStopSecondsFromEnd').value = 20;
            view.querySelector('#selectOcrImageFormat').value = 'jpg';
            view.querySelector('#txtOcrJpegQuality').value = 92;

            // Uncheck all libraries (default behavior: all libraries enabled when none selected)
            view.querySelectorAll('.chkLibrary').forEach(checkbox => {
                checkbox.checked = false;
            });

            loading.hide();
            toast('Settings reset to defaults. Click Save to apply changes.');
        }

        onResume(options) {
            super.onResume(options);
            this.loadData(this.view);
            
            // Load donate image with authentication
            const donateImg = this.view.querySelector('#donateImage');
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
