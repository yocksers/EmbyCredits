define(['loading', 'toast'], function (loading, toast) {
    'use strict';
    
    function startProgressPolling(instance, view, isDebugMode = false, isDryRun = false) {
        if (instance.progressInterval) clearInterval(instance.progressInterval);
        if (instance.progressHideTimeout) {
            clearTimeout(instance.progressHideTimeout);
            instance.progressHideTimeout = null;
        }
        
        // Clear stored progress when new detection starts
        instance.lastProgress = null;
        
        // Store dry run state for use in updateResults
        instance.isDryRun = isDryRun;
        
        // Flag to prevent multiple completion handlers
        instance.hasCompleted = false;
        
        // Clear previous results from UI
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
                // Store progress in instance for persistence
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
                    
                    // Use the stored dry run flag (it was set when polling started)
                    // Update results one final time with the correct dry run state
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
    
    function playVideoAtTimestamp(episodeId, timestampSeconds) {
        // Use Emby's playback manager to start playing the episode at the specified timestamp
        require(['playbackManager'], function(playbackManager) {
            ApiClient.getItem(ApiClient.getCurrentUserId(), episodeId).then(function(item) {
                playbackManager.play({
                    items: [item],
                    startPositionTicks: timestampSeconds * 10000000 // Convert seconds to ticks (1 tick = 100ns)
                });
            }).catch(function(error) {
                console.error('Error starting playback:', error);
                require(['toast'], function(toast) {
                    toast({ type: 'error', text: 'Failed to start playback' });
                });
            });
        });
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
                item.innerHTML = `<strong>${episode}</strong><br/><span style="font-size: 0.9em; opacity: 0.8;">${reason}</span>`;
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
                item.innerHTML = `<strong>${episode}</strong><br/><span style="font-size: 0.9em;">${reason}</span>`;
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
                        playVideoAtTimestamp(episodeId, timestampSeconds);
                    });
                    episodeTitle.appendChild(playButton);
                    
                    // Add "Add Timestamp" button if this is a dry run
                    if (isDryRun) {

                        const addButton = document.createElement('button');
                        addButton.className = 'button-flat';
                        addButton.style.cssText = 'padding: 0.2em 0.5em; font-size: 0.8em; min-width: auto; background-color: #1e88e5; color: white; margin-left: 0.5em;';
                        addButton.innerHTML = '<i class="md-icon">add</i> Add Timestamp';
                        addButton.title = 'Save this timestamp as a chapter marker';
                        addButton.addEventListener('click', function() {
                            const episodeId = progress.EpisodeIds[episode];
                            const timestampSeconds = parseTimestamp(timestamp);
                            addTimestampFromDryRun(episodeId, timestampSeconds, episode, addButton);
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
    
    function addTimestampFromDryRun(episodeId, timestampSeconds, episodeName, buttonElement) {
        loading.show();
        
        ApiClient.ajax({
            type: 'POST',
            url: ApiClient.getUrl('CreditsDetector/AddTimestampFromDryRun'),
            contentType: 'application/json',
            dataType: 'json',
            data: JSON.stringify({
                EpisodeId: episodeId,
                TimestampSeconds: timestampSeconds
            })
        }).then(response => {
            loading.hide();
            if (response && response.Success) {
                toast({ type: 'success', text: response.Message || 'Timestamp added successfully!' });
                
                // Update button to show it's been added
                buttonElement.style.backgroundColor = '#4caf50';
                buttonElement.innerHTML = '<i class="md-icon">check</i> Added';
                buttonElement.disabled = true;
            } else {
                toast({ type: 'error', text: (response && response.Message) || 'Failed to add timestamp' });
            }
        }).catch(error => {
            loading.hide();
            console.error('Error adding timestamp:', error);
            toast({ type: 'error', text: 'Failed to add timestamp. Check server logs.' });
        });
    }
    
    function showEditTimestampDialog(episodeId, currentTimestampSeconds, episodeName, buttonElement) {
        const hours = Math.floor(currentTimestampSeconds / 3600);
        const minutes = Math.floor((currentTimestampSeconds % 3600) / 60);
        const seconds = Math.floor(currentTimestampSeconds % 60);
        
        let currentTimeStr;
        if (hours > 0) {
            currentTimeStr = `${hours}:${minutes.toString().padStart(2, '0')}:${seconds.toString().padStart(2, '0')}`;
        } else {
            currentTimeStr = `${minutes}:${seconds.toString().padStart(2, '0')}`;
        }
        
        const promptMessage = `Edit Timestamp - ${episodeName}\n\nEnter the new timestamp (format: MM:SS or HH:MM:SS):`;
        const newTimeStr = prompt(promptMessage, currentTimeStr);
        
        if (newTimeStr && newTimeStr !== currentTimeStr) {
            const newTimestampSeconds = parseTimestamp(newTimeStr);
            if (newTimestampSeconds > 0) {
                require(['loading', 'toast'], (loading, toast) => {
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
                    .then(response => response.json())
                    .then(result => {
                        loading.hide();
                        if (result.Success) {
                            toast({ type: 'success', text: result.Message || 'Timestamp updated successfully!' });
                            
                            buttonElement.style.backgroundColor = '#4caf50';
                            buttonElement.innerHTML = '<i class="md-icon">check</i> Saved';
                            setTimeout(() => {
                                buttonElement.style.backgroundColor = '#ff9800';
                                buttonElement.innerHTML = '<i class="md-icon">edit</i> Edit Timestamp';
                            }, 2000);
                        } else {
                            toast({ type: 'error', text: result.Message || 'Failed to update timestamp' });
                        }
                    })
                    .catch(error => {
                        loading.hide();
                        console.error('Error updating timestamp:', error);
                        toast({ type: 'error', text: 'Failed to update timestamp. Check server logs.' });
                    });
                });
            } else {
                require(['toast'], (toast) => {
                    toast({ type: 'error', text: 'Invalid timestamp format. Use MM:SS or HH:MM:SS' });
                });
            }
        }
    }
    
    return {
        startProgressPolling: startProgressPolling,
        updateProgressUI: updateProgressUI,
        updateResults: updateResults
    };
});
