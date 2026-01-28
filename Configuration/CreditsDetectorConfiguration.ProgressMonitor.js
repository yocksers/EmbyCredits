define(['loading', 'toast'], function (loading, toast) {
    'use strict';
    
    function startProgressPolling(instance, view, isDebugMode = false) {
        if (instance.progressInterval) clearInterval(instance.progressInterval);
        if (instance.progressHideTimeout) {
            clearTimeout(instance.progressHideTimeout);
            instance.progressHideTimeout = null;
        }
        
        const btnCancel = view.querySelector('#btnCancelProcessing');
        if (btnCancel) btnCancel.style.display = 'inline-block';
        
        instance.progressInterval = setInterval(() => {
            ApiClient.getJSON(ApiClient.getUrl('CreditsDetector/GetProgress')).then(progress => {
                if (!progress.IsRunning) {
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
                        ? `Processing cancelled. ${progress.SuccessfulItems} succeeded, ${progress.FailedItems} failed.`
                        : progress.CurrentItem === 'Dry Run Complete'
                        ? `Dry run complete! ${progress.SuccessfulItems} detected, ${progress.FailedItems} failed. No markers were saved.`
                        : `Processing complete! ${progress.SuccessfulItems} succeeded, ${progress.FailedItems} failed.`;
                    toast(message);
                    
                    if (isDebugMode) {
                        setTimeout(() => downloadDebugLog(), 1000);
                    }
                    return;
                }
                updateProgressUI(view, progress);
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
        
        updateResults(view, progress);
    }
    
    function updateResults(view, progress) {
        const failureDetails = view.querySelector('#failureDetails');
        const failureList = view.querySelector('#failureList');
        const successDetails = view.querySelector('#successDetails');
        const successList = view.querySelector('#successList');
        
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
            Object.entries(progress.SuccessDetails).forEach(([episode, timestamp]) => {
                const item = document.createElement('div');
                item.style.cssText = 'padding: 0.5em; margin-bottom: 0.5em; display: flex; align-items: center; gap: 1em;';
                
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
                }
                
                textContent.appendChild(episodeTitle);
                
                const detailsText = document.createElement('div');
                detailsText.innerHTML = `<span style="font-size: 0.9em; font-weight: bold;">Credits marker added at ${timestamp}</span><span>${confidenceText}</span>`;
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
    
    return {
        startProgressPolling: startProgressPolling,
        updateProgressUI: updateProgressUI
    };
});
