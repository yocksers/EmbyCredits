define(['loading', 'toast'], function (loading, toast) {
    'use strict';

    function processSeries(instance, view) {
        const libraryId = view.querySelector('#selectLibraryFilter').value;
        const seriesId = view.querySelector('#selectSeries').value;
        const episodeId = view.querySelector('#selectEpisode').value;
        const skipExistingMarkers = view.querySelector('#chkManualSkipExistingMarkers').checked;

        // Priority: Episode > Season > Series > Library
        if (episodeId) {
            // Check if it's a season selection (format: "season:N")
            if (episodeId.startsWith('season:')) {
                const seasonNumber = parseInt(episodeId.split(':')[1]);
                loading.show();
                ApiClient.ajax({
                    type: 'POST',
                    url: ApiClient.getUrl('CreditsDetector/ProcessSeason'),
                    contentType: 'application/json',
                    data: JSON.stringify({ SeriesId: seriesId, SeasonNumber: seasonNumber, SkipExistingMarkers: skipExistingMarkers })
                }).then(response => {
                    loading.hide();
                    toast(response.Message || `Season ${seasonNumber} queued for processing.`);
                    view.querySelector('#progressContainer').style.display = 'block';
                    require(['configurationpage?name=CreditsDetectorConfigurationProgressMonitor'], (progressMonitor) => {
                        progressMonitor.startProgressPolling(instance, view);
                    });
                }).catch(error => {
                    loading.hide();
                    console.error('Error processing season:', error);
                    toast({ type: 'error', text: 'Failed to process season. Check server logs.' });
                });
                return;
            }
            
            // Process single episode
            loading.show();
            ApiClient.ajax({
                type: 'POST',
                url: ApiClient.getUrl('CreditsDetector/ProcessEpisode'),
                contentType: 'application/json',
                data: JSON.stringify({ ItemId: episodeId, SkipExistingMarkers: skipExistingMarkers })
            }).then(response => {
                loading.hide();
                toast(response.Message || 'Episode queued for processing.');
                view.querySelector('#progressContainer').style.display = 'block';
                require(['configurationpage?name=CreditsDetectorConfigurationProgressMonitor'], (progressMonitor) => {
                    progressMonitor.startProgressPolling(instance, view);
                });
            }).catch(error => {
                loading.hide();
                console.error('Error processing episode:', error);
                toast({ type: 'error', text: 'Failed to process episode. Check server logs.' });
            });
            return;
        }

        if (seriesId) {
            loading.show();
            ApiClient.ajax({
                type: 'POST',
                url: ApiClient.getUrl('CreditsDetector/ProcessSeries'),
                contentType: 'application/json',
                data: JSON.stringify({ SeriesId: seriesId, SkipExistingMarkers: skipExistingMarkers })
            }).then(response => {
                loading.hide();
                toast(`Episodes queued. They will be processed one at a time.`);
                view.querySelector('#progressContainer').style.display = 'block';
                require(['configurationpage?name=CreditsDetectorConfigurationProgressMonitor'], (progressMonitor) => {
                    progressMonitor.startProgressPolling(instance, view);
                });
            }).catch(error => {
                loading.hide();
                console.error('Error processing series:', error);
                toast({ type: 'error', text: 'Failed to start processing. Check server logs.' });
            });
            return;
        }

        if (libraryId) {
            loading.show();
            ApiClient.ajax({
                type: 'POST',
                url: ApiClient.getUrl('CreditsDetector/ProcessLibrary'),
                contentType: 'application/json',
                data: JSON.stringify({ LibraryId: libraryId, SkipExistingMarkers: skipExistingMarkers })
            }).then(response => {
                loading.hide();
                const message = response.Message || `Queued ${response.EpisodeCount || 0} episodes from ${response.SeriesCount || 0} TV shows for processing.`;
                toast(message);
                view.querySelector('#progressContainer').style.display = 'block';
                require(['configurationpage?name=CreditsDetectorConfigurationProgressMonitor'], (progressMonitor) => {
                    progressMonitor.startProgressPolling(instance, view);
                });
            }).catch(error => {
                loading.hide();
                console.error('Error processing library:', error);
                toast({ type: 'error', text: 'Failed to start processing. Check server logs.' });
            });
            return;
        }

        toast({ type: 'error', text: 'Please select a library, TV show, or episode first.' });
    }

    function queueAllSeries(instance, view) {
        loading.show();
        ApiClient.ajax({
            type: 'POST',
            url: ApiClient.getUrl('CreditsDetector/TriggerDetection'),
            contentType: 'application/json'
        }).then(response => {
            loading.hide();
            toast(response.Message || 'All episodes queued for processing.');
            view.querySelector('#progressContainer').style.display = 'block';
            require(['configurationpage?name=CreditsDetectorConfigurationProgressMonitor'], (progressMonitor) => {
                progressMonitor.startProgressPolling(instance, view);
            });
        }).catch(error => {
            loading.hide();
            console.error('Error queuing all series:', error);
            toast({ type: 'error', text: 'Failed to queue all series. Check server logs.' });
        });
    }

    function cancelProcessing(view) {
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
    }

    function clearQueue(view) {
        if (confirm('Are you sure you want to clear the processing queue? This will reset all processing state.')) {
            loading.show();
            ApiClient.ajax({
                type: 'POST',
                url: ApiClient.getUrl('CreditsDetector/ClearQueue')
            }).then(response => {
                loading.hide();
                view.querySelector('#progressContainer').style.display = 'none';
                const message = response.Message || 'Queue cleared and processing state reset.';
                toast(message);
            }).catch(error => {
                loading.hide();
                console.error('Error clearing queue:', error);
                toast({ type: 'error', text: 'Failed to clear queue.' });
            });
        }
    }

    function startDryRun(instance, view, isDebug = false) {
        const libraryId = view.querySelector('#selectLibraryFilter').value;
        const seriesId = view.querySelector('#selectSeries').value;
        const episodeId = view.querySelector('#selectEpisode').value;
        const skipExistingMarkers = view.querySelector('#chkManualSkipExistingMarkers').checked;

        const endpoint = isDebug ? 'CreditsDetector/DryRunSeriesDebug' : 'CreditsDetector/DryRunSeries';
        
        // Priority: Episode > Season > Series > Library
        let dataPayload = null;
        if (episodeId) {
            // Check if it's a season selection (format: "season:N")
            if (episodeId.startsWith('season:')) {
                const seasonNumber = parseInt(episodeId.split(':')[1]);
                dataPayload = { SeriesId: seriesId, SeasonNumber: seasonNumber, SkipExistingMarkers: skipExistingMarkers };
            } else {
                dataPayload = { EpisodeId: episodeId, SkipExistingMarkers: skipExistingMarkers };
            }
        } else if (seriesId) {
            dataPayload = { SeriesId: seriesId, SkipExistingMarkers: skipExistingMarkers };
        } else if (libraryId) {
            dataPayload = { LibraryId: libraryId, SkipExistingMarkers: skipExistingMarkers };
        }

        if (!dataPayload) {
            toast({ type: 'error', text: 'Please select a library, TV show, or episode first.' });
            return;
        }

        loading.show();
        ApiClient.ajax({
            type: 'POST',
            url: ApiClient.getUrl(endpoint),
            contentType: 'application/json',
            data: JSON.stringify(dataPayload)
        }).then(response => {
            loading.hide();
            const message = isDebug 
                ? (response.Message || 'Debug dry run started. Detailed logs will be captured.')
                : (response.Message || 'Dry run started. No markers will be saved.');
            toast(message);
            view.querySelector('#progressContainer').style.display = 'block';
            require(['configurationpage?name=CreditsDetectorConfigurationProgressMonitor'], (progressMonitor) => {
                progressMonitor.startProgressPolling(instance, view, isDebug);
            });
        }).catch(error => {
            loading.hide();
            console.error(`Error starting ${isDebug ? 'debug ' : ''}dry run:`, error);
            toast({ type: 'error', text: `Failed to start ${isDebug ? 'debug ' : ''}dry run. Check server logs.` });
        });
    }

    function testOcrConnection(view) {
        const ocrEndpoint = view.querySelector('#txtOcrEndpoint').value;
        if (!ocrEndpoint) {
            toast({ type: 'error', text: 'Please enter an OCR endpoint URL first.' });
            return;
        }

        const successIndicator = view.querySelector('#ocrTestSuccess');
        if (successIndicator) {
            successIndicator.style.display = 'none';
        }

        loading.show();
        ApiClient.fetch({
            type: 'POST',
            url: ApiClient.getUrl('CreditsDetector/TestOcrConnection'),
            dataType: 'json',
            contentType: 'application/json',
            data: JSON.stringify({ OcrEndpoint: ocrEndpoint })
        }).then(response => {
            loading.hide();
            if (response && response.Success) {
                toast({ type: 'success', text: response.Message || 'OCR connection successful!' });
                if (successIndicator) {
                    successIndicator.style.display = 'inline';
                    setTimeout(() => {
                        successIndicator.style.display = 'none';
                    }, 5000);
                }
            } else {
                if (successIndicator) {
                    successIndicator.style.display = 'none';
                }
                toast({ type: 'error', text: response.Message || 'OCR connection failed' });
            }
        }).catch(error => {
            loading.hide();
            if (successIndicator) {
                successIndicator.style.display = 'none';
            }
            console.error('Error testing OCR connection:', error);
            toast({ type: 'error', text: 'Failed to test OCR connection. Check endpoint URL.' });
        });
    }

    return {
        processSeries: processSeries,
        queueAllSeries: queueAllSeries,
        cancelProcessing: cancelProcessing,
        clearQueue: clearQueue,
        startDryRun: startDryRun,
        testOcrConnection: testOcrConnection
    };
});
