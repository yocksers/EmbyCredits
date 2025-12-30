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
                item.style.cssText = 'padding: 0.5em; margin-bottom: 0.5em; border-left: 3px solid #E5A54A; background: rgba(255,255,255,0.03);';
                item.innerHTML = `<strong style="color: #e0e0e0;">${episode}</strong><br/><span style="color: #E5A54A; font-size: 0.9em;">${reason}</span>`;
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
                item.style.cssText = 'padding: 0.5em; margin-bottom: 0.5em; border-left: 3px solid #52b54b; background: rgba(255,255,255,0.03);';
                item.innerHTML = `<strong style="color: #e0e0e0;">${episode}</strong><br/><span style="color: #52b54b; font-size: 0.9em; font-weight: bold;">Credits marker added at ${timestamp}</span>`;
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
