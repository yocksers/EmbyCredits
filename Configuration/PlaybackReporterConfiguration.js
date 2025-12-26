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

        startProgressPolling(view) {

            if (this.progressInterval) {
                clearInterval(this.progressInterval);
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

                        setTimeout(() => {
                            const progressContainer = view.querySelector('#progressContainer');
                            if (progressContainer) progressContainer.style.display = 'none';
                        }, 10000);

                        const message = progress.CurrentItem === 'Cancelled' 
                            ? `Processing cancelled. ${progress.SuccessfulItems} succeeded, ${progress.FailedItems} failed.`
                            : progress.CurrentItem === 'Dry Run Complete'
                            ? `Dry run complete! ${progress.SuccessfulItems} detected, ${progress.FailedItems} failed. No markers were saved.`
                            : `Processing complete! ${progress.SuccessfulItems} succeeded, ${progress.FailedItems} failed.`;
                        toast(message);
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

        loadSeriesList(view) {
            ApiClient.getJSON(ApiClient.getUrl('CreditsDetector/GetAllSeries')).then(response => {
                if (response.Success && response.Series) {
                    const selectSeries = view.querySelector('#selectSeries');
                    const selectSeriesForMarkers = view.querySelector('#selectSeriesForMarkers');

                    selectSeries.innerHTML = '<option value="">-- Select a TV Show --</option>';
                    selectSeriesForMarkers.innerHTML = '<option value="">-- Select a TV Show --</option>';

                    response.Series.forEach(series => {
                        const option1 = document.createElement('option');
                        option1.value = series.Id;
                        option1.textContent = series.Year ? `${series.Name} (${series.Year})` : series.Name;
                        selectSeries.appendChild(option1);

                        const option2 = document.createElement('option');
                        option2.value = series.Id;
                        option2.textContent = series.Year ? `${series.Name} (${series.Year})` : series.Name;
                        selectSeriesForMarkers.appendChild(option2);
                    });
                }
            }).catch(error => {
                console.error('Error loading series list:', error);
            });
        }

        loadLibraries(view, config) {
            ApiClient.getJSON(ApiClient.getUrl('Library/MediaFolders')).then(response => {
                const autoDetectionContainer = view.querySelector('#autoDetectionLibraries');
                const scheduledTaskContainer = view.querySelector('#scheduledTaskLibraries');

                autoDetectionContainer.innerHTML = '';
                scheduledTaskContainer.innerHTML = '';

                const autoDetectionIds = config.AutoDetectionLibraryIds || [];
                const scheduledTaskIds = config.ScheduledTaskLibraryIds || [];

                response.Items.forEach(library => {

                    const autoDiv = document.createElement('div');
                    autoDiv.className = 'checkboxContainer';
                    autoDiv.innerHTML = `
                        <label>
                            <input is="emby-checkbox" type="checkbox" class="chkAutoDetectionLibrary" data-library-id="${library.Id}" ${autoDetectionIds.includes(library.Id) ? 'checked' : ''} />
                            <span>${library.Name}</span>
                        </label>
                    `;
                    autoDetectionContainer.appendChild(autoDiv);

                    const schedDiv = document.createElement('div');
                    schedDiv.className = 'checkboxContainer';
                    schedDiv.innerHTML = `
                        <label>
                            <input is="emby-checkbox" type="checkbox" class="chkScheduledTaskLibrary" data-library-id="${library.Id}" ${scheduledTaskIds.includes(library.Id) ? 'checked' : ''} />
                            <span>${library.Name}</span>
                        </label>
                    `;
                    scheduledTaskContainer.appendChild(schedDiv);
                });
            }).catch(error => {
                console.error('Error loading libraries:', error);
            });
        }

        loadData(view) {
            loading.show();
            getPluginConfiguration().then(config => {
                this.config = config;

                view.querySelector('#chkEnableAutoDetection').checked = config.EnableAutoDetection !== false;
                view.querySelector('#chkUseEpisodeComparison').checked = config.UseEpisodeComparison !== false;
                view.querySelector('#chkEnableFailedEpisodeFallback').checked = config.EnableFailedEpisodeFallback || false;
                view.querySelector('#txtMinimumSuccessRateForFallback').value = config.MinimumSuccessRateForFallback || 0.5;
                view.querySelector('#chkEnableDetailedLogging').checked = config.EnableDetailedLogging || false;

                view.querySelector('#txtTempFolderPath').value = config.TempFolderPath || '';

                view.querySelector('#chkEnableOcrDetection').checked = config.EnableOcrDetection || false;
                view.querySelector('#txtOcrEndpoint').value = config.OcrEndpoint || 'http://localhost:8884';
                view.querySelector('#txtOcrDetectionKeywords').value = config.OcrDetectionKeywords || 'directed by,produced by,executive producer,written by,cast,credits,fin,ende,終,끝,fim,fine';
                view.querySelector('#txtOcrMinutesFromEnd').value = config.OcrMinutesFromEnd || 0;
                view.querySelector('#txtOcrDetectionSearchStart').value = config.OcrDetectionSearchStart || 0.65;
                view.querySelector('#txtOcrFrameRate').value = config.OcrFrameRate || 0.5;
                view.querySelector('#txtOcrMinimumMatches').value = config.OcrMinimumMatches || 2;
                view.querySelector('#txtOcrMaxFramesToProcess').value = config.OcrMaxFramesToProcess || 0;
                view.querySelector('#txtOcrMaxAnalysisDuration').value = config.OcrMaxAnalysisDuration || 600;
                view.querySelector('#txtOcrStopSecondsFromEnd').value = config.OcrStopSecondsFromEnd || 20;
                view.querySelector('#selectOcrImageFormat').value = config.OcrImageFormat || 'png';
                view.querySelector('#txtOcrJpegQuality').value = config.OcrJpegQuality || 92;

                this.loadSeriesList(view);
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
            this.config.MinimumSuccessRateForFallback = parseFloat(view.querySelector('#txtMinimumSuccessRateForFallback').value) || 0.5;
            this.config.EnableDetailedLogging = view.querySelector('#chkEnableDetailedLogging').checked;

            this.config.TempFolderPath = view.querySelector('#txtTempFolderPath').value || '';

            this.config.EnableOcrDetection = view.querySelector('#chkEnableOcrDetection').checked;
            this.config.OcrEndpoint = view.querySelector('#txtOcrEndpoint').value || 'http://localhost:8884';
            this.config.OcrDetectionKeywords = view.querySelector('#txtOcrDetectionKeywords').value || 'directed by,produced by,executive producer,written by,cast,credits,fin,ende,終,끝,fim,fine';
            this.config.OcrMinutesFromEnd = parseFloat(view.querySelector('#txtOcrMinutesFromEnd').value) || 0;
            this.config.OcrDetectionSearchStart = parseFloat(view.querySelector('#txtOcrDetectionSearchStart').value) || 0.65;
            this.config.OcrFrameRate = parseFloat(view.querySelector('#txtOcrFrameRate').value) || 0.5;
            this.config.OcrMinimumMatches = parseInt(view.querySelector('#txtOcrMinimumMatches').value) || 2;
            this.config.OcrMaxFramesToProcess = parseInt(view.querySelector('#txtOcrMaxFramesToProcess').value) || 0;
            this.config.OcrMaxAnalysisDuration = parseFloat(view.querySelector('#txtOcrMaxAnalysisDuration').value) || 600;
            this.config.OcrStopSecondsFromEnd = parseFloat(view.querySelector('#txtOcrStopSecondsFromEnd').value) || 20;
            this.config.OcrImageFormat = view.querySelector('#selectOcrImageFormat').value || 'png';
            this.config.OcrJpegQuality = parseInt(view.querySelector('#txtOcrJpegQuality').value) || 92;

            const autoDetectionLibraries = [];
            view.querySelectorAll('.chkAutoDetectionLibrary').forEach(checkbox => {
                if (checkbox.checked) {
                    autoDetectionLibraries.push(checkbox.getAttribute('data-library-id'));
                }
            });
            this.config.AutoDetectionLibraryIds = autoDetectionLibraries;

            const scheduledTaskLibraries = [];
            view.querySelectorAll('.chkScheduledTaskLibrary').forEach(checkbox => {
                if (checkbox.checked) {
                    scheduledTaskLibraries.push(checkbox.getAttribute('data-library-id'));
                }
            });
            this.config.ScheduledTaskLibraryIds = scheduledTaskLibraries;

            updatePluginConfiguration(this.config).then(result => {
                loading.hide();
                Dashboard.processPluginConfigurationUpdateResult(result);
                toast('Configuration saved.');
            }).catch(() => {
                loading.hide();
                toast({ type: 'error', text: 'Error saving configuration.' });
            });
        }

        onResume(options) {
            super.onResume(options);
            this.loadData(this.view);
        }

        onPause() {
            super.onPause();
            if (this.progressInterval) {
                clearInterval(this.progressInterval);
                this.progressInterval = null;
            }
        }
    };
});
