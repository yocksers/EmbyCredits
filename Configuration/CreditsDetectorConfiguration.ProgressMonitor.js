define(['loading', 'toast'], function (loading, toast) {
    'use strict';
    
    function startProgressPolling(instance, view, isDebugMode = false, isDryRun = false) {
        if (instance.progressInterval) clearInterval(instance.progressInterval);
        if (instance.progressHideTimeout) {
            clearTimeout(instance.progressHideTimeout);
            instance.progressHideTimeout = null;
        }
        
        instance.lastProgress = null;
        
        instance.isDryRun = isDryRun;
        
        instance.hasCompleted = false;
        
        const skipDetails = view.querySelector('#skipDetails');
        const skipList = view.querySelector('#skipList');
        const failureDetails = view.querySelector('#failureDetails');
        const failureList = view.querySelector('#failureList');
        const successDetails = view.querySelector('#successDetails');
        const successList = view.querySelector('#successList');
        
        if (skipDetails) skipDetails.style.display = 'none';
        if (skipList) skipList.innerHTML = '';
        if (failureDetails) failureDetails.style.display = 'none';
        if (failureList) failureList.innerHTML = '';
        if (successDetails) successDetails.style.display = 'none';
        if (successList) successList.innerHTML = '';
        
        const btnCancel = view.querySelector('#btnCancelProcessing');
        if (btnCancel) btnCancel.style.display = 'inline-block';
        
        instance.progressInterval = setInterval(() => {
            ApiClient.getJSON(ApiClient.getUrl('CreditsDetector/GetProgress')).then(progress => {
                instance.lastProgress = progress;
                
                if (!progress.IsRunning && !instance.hasCompleted) {
                    instance.hasCompleted = true;
                    clearInterval(instance.progressInterval);
                    instance.progressInterval = null;
                    if (btnCancel) btnCancel.style.display = 'none';
                    updateProgressUI(view, progress);
                    
                    instance.progressHideTimeout = setTimeout(() => {
                        const container = view.querySelector('#progressContainer');
                        if (container) container.style.display = 'none';
                        instance.progressHideTimeout = null;
                    }, 10000);
                    
                    const message = progress.CurrentItem === 'Cancelled' 
                        ? `Processing cancelled. ${progress.SuccessfulItems} succeeded, ${progress.FailedItems} failed${progress.SkippedItems > 0 ? `, ${progress.SkippedItems} skipped` : ''}.`
                        : progress.CurrentItem === 'Dry Run Complete'
                        ? `Dry run complete! ${progress.SuccessfulItems} detected, ${progress.FailedItems} failed${progress.SkippedItems > 0 ? `, ${progress.SkippedItems} skipped` : ''}. No markers were saved.`
                        : `Processing complete! ${progress.SuccessfulItems} succeeded, ${progress.FailedItems} failed${progress.SkippedItems > 0 ? `, ${progress.SkippedItems} skipped` : ''}.`;
                    toast(message);
                    
                    if (isDebugMode) {
                        setTimeout(() => downloadDebugLog(), 1000);
                    }
                    
                    updateResults(view, progress, instance.isDryRun);
                    return;
                }
                updateProgressUI(view, progress);
                updateResults(view, progress, instance.isDryRun);
            }).catch(error => {
                console.error('Error fetching progress:', error);
                clearInterval(instance.progressInterval);
                instance.progressInterval = null;
            });
        }, 500);
    }
    
    function parseTimestamp(timestamp) {
        const parts = timestamp.split(':').map(p => parseInt(p, 10));
        if (parts.length === 2) {
            return parts[0] * 60 + parts[1];
        } else if (parts.length === 3) {
            return parts[0] * 3600 + parts[1] * 60 + parts[2];
        }
        return 0;
    }
    
    function updateProgressUI(view, progress) {
        const progressBar = view.querySelector('#progressBar');
        const percentText = view.querySelector('#percentText');
        const itemProgressBar = view.querySelector('#itemProgressBar');
        const currentItem = view.querySelector('#currentItem');
        const progressCount = view.querySelector('#progressCount');
        const successCount = view.querySelector('#successCount');
        const failedCount = view.querySelector('#failedCount');
        const skippedCount = view.querySelector('#skippedCount');
        const etaText = view.querySelector('#etaText');
        
        if (!progressBar || !percentText) return;
        
        const percent = progress.PercentComplete || 0;
        progressBar.style.width = percent + '%';
        percentText.textContent = percent.toFixed(0) + '%';
        
        if (itemProgressBar) itemProgressBar.style.width = (progress.CurrentItemProgress || 0) + '%';
        if (currentItem) currentItem.textContent = progress.CurrentItem || 'Starting...';
        if (progressCount) progressCount.textContent = `${progress.ProcessedItems}/${progress.TotalItems}`;
        if (successCount) successCount.textContent = progress.SuccessfulItems || 0;
        if (failedCount) failedCount.textContent = progress.FailedItems || 0;
        if (skippedCount) skippedCount.textContent = progress.SkippedItems || 0;
        
        if (etaText) {
            if (progress.IsRunning && progress.ProcessedItems > 0 && progress.EstimatedTimeRemainingSeconds != null && progress.EstimatedTimeRemainingSeconds > 0) {
                const totalSeconds = Math.floor(progress.EstimatedTimeRemainingSeconds);
                const hours = Math.floor(totalSeconds / 3600);
                const minutes = Math.floor((totalSeconds % 3600) / 60);
                const seconds = totalSeconds % 60;
                
                if (hours > 0) {
                    etaText.textContent = `${hours}h ${minutes}m`;
                } else if (minutes > 0) {
                    etaText.textContent = `${minutes}m ${seconds}s`;
                } else {
                    etaText.textContent = `${seconds}s`;
                }
            } else if (progress.IsRunning && progress.ProcessedItems === 0) {
                etaText.textContent = 'Calculating...';
            } else {
                etaText.textContent = '-';
            }
        }

        const ffmpegSessionsContainer = view.querySelector('#ffmpegSessionsContainer');
        const ffmpegSessionsList = view.querySelector('#ffmpegSessionsList');
        if (ffmpegSessionsContainer && ffmpegSessionsList) {
            const processes = progress.ActiveFfmpegProcesses;
            if (processes && processes.length > 0) {
                ffmpegSessionsContainer.style.display = 'block';
                ffmpegSessionsList.innerHTML = '';
                processes.forEach(proc => {
                    const age = proc.AgeSeconds;
                    const pct = proc.PercentOfTimeout;
                    const minutes = Math.floor(age / 60);
                    const seconds = age % 60;
                    const ageLabel = minutes > 0 ? `${minutes}m ${seconds}s` : `${seconds}s`;
                    const barColor = pct >= 80 ? '#E53935' : pct >= 50 ? '#FFA726' : '#52B54B';

                    const stalled = proc.SecondsSinceLastOutput != null && proc.SecondsSinceLastOutput >= 30;
                    const stalledBadge = stalled
                        ? `<span style="margin-left:0.4em; font-size:0.78em; font-weight:bold; color:#E53935; background:rgba(229,57,53,0.12); border-radius:3px; padding:0 0.3em;">STALLED ${proc.SecondsSinceLastOutput}s</span>`
                        : '';

                    const row = document.createElement('div');
                    row.style.cssText = 'margin-bottom: 0.4em;';
                    row.innerHTML =
                        `<div style="display:flex; justify-content:space-between; font-size:0.82em; margin-bottom:0.15em;">` +
                        `<span style="opacity:0.85; overflow:hidden; text-overflow:ellipsis; white-space:nowrap; max-width:70%;">${proc.Description}${stalledBadge}</span>` +
                        `<span style="opacity:0.7; white-space:nowrap; margin-left:0.5em;">${ageLabel}</span>` +
                        `</div>` +
                        `<div style="background:rgba(128,128,128,0.2); border-radius:3px; height:5px; overflow:hidden;">` +
                        `<div style="background:${barColor}; height:100%; width:${pct}%; transition:width 0.5s ease;"></div>` +
                        `</div>`;
                    ffmpegSessionsList.appendChild(row);
                });
            } else {
                ffmpegSessionsContainer.style.display = 'none';
            }
        }
    }
    
    function updateResults(view, progress, isDryRun = false) {
        const skipDetails = view.querySelector('#skipDetails');
        const skipList = view.querySelector('#skipList');
        const failureDetails = view.querySelector('#failureDetails');
        const failureList = view.querySelector('#failureList');
        const successDetails = view.querySelector('#successDetails');
        const successList = view.querySelector('#successList');
        
        if (progress.SkipReasons && Object.keys(progress.SkipReasons).length > 0) {
            skipDetails.style.display = 'block';
            skipList.innerHTML = '';
            Object.entries(progress.SkipReasons).forEach(([episode, reason]) => {
                const item = document.createElement('div');
                item.style.cssText = 'padding: 0.5em; margin-bottom: 0.5em;';
                const appliedRule = progress.AppliedRules && progress.AppliedRules[episode];
                const ruleHtml = appliedRule ? `<br/><span style="font-size: 0.8em; opacity: 0.65;">Rule: ${appliedRule}</span>` : '';
                item.innerHTML = `<strong>${episode}</strong><br/><span style="font-size: 0.9em; opacity: 0.8;">${reason}</span>${ruleHtml}`;
                skipList.appendChild(item);
            });
        } else {
            skipDetails.style.display = 'none';
        }
        
        if (progress.FailureReasons && Object.keys(progress.FailureReasons).length > 0) {
            failureDetails.style.display = 'block';
            failureList.innerHTML = '';
            Object.entries(progress.FailureReasons).forEach(([episode, reason]) => {
                const item = document.createElement('div');
                item.style.cssText = 'padding: 0.5em; margin-bottom: 0.5em;';
                const appliedRule = progress.AppliedRules && progress.AppliedRules[episode];
                const ruleHtml = appliedRule ? `<br/><span style="font-size: 0.8em; opacity: 0.65;">Rule: ${appliedRule}</span>` : '';
                item.innerHTML = `<strong>${episode}</strong><br/><span style="font-size: 0.9em;">${reason}</span>${ruleHtml}`;
                failureList.appendChild(item);
            });
        } else {
            failureDetails.style.display = 'none';
        }
        
        if (progress.SuccessDetails && Object.keys(progress.SuccessDetails).length > 0) {
            successDetails.style.display = 'block';
            successList.innerHTML = '';
            Object.entries(progress.SuccessDetails).forEach(([episode, successDetail]) => {
                const item = document.createElement('div');
                item.style.cssText = 'padding: 0.5em; margin-bottom: 0.5em; display: flex; align-items: center; gap: 1em;';
                
                // Extract just the timestamp from the beginning of the success detail string
                // Format is "HH:MM:SS [Method] - details..." or "MM:SS [Method] - details..."
                const timestampMatch = successDetail.match(/^(\d{1,2}:\d{2}(?::\d{2})?)/);
                const timestamp = timestampMatch ? timestampMatch[1] : '';
                
                const confidence = progress.ConfidenceScores && progress.ConfidenceScores[episode];
                const confidenceText = confidence !== undefined && confidence !== null
                    ? ` (confidence: ${(confidence * 100).toFixed(0)}%)`
                    : '';
                
                const textContent = document.createElement('div');
                textContent.style.cssText = 'flex: 1;';
                
                const episodeTitle = document.createElement('div');
                episodeTitle.style.cssText = 'display: flex; align-items: center; gap: 0.5em; margin-bottom: 0.3em;';
                
                const titleText = document.createElement('strong');
                titleText.textContent = episode;
                episodeTitle.appendChild(titleText);
                
                if (progress.EpisodeIds && progress.EpisodeIds[episode]) {
                    const playButton = document.createElement('button');
                    playButton.className = 'button-flat';
                    playButton.style.cssText = 'padding: 0.2em 0.5em; font-size: 0.8em; min-width: auto; background-color: #52B54B; color: white;';
                    playButton.innerHTML = '<i class="md-icon">play_arrow</i> Play at timestamp';
                    playButton.title = 'Play episode at detected credits timestamp';
                    playButton.addEventListener('click', function() {
                        const episodeId = progress.EpisodeIds[episode];
                        const timestampSeconds = parseTimestamp(timestamp);
                        const episodeName = episode;
                        require(['configurationpage?name=CreditsDetectorConfigurationVideoPlayer'], function(videoPlayer) {
                            videoPlayer.openVideoDialog(episodeId, timestampSeconds, {
                                title: 'Preview \u2014 ' + episodeName,
                                onTimestampSelected: function(chosenSeconds) {
                                    if (isDryRun) {
                                        loading.show();
                                        ApiClient.ajax({
                                            type: 'POST',
                                            url: ApiClient.getUrl('CreditsDetector/AddTimestampFromDryRun'),
                                            contentType: 'application/json',
                                            dataType: 'json',
                                            data: JSON.stringify({ EpisodeId: episodeId, TimestampSeconds: chosenSeconds })
                                        }).then(function(response) {
                                            loading.hide();
                                            if (response && response.Success) {
                                                toast({ type: 'success', text: response.Message || 'Timestamp added successfully!' });
                                            } else {
                                                toast({ type: 'error', text: (response && response.Message) || 'Failed to add timestamp' });
                                            }
                                        }).catch(function() {
                                            loading.hide();
                                            toast({ type: 'error', text: 'Failed to add timestamp. Check server logs.' });
                                        });
                                    } else {
                                        loading.show();
                                        fetch(ApiClient.getUrl('CreditsDetector/UpdateCreditsMarker'), {
                                            method: 'POST',
                                            headers: {
                                                'X-Emby-Token': ApiClient.accessToken(),
                                                'Content-Type': 'application/json'
                                            },
                                            body: JSON.stringify({ EpisodeId: episodeId, CreditsStartSeconds: chosenSeconds, IsRelativeFromEnd: false })
                                        }).then(function(r) { return r.json(); }).then(function(res) {
                                            loading.hide();
                                            if (res.Success) {
                                                toast({ type: 'success', text: res.Message || 'Timestamp updated successfully!' });
                                            } else {
                                                toast({ type: 'error', text: res.Message || 'Failed to update timestamp' });
                                            }
                                        }).catch(function() {
                                            loading.hide();
                                            toast({ type: 'error', text: 'Failed to update timestamp. Check server logs.' });
                                        });
                                    }
                                }
                            });
                        });
                    });
                    episodeTitle.appendChild(playButton);

                    if (isDryRun) {
                        const addButton = document.createElement('button');
                        addButton.className = 'button-flat';
                        addButton.style.cssText = 'padding: 0.2em 0.5em; font-size: 0.8em; min-width: auto; background-color: #1e88e5; color: white; margin-left: 0.5em;';
                        addButton.innerHTML = '<i class="md-icon">add</i> Add Timestamp';
                        addButton.title = 'Save this timestamp as a chapter marker';
                        addButton.addEventListener('click', function() {
                            const episodeId = progress.EpisodeIds[episode];
                            const timestampSeconds = parseTimestamp(timestamp);
                            loading.show();
                            ApiClient.ajax({
                                type: 'POST',
                                url: ApiClient.getUrl('CreditsDetector/AddTimestampFromDryRun'),
                                contentType: 'application/json',
                                dataType: 'json',
                                data: JSON.stringify({ EpisodeId: episodeId, TimestampSeconds: timestampSeconds })
                            }).then(function(response) {
                                loading.hide();
                                if (response && response.Success) {
                                    toast({ type: 'success', text: response.Message || 'Timestamp added successfully!' });
                                    addButton.style.backgroundColor = '#4caf50';
                                    addButton.innerHTML = '<i class="md-icon">check</i> Added';
                                    addButton.disabled = true;
                                } else {
                                    toast({ type: 'error', text: (response && response.Message) || 'Failed to add timestamp' });
                                }
                            }).catch(function() {
                                loading.hide();
                                toast({ type: 'error', text: 'Failed to add timestamp. Check server logs.' });
                            });
                        });
                        episodeTitle.appendChild(addButton);
                    } else {
                        const editButton = document.createElement('button');
                        editButton.className = 'button-flat';
                        editButton.style.cssText = 'padding: 0.2em 0.5em; font-size: 0.8em; min-width: auto; background-color: #ff9800; color: white; margin-left: 0.5em;';
                        editButton.innerHTML = '<i class="md-icon">edit</i> Edit Timestamp';
                        editButton.title = 'Fine-tune this timestamp';
                        editButton.addEventListener('click', function() {
                            const episodeId = progress.EpisodeIds[episode];
                            const timestampSeconds = parseTimestamp(timestamp);
                            showEditTimestampDialog(episodeId, timestampSeconds, episode, editButton);
                        });
                        episodeTitle.appendChild(editButton);
                    }
                }
                
                textContent.appendChild(episodeTitle);
                
                const detailsText = document.createElement('div');
                detailsText.innerHTML = `<span style="font-size: 0.9em; font-weight: bold;">${successDetail}</span><span>${confidenceText}</span>`;
                textContent.appendChild(detailsText);
                
                const appliedRule = progress.AppliedRules && progress.AppliedRules[episode];
                if (appliedRule) {
                    const ruleText = document.createElement('div');
                    ruleText.innerHTML = `<span style="font-size: 0.8em; opacity: 0.65;">Rule: ${appliedRule}</span>`;
                    textContent.appendChild(ruleText);
                }
                
                item.appendChild(textContent);
                
                if (progress.ThumbnailPaths && progress.ThumbnailPaths[episode]) {
                    const thumbnailId = progress.ThumbnailPaths[episode];
                    const thumbnailUrl = ApiClient.getUrl('CreditsDetector/Thumbnail/' + encodeURIComponent(thumbnailId), {
                        api_key: ApiClient.accessToken()
                    });
                    
                    const thumbnailContainer = document.createElement('div');
                    thumbnailContainer.style.cssText = 'flex-shrink: 0;';
                    
                    const thumbnail = document.createElement('img');
                    thumbnail.src = thumbnailUrl;
                    thumbnail.style.cssText = 'max-width: 160px; max-height: 90px; border-radius: 4px; box-shadow: 0 2px 8px rgba(0,0,0,0.3); cursor: pointer;';
                    thumbnail.title = `Credits detected at ${timestamp} - Click to enlarge`;
                    thumbnail.alt = 'Credit detection thumbnail';
                    
                    thumbnail.addEventListener('click', function() {
                        const modal = document.createElement('div');
                        modal.style.cssText = 'position: fixed; top: 0; left: 0; right: 0; bottom: 0; background: rgba(0,0,0,0.9); z-index: 10000; display: flex; align-items: center; justify-content: center; cursor: pointer;';
                        
                        const largeImage = document.createElement('img');
                        largeImage.src = thumbnailUrl;
                        largeImage.style.cssText = 'max-width: 90%; max-height: 90%; border-radius: 4px;';
                        
                        modal.appendChild(largeImage);
                        modal.addEventListener('click', function() {
                            document.body.removeChild(modal);
                        });
                        
                        document.body.appendChild(modal);
                    });
                    
                    thumbnailContainer.appendChild(thumbnail);
                    item.appendChild(thumbnailContainer);
                }
                
                successList.appendChild(item);
            });
        } else {
            successDetails.style.display = 'none';
        }
    }
    
    function downloadDebugLog() {
        loading.show();
        fetch(ApiClient.getUrl('CreditsDetector/GetDebugLog'), {
            method: 'GET',
            headers: { 'X-Emby-Token': ApiClient.accessToken() }
        })
        .then(response => {
            if (!response.ok) throw new Error('Failed to download debug log');
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
    
    function showEditTimestampDialog(episodeId, currentTimestampSeconds, episodeName, buttonElement) {
        var hours = Math.floor(currentTimestampSeconds / 3600);
        var minutes = Math.floor((currentTimestampSeconds % 3600) / 60);
        var seconds = Math.floor(currentTimestampSeconds % 60);
        var currentTimeStr = hours > 0
            ? hours + ':' + String(minutes).padStart(2, '0') + ':' + String(seconds).padStart(2, '0')
            : minutes + ':' + String(seconds).padStart(2, '0');

        var newTimeStr = prompt('Edit Timestamp - ' + episodeName + '\n\nEnter the new timestamp (format: MM:SS or HH:MM:SS):', currentTimeStr);

        if (newTimeStr && newTimeStr !== currentTimeStr) {
            var newTimestampSeconds = parseTimestamp(newTimeStr);
            if (newTimestampSeconds > 0) {
                loading.show();
                fetch(ApiClient.getUrl('CreditsDetector/UpdateCreditsMarker'), {
                    method: 'POST',
                    headers: {
                        'X-Emby-Token': ApiClient.accessToken(),
                        'Content-Type': 'application/json'
                    },
                    body: JSON.stringify({
                        EpisodeId: episodeId,
                        CreditsStartSeconds: newTimestampSeconds,
                        IsRelativeFromEnd: false
                    })
                })
                .then(function(r) { return r.json(); })
                .then(function(res) {
                    loading.hide();
                    if (res.Success) {
                        toast({ type: 'success', text: res.Message || 'Timestamp updated successfully!' });
                        buttonElement.style.backgroundColor = '#4caf50';
                        buttonElement.innerHTML = '<i class="md-icon">check</i> Saved';
                        setTimeout(function() {
                            buttonElement.style.backgroundColor = '#ff9800';
                            buttonElement.innerHTML = '<i class="md-icon">edit</i> Edit Timestamp';
                        }, 2000);
                    } else {
                        toast({ type: 'error', text: res.Message || 'Failed to update timestamp' });
                    }
                })
                .catch(function() {
                    loading.hide();
                    toast({ type: 'error', text: 'Failed to update timestamp. Check server logs.' });
                });
            } else {
                toast({ type: 'error', text: 'Invalid timestamp format. Use MM:SS or HH:MM:SS' });
            }
        }
    }
    
    return {
        startProgressPolling: startProgressPolling,
        updateProgressUI: updateProgressUI,
        updateResults: updateResults
    };
});
