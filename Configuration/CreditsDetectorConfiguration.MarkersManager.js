define(['loading', 'toast'], function (loading, toast) {
    'use strict';
    
    const seasonEpisodesCache = new Map();
    
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
        require(['playbackManager'], function(playbackManager) {
            ApiClient.getItem(ApiClient.getCurrentUserId(), episodeId).then(function(item) {
                playbackManager.play({
                    items: [item],
                    startPositionTicks: timestampSeconds * 10000000
                });
            }).catch(function(error) {
                console.error('Error starting playback:', error);
                require(['toast'], function(toast) {
                    toast({ type: 'error', text: 'Failed to start playback' });
                });
            });
        });
    }
    
    function displayMarkers(instance, view) {
        const seriesSelect = view.querySelector('#selectSeriesForMarkers');
        const markersDisplay = view.querySelector('#markersDisplay');
        const markersContent = view.querySelector('#markersContent');
        const markersSeriesName = view.querySelector('#markersSeriesName');
        
        if (!seriesSelect || !markersDisplay || !markersContent) {
            toast({ type: 'error', text: 'Marker display elements not found' });
            return;
        }
        
        const seriesId = seriesSelect.value;
        
        if (!seriesId) {
            toast({ type: 'error', text: 'Please select a series' });
            return;
        }
        
        loading.show();
        ApiClient.getJSON(ApiClient.getUrl('CreditsDetector/GetSeriesMarkers', { SeriesId: seriesId }))
            .then(response => {
                loading.hide();
                
                if (!response.Success) {
                    toast({ type: 'error', text: response.Message || 'Failed to load markers' });
                    return;
                }
                
                markersDisplay.style.display = 'block';
                
                const seriesNameDiv = document.createElement('div');
                seriesNameDiv.style.cssText = 'margin-bottom: 1em;';
                
                const titleHtml = `<h3 style="display: inline-block; margin: 0; margin-right: 1em;">${response.SeriesName || 'Series Markers'}</h3>`;
                const buttonsHtml = `
                    <button is="emby-button" type="button" class="raised btnExportSeries" data-series-id="${seriesId}" style="padding: 0.4em 0.8em; font-size: 0.85em; margin-right: 0.5em; vertical-align: middle;">⬇ Export</button>
                    <button is="emby-button" type="button" class="raised button-submit btnImportSeries" data-series-id="${seriesId}" style="padding: 0.4em 0.8em; font-size: 0.85em; vertical-align: middle;">⬆ Import</button>
                `;
                
                seriesNameDiv.innerHTML = titleHtml + buttonsHtml;
                markersSeriesName.innerHTML = '';
                markersSeriesName.appendChild(seriesNameDiv);
                
                markersContent.innerHTML = '';
                
                if (!response.Episodes || response.Episodes.length === 0) {
                    markersContent.innerHTML = '<div style="padding: 1em; opacity: 0.6;">No episodes found for this series.</div>';
                    return;
                }

                const episodesBySeason = {};
                const episodesWithMarkers = response.Episodes.filter(ep => ep.HasCreditsMarker).length;
                seasonEpisodesCache.clear();
                response.Episodes.forEach(ep => {
                    const season = ep.Season || 0;
                    if (season === 0) return;
                    
                    if (!episodesBySeason[season]) {
                        episodesBySeason[season] = [];
                    }
                    episodesBySeason[season].push(ep);
                });
                
                Object.keys(episodesBySeason).forEach(season => {
                    seasonEpisodesCache.set(season, episodesBySeason[season]);
                });
                

                Object.keys(episodesBySeason).sort((a, b) => Number(a) - Number(b)).forEach(season => {
                    const seasonDiv = document.createElement('div');
                    seasonDiv.style.cssText = 'margin-bottom: 1.5em;';
                    
                    const episodesInSeason = episodesBySeason[season];
                    const missingMarkersCount = episodesInSeason.filter(ep => !ep.HasCreditsMarker).length;
                    
                    const seasonHeader = document.createElement('div');
                    seasonHeader.style.cssText = 'margin-bottom: 0.5em;';
                    
                    let headerHtml = `<h4 style="display: inline-block; margin: 0; margin-right: 1em;">Season ${season}</h4>`;
                    
                    if (missingMarkersCount > 0) {
                        headerHtml += `<button is="emby-button" type="button" class="raised button-submit btnDetectSeasonMissing" data-series-id="${seriesId}" data-season-number="${season}" style="padding: 0.4em 0.8em; font-size: 0.85em; font-weight: normal; vertical-align: middle; margin-right: 0.5em;">▶ Detect Missing (${missingMarkersCount})</button>`;
                        headerHtml += `<button is="emby-button" type="button" class="raised btnBatchSetTime" data-series-id="${seriesId}" data-season-number="${season}" style="padding: 0.4em 0.8em; font-size: 0.85em; font-weight: normal; vertical-align: middle;">✏ Set Time for Missing (${missingMarkersCount})</button>`;
                    }
                    
                    seasonHeader.innerHTML = headerHtml;
                    seasonDiv.appendChild(seasonHeader);
                    
                    episodesBySeason[season].forEach(episode => {
                        const episodeDiv = document.createElement('div');
                        const hasMarkers = episode.HasCreditsMarker;
                        episodeDiv.style.cssText = `padding: 1em; margin-bottom: 0.5em; border: 1px solid rgba(128,128,128,0.3); border-radius: 4px; background: rgba(128,128,128,0.08);`;
                        
                        let markersHtml = '';
                        if (hasMarkers && episode.Markers && episode.Markers.length > 0) {
                            episode.Markers.forEach(marker => {
                                markersHtml += `<div style="margin-top: 0.5em; opacity: 0.9;">
                                    <strong>${marker.MarkerType || 'Credits'}</strong>: ${marker.StartTime}
                                    <button is="emby-button" type="button" class="raised btnPlayAtMarker" data-episode-id="${episode.EpisodeId}" data-timestamp="${marker.StartTime}" style="margin-left: 1em; padding: 0.25em 0.75em; font-size: 0.9em; background-color: #52B54B; color: white;"><i class="md-icon" style="font-size: 1em;">play_arrow</i> Play</button>
                                    <button is="emby-button" type="button" class="raised btnEditMarker" data-episode-id="${episode.EpisodeId}" data-current-time="${marker.StartTime}" data-duration-seconds="${episode.DurationSeconds || 0}" style="margin-left: 0.5em; padding: 0.25em 0.75em; font-size: 0.9em; background-color: #4A9FE5; color: white;">Edit</button>
                                    <button is="emby-button" type="button" class="raised button-submit btnApplyToSeason" data-episode-id="${episode.EpisodeId}" data-series-id="${seriesId}" data-season-number="${episode.Season}" data-episode-name="${episode.EpisodeName || 'Unknown'}" style="margin-left: 0.5em; padding: 0.25em 0.75em; font-size: 0.9em;">Apply to Season</button>
                                </div>`;
                            });
                        } else {
                            markersHtml = `<div style="margin-top: 0.5em; opacity: 0.7; font-style: italic;">No credits marker <button is="emby-button" type="button" class="raised button-submit btnEditMarker" data-episode-id="${episode.EpisodeId}" data-current-time="" data-duration-seconds="${episode.DurationSeconds || 0}" style="margin-left: 1em; padding: 0.25em 0.75em; font-size: 0.9em; background-color: #4A9FE5; color: white;">Add Marker</button></div>`;
                        }
                        
                        episodeDiv.innerHTML = `
                            <div><strong>${episode.SeasonEpisode}</strong> - ${episode.EpisodeName || 'Unknown'} <span style="opacity: 0.7; font-size: 0.9em;">(${episode.Duration})</span></div>
                            ${markersHtml}
                        `;
                        
                        seasonDiv.appendChild(episodeDiv);
                    });
                    
                    markersContent.appendChild(seasonDiv);
                });
                
                markersContent.querySelectorAll('.btnPlayAtMarker').forEach(btn => {
                    btn.addEventListener('click', function() {
                        const episodeId = this.getAttribute('data-episode-id');
                        const timestamp = this.getAttribute('data-timestamp');
                        const timestampSeconds = parseTimestamp(timestamp);
                        playVideoAtTimestamp(episodeId, timestampSeconds);
                    });
                });
                
                markersContent.querySelectorAll('.btnEditMarker').forEach(btn => {
                    btn.addEventListener('click', function() {
                        const episodeId = this.getAttribute('data-episode-id');
                        const currentTime = this.getAttribute('data-current-time');
                        const durationSeconds = parseFloat(this.getAttribute('data-duration-seconds')) || 0;
                        editMarker(instance, view, episodeId, currentTime, seriesId, durationSeconds);
                    });
                });
                
                markersContent.querySelectorAll('.btnApplyToSeason').forEach(btn => {
                    btn.addEventListener('click', function() {
                        const episodeId = this.getAttribute('data-episode-id');
                        const seriesId = this.getAttribute('data-series-id');
                        const seasonNumber = parseInt(this.getAttribute('data-season-number'));
                        const episodeName = this.getAttribute('data-episode-name');
                        applyToSeason(instance, view, episodeId, seriesId, seasonNumber, episodeName);
                    });
                });
                
                markersContent.querySelectorAll('.btnDetectSeasonMissing').forEach(btn => {
                    btn.addEventListener('click', function() {
                        const seriesId = this.getAttribute('data-series-id');
                        const seasonNumber = parseInt(this.getAttribute('data-season-number'));
                        detectSeasonMissingMarkers(instance, view, seriesId, seasonNumber);
                    });
                });
                
                markersContent.querySelectorAll('.btnBatchSetTime').forEach(btn => {
                    btn.addEventListener('click', function() {
                        const seriesId = this.getAttribute('data-series-id');
                        const seasonNumber = this.getAttribute('data-season-number');
                        const seasonEpisodes = seasonEpisodesCache.get(seasonNumber) || [];
                        batchSetTimeForMissing(instance, view, seriesId, parseInt(seasonNumber), seasonEpisodes);
                    });
                });
                
                document.querySelectorAll('.btnExportSeries').forEach(btn => {
                    btn.addEventListener('click', function() {
                        const seriesId = this.getAttribute('data-series-id');
                        exportSeriesCredits(seriesId, response.SeriesName);
                    });
                });
                
                document.querySelectorAll('.btnImportSeries').forEach(btn => {
                    btn.addEventListener('click', function() {
                        const seriesId = this.getAttribute('data-series-id');
                        importSeriesCredits(instance, view, seriesId);
                    });
                });
                
                toast({ type: 'success', text: `Showing ${response.Episodes.length} episode(s) (${episodesWithMarkers} with credits markers)` });
            })
            .catch(error => {
                loading.hide();
                console.error('Error fetching markers:', error);
                toast({ type: 'error', text: 'Failed to load markers: ' + error.message });
            });
    }
    
    function formatTime(ticks) {
        if (!ticks) return '00:00:00';
        const seconds = Math.floor(ticks / 10000000);
        const h = Math.floor(seconds / 3600);
        const m = Math.floor((seconds % 3600) / 60);
        const s = seconds % 60;
        return `${String(h).padStart(2, '0')}:${String(m).padStart(2, '0')}:${String(s).padStart(2, '0')}`;
    }
    
    function formatTimeFromSeconds(totalSeconds) {
        if (!totalSeconds || totalSeconds <= 0) return '00:00:00';
        const seconds = Math.floor(totalSeconds);
        const h = Math.floor(seconds / 3600);
        const m = Math.floor((seconds % 3600) / 60);
        const s = seconds % 60;
        return `${String(h).padStart(2, '0')}:${String(m).padStart(2, '0')}:${String(s).padStart(2, '0')}`;
    }
    
    function parseTimeToSeconds(timeStr) {
        const isRelative = timeStr.startsWith('-');
        const cleanTime = isRelative ? timeStr.substring(1) : timeStr;
        
        const parts = cleanTime.split(':').map(p => parseInt(p, 10));
        let seconds = 0;
        if (parts.length === 3) {
            seconds = parts[0] * 3600 + parts[1] * 60 + parts[2];
        } else if (parts.length === 2) {
            seconds = parts[0] * 60 + parts[1];
        }
        
        return isRelative ? -seconds : seconds;
    }
    
    function editMarker(instance, view, episodeId, currentTime, seriesId, durationSeconds, successCallback) {
        const currentSeconds = currentTime ? parseTimeToSeconds(currentTime) : 0;
        const maxTime = durationSeconds > 0 ? formatTimeFromSeconds(durationSeconds) : 'Unknown';
        const promptMessage = currentTime ? 
            `Edit credits start time for this episode.\nCurrent: ${currentTime}\nMax Duration: ${maxTime}\n\nEnter new time:\n• Absolute time: HH:MM:SS or MM:SS\n• Relative from end: -HH:MM:SS or -MM:SS (e.g., -00:31 for 31 seconds from end)` :
            `Add credits start time for this episode.\nMax Duration: ${maxTime}\n\nEnter time:\n• Absolute time: HH:MM:SS or MM:SS\n• Relative from end: -HH:MM:SS or -MM:SS (e.g., -00:31 for 31 seconds from end)`;
        
        const newTime = prompt(promptMessage, currentTime || '');
        
        if (newTime === null) return;
        
        if (!newTime || !/^-?\d{1,2}:\d{2}(:\d{2})?$/.test(newTime)) {
            require(['toast'], (toast) => {
                toast({ type: 'error', text: 'Invalid time format. Use HH:MM:SS, MM:SS, or -MM:SS for relative from end' });
            });
            return;
        }
        
        const newSeconds = parseTimeToSeconds(newTime);
        const isRelative = newTime.startsWith('-');
        const absSeconds = Math.abs(newSeconds);
        
        if (!isRelative && newSeconds < 0) {
            require(['toast'], (toast) => {
                toast({ type: 'error', text: 'Time must be positive' });
            });
            return;
        }
        
        if (!isRelative && durationSeconds > 0 && newSeconds > durationSeconds) {
            require(['toast'], (toast) => {
                toast({ type: 'error', text: `Time cannot exceed video duration (${maxTime})` });
            });
            return;
        }
        
        if (isRelative && durationSeconds > 0 && absSeconds > durationSeconds) {
            require(['toast'], (toast) => {
                toast({ type: 'error', text: `Offset from end cannot exceed video duration (${maxTime})` });
            });
            return;
        }
        
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
                    CreditsStartSeconds: absSeconds,
                    IsRelativeFromEnd: isRelative
                })
            })
            .then(response => response.json())
            .then(result => {
                loading.hide();
                if (result.Success) {
                    toast({ type: 'success', text: result.Message });
                    if (successCallback) {
                        successCallback();
                    } else {
                        displayMarkers(instance, view);
                    }
                } else {
                    toast({ type: 'error', text: result.Message || 'Failed to update marker' });
                }
            })
            .catch(error => {
                loading.hide();
                console.error('Error updating marker:', error);
                toast({ type: 'error', text: 'Failed to update marker: ' + error.message });
            });
        });
    }
    
    function applyToSeason(instance, view, episodeId, seriesId, seasonNumber, episodeName, successCallback) {
        const confirmMsg = `Copy the credits timestamp from "${episodeName}" to all other episodes in Season ${seasonNumber} that don't have markers?\n\nThis will only apply to episodes without existing credits markers.`;
        
        if (!confirm(confirmMsg)) {
            return;
        }
        
        require(['loading', 'toast'], (loading, toast) => {
            loading.show();
            
            fetch(ApiClient.getUrl('CreditsDetector/ApplyToSeason'), {
                method: 'POST',
                headers: {
                    'X-Emby-Token': ApiClient.accessToken(),
                    'Content-Type': 'application/json'
                },
                body: JSON.stringify({
                    EpisodeId: episodeId,
                    SeriesId: seriesId,
                    SeasonNumber: seasonNumber
                })
            })
            .then(response => response.json())
            .then(result => {
                loading.hide();
                if (result.Success) {
                    toast({ type: 'success', text: result.Message });
                    setTimeout(() => {
                        if (successCallback) {
                            successCallback();
                        } else {
                            displayMarkers(instance, view);
                        }
                    }, 1000);
                } else {
                    toast({ type: 'error', text: result.Message || 'Failed to apply marker to season' });
                }
            })
            .catch(error => {
                loading.hide();
                console.error('Error applying marker to season:', error);
                toast({ type: 'error', text: 'Failed to apply marker to season: ' + error.message });
            });
        });
    }
    
    function detectSeasonMissingMarkers(instance, view, seriesId, seasonNumber) {
        const confirmMsg = `This will queue all episodes in Season ${seasonNumber} that are missing credits markers for detection.\n\nDo you want to continue?`;
        
        if (!confirm(confirmMsg)) {
            return;
        }
        
        require(['loading', 'toast'], (loading, toast) => {
            loading.show();
            
            fetch(ApiClient.getUrl('CreditsDetector/ProcessSeasonMissingMarkers'), {
                method: 'POST',
                headers: {
                    'X-Emby-Token': ApiClient.accessToken(),
                    'Content-Type': 'application/json'
                },
                body: JSON.stringify({
                    SeriesId: seriesId,
                    SeasonNumber: seasonNumber
                })
            })
            .then(response => response.json())
            .then(result => {
                loading.hide();
                if (result.Success) {
                    toast({ type: 'success', text: result.Message });
                    
                    if (result.EpisodeCount > 0) {
                        const progressContainer = view.querySelector('#progressContainer');
                        if (progressContainer) {
                            progressContainer.style.display = 'block';
                        }
                        
                        require(['configurationpage?name=CreditsDetectorConfigurationProgressMonitor'], (progressMonitor) => {
                            progressMonitor.startProgressPolling(instance, view);
                        });
                    }
                } else {
                    toast({ type: 'error', text: result.Message || 'Failed to queue episodes for detection' });
                }
            })
            .catch(error => {
                loading.hide();
                console.error('Error queueing season for detection:', error);
                toast({ type: 'error', text: 'Failed to queue episodes for detection: ' + error.message });
            });
        });
    }
    
    function batchSetTimeForMissing(instance, view, seriesId, seasonNumber, seasonEpisodes) {
        const episodesWithoutMarkers = seasonEpisodes.filter(ep => !ep.HasCreditsMarker);
        const episodeDurations = episodesWithoutMarkers
            .map(ep => ep.DurationSeconds || 0)
            .filter(d => d > 0);
        
        const minDuration = episodeDurations.length > 0 ? Math.min(...episodeDurations) : 0;
        const maxTimeFormatted = minDuration > 0 ? formatTimeFromSeconds(minDuration) : 'Unknown';
        
        const promptMessage = `Set the same credits start time for ALL episodes missing markers in Season ${seasonNumber}.\nMax Duration (shortest episode): ${maxTimeFormatted}\n\nEnter time:\n• Absolute time: HH:MM:SS or MM:SS\n• Relative from end: -HH:MM:SS or -MM:SS (e.g., -00:31 for 31 seconds from end)`;
        
        const timeInput = prompt(promptMessage, '');
        
        if (timeInput === null) return;
        
        if (!timeInput || !/^-?\d{1,2}:\d{2}(:\d{2})?$/.test(timeInput)) {
            require(['toast'], (toast) => {
                toast({ type: 'error', text: 'Invalid time format. Use HH:MM:SS, MM:SS, or -MM:SS for relative from end' });
            });
            return;
        }
        
        const seconds = parseTimeToSeconds(timeInput);
        const isRelative = timeInput.startsWith('-');
        const absSeconds = Math.abs(seconds);
        
        if (!isRelative && seconds < 0) {
            require(['toast'], (toast) => {
                toast({ type: 'error', text: 'Time must be positive' });
            });
            return;
        }
        
        if (!isRelative && minDuration > 0 && seconds > minDuration) {
            require(['toast'], (toast) => {
                toast({ type: 'error', text: `Time cannot exceed shortest episode duration (${maxTimeFormatted})` });
            });
            return;
        }
        
        if (isRelative && minDuration > 0 && absSeconds > minDuration) {
            require(['toast'], (toast) => {
                toast({ type: 'error', text: `Offset from end cannot exceed shortest episode duration (${maxTimeFormatted})` });
            });
            return;
        }
        
        require(['loading', 'toast'], (loading, toast) => {
            loading.show();
            
            fetch(ApiClient.getUrl('CreditsDetector/BatchUpdateSeasonMissingMarkers'), {
                method: 'POST',
                headers: {
                    'X-Emby-Token': ApiClient.accessToken(),
                    'Content-Type': 'application/json'
                },
                body: JSON.stringify({
                    SeriesId: seriesId,
                    SeasonNumber: seasonNumber,
                    CreditsStartSeconds: absSeconds,
                    IsRelativeFromEnd: isRelative
                })
            })
            .then(response => response.json())
            .then(result => {
                loading.hide();
                if (result.Success) {
                    toast({ type: 'success', text: result.Message });
                    setTimeout(() => {
                        displayMarkers(instance, view);
                    }, 1000);
                } else {
                    toast({ type: 'error', text: result.Message || 'Failed to set credits markers' });
                }
            })
            .catch(error => {
                loading.hide();
                console.error('Error setting batch markers:', error);
                toast({ type: 'error', text: 'Failed to set credits markers: ' + error.message });
            });
        });
    }
    
    function exportSeriesCredits(seriesId, seriesName) {
        require(['loading', 'toast'], (loading, toast) => {
            loading.show();
            
            const fileName = `${seriesName.replace(/[^a-z0-9]/gi, '_')}_credits.json`;
            const url = ApiClient.getUrl('CreditsDetector/ExportSeriesCredits', { SeriesId: seriesId });
            
            fetch(url, {
                method: 'GET',
                headers: {
                    'X-Emby-Token': ApiClient.accessToken()
                }
            })
            .then(response => {
                if (!response.ok) {
                    throw new Error('Export failed');
                }
                return response.blob();
            })
            .then(blob => {
                loading.hide();
                
                const downloadUrl = window.URL.createObjectURL(blob);
                const a = document.createElement('a');
                a.href = downloadUrl;
                a.download = fileName;
                document.body.appendChild(a);
                a.click();
                document.body.removeChild(a);
                window.URL.revokeObjectURL(downloadUrl);
                
                toast({ type: 'success', text: `Exported credits for ${seriesName}` });
            })
            .catch(error => {
                loading.hide();
                console.error('Error exporting series credits:', error);
                toast({ type: 'error', text: 'Failed to export credits: ' + error.message });
            });
        });
    }
    
    function importSeriesCredits(instance, view, seriesId) {
        const input = document.createElement('input');
        input.type = 'file';
        input.accept = '.json';
        
        input.addEventListener('change', function(e) {
            const file = e.target.files[0];
            if (!file) return;
            
            const reader = new FileReader();
            reader.onload = function(event) {
                const jsonData = event.target.result;
                
                require(['loading', 'toast'], (loading, toast) => {
                    const overwrite = confirm('Overwrite existing credits markers?\n\nYes = Replace existing markers\nNo = Skip episodes that already have markers');
                    
                    loading.show();
                    
                    fetch(ApiClient.getUrl('CreditsDetector/ImportSeriesCredits'), {
                        method: 'POST',
                        headers: {
                            'X-Emby-Token': ApiClient.accessToken(),
                            'Content-Type': 'application/json'
                        },
                        body: JSON.stringify({
                            SeriesId: seriesId,
                            JsonData: jsonData,
                            OverwriteExisting: overwrite
                        })
                    })
                    .then(response => response.json())
                    .then(result => {
                        loading.hide();
                        if (result.Success) {
                            const message = `Import complete!\nImported: ${result.ItemsImported}\nSkipped: ${result.ItemsSkipped}\nNot Found: ${result.ItemsNotFound}`;
                            toast({ type: 'success', text: message });
                            
                            setTimeout(() => {
                                displayMarkers(instance, view);
                            }, 1000);
                        } else {
                            toast({ type: 'error', text: result.Message || 'Import failed' });
                        }
                    })
                    .catch(error => {
                        loading.hide();
                        console.error('Error importing series credits:', error);
                        toast({ type: 'error', text: 'Failed to import credits: ' + error.message });
                    });
                });
            };
            
            reader.readAsText(file);
        });
        
        input.click();
    }
    
    function showSeasonValidation(instance, view) {
        const seriesSelect = view.querySelector('#selectSeriesForMarkers');
        if (!seriesSelect || !seriesSelect.value) {
            require(['toast'], (toast) => {
                toast({ type: 'error', text: 'Please select a series first' });
            });
            return;
        }

        const seriesId = seriesSelect.value;
        const seasonNumber = prompt('Enter season number for batch validation:');
        
        if (!seasonNumber || isNaN(seasonNumber) || parseInt(seasonNumber) < 1) {
            return;
        }

        require(['loading', 'toast'], (loading, toast) => {
            loading.show();
            
            ApiClient.getJSON(ApiClient.getUrl('CreditsDetector/GetSeasonValidation', {
                SeriesId: seriesId,
                SeasonNumber: parseInt(seasonNumber)
            }))
            .then(response => {
                loading.hide();
                
                if (!response.Success) {
                    toast({ type: 'error', text: response.Message || 'Failed to load validation data' });
                    return;
                }

                showValidationModal(instance, view, response);
            })
            .catch(error => {
                loading.hide();
                console.error('Error loading validation data:', error);
                toast({ type: 'error', text: 'Failed to load validation data: ' + error.message });
            });
        });
    }

    function refreshValidationModal(instance, view, modal, content, seriesId, seasonNumber) {
        loading.show();
        ApiClient.getJSON(ApiClient.getUrl('CreditsDetector/ValidateSeasonCredits', {
            SeriesId: seriesId,
            SeasonNumber: seasonNumber
        }))
        .then(response => {
            loading.hide();
            if (response.Success) {
                updateValidationModalContent(instance, view, modal, content, response);
            } else {
                toast({ type: 'error', text: response.Message || 'Failed to reload validation data' });
            }
        })
        .catch(error => {
            loading.hide();
            console.error('Error reloading validation data:', error);
            toast({ type: 'error', text: 'Failed to reload validation data: ' + error.message });
        });
    }

    function updateValidationModalContent(instance, view, modal, content, data) {
        const episodesWithMarkers = data.Episodes.filter(ep => ep.Marker && ep.Marker.HasMarker && ep.Marker.StartTime && ep.DurationSeconds > 0);
        let averageFromEndText = '';
        
        if (episodesWithMarkers.length > 0) {
            const timeFromEndValues = episodesWithMarkers.map(ep => {
                const timeparts = ep.Marker.StartTime.split(':').map(p => parseInt(p, 10));
                let markerSeconds = 0;
                if (timeparts.length === 3) {
                    markerSeconds = timeparts[0] * 3600 + timeparts[1] * 60 + timeparts[2];
                } else if (timeparts.length === 2) {
                    markerSeconds = timeparts[0] * 60 + timeparts[1];
                }
                return ep.DurationSeconds - markerSeconds;
            });
            
            const avgFromEnd = timeFromEndValues.reduce((a, b) => a + b, 0) / timeFromEndValues.length;
            const avgMinutes = Math.floor(avgFromEnd / 60);
            const avgSeconds = Math.floor(avgFromEnd % 60);
            averageFromEndText = ` | Average from end: ${avgMinutes}:${avgSeconds.toString().padStart(2, '0')}`;
        }
        
        let html = `<h2 style="margin-top: 0; color: #52B54B;">${data.SeriesName} - Season ${data.SeasonNumber} Validation</h2>`;
        html += `<p style="color: #b8b8b8; margin-bottom: 1.5em;">Side-by-side comparison of all episodes for quick validation${averageFromEndText}</p>`;
        html += `<div style="display: grid; grid-template-columns: repeat(auto-fill, minmax(300px, 1fr)); gap: 1em;">`;
        
        data.Episodes.forEach(ep => {
            const hasMarker = ep.Marker && ep.Marker.HasMarker;
            const statusIcon = hasMarker ? '✓' : '✗';
            const statusColor = hasMarker ? '#52B54B' : '#E53935';
            const borderColor = hasMarker ? 'rgba(82, 181, 75, 0.5)' : 'rgba(229, 57, 53, 0.3)';
            
            html += `<div style="background: #242424; border: 2px solid ${borderColor}; border-radius: 6px; padding: 1em;">`;
            html += `<div style="display: flex; justify-content: space-between; align-items: center; margin-bottom: 0.5em;">`;
            html += `<strong style="color: #e8e8e8;">Episode ${ep.EpisodeNumber}</strong>`;
            html += `<span style="font-size: 1.5em; color: ${statusColor};">${statusIcon}</span>`;
            html += `</div>`;
            html += `<div style="font-size: 0.9em; color: #d0d0d0; margin-bottom: 0.5em;">${ep.EpisodeName}</div>`;
            html += `<div style="font-size: 0.85em; color: #a0a0a0; margin-bottom: 0.5em;">Duration: ${ep.Duration}</div>`;
            
            if (hasMarker) {
                html += `<div style="font-weight: bold; color: #52B54B; margin-bottom: 0.5em;">Credits: ${ep.Marker.StartTime}</div>`;
                html += `<div style="display: flex; flex-direction: row; gap: 0.5em; flex-wrap: wrap;">`;
                html += `<button is="emby-button" type="button" class="raised btnValidationPlay" data-episode-id="${ep.EpisodeId}" data-timestamp="${ep.Marker.StartTime}" style="padding: 0.3em 0.6em; font-size: 0.85em; background-color: #52B54B; color: white; flex: 1; min-width: 80px;"><i class="md-icon" style="font-size: 1em;">play_arrow</i> Play</button>`;
                html += `<button is="emby-button" type="button" class="raised btnValidationEdit" data-episode-id="${ep.EpisodeId}" data-current-time="${ep.Marker.StartTime}" data-duration-seconds="${ep.DurationSeconds || 0}" data-series-id="${data.SeriesId}" style="padding: 0.3em 0.6em; font-size: 0.85em; background-color: #4A9FE5; color: white; flex: 1; min-width: 70px;">Edit</button>`;
                html += `<button is="emby-button" type="button" class="raised btnValidationApply" data-episode-id="${ep.EpisodeId}" data-series-id="${data.SeriesId}" data-season-number="${data.SeasonNumber}" data-episode-name="${ep.EpisodeName || 'Unknown'}" style="padding: 0.3em 0.6em; font-size: 0.85em; background-color: #52B54B; color: white; flex: 1; min-width: 80px;">Apply to all</button>`;
                html += `</div>`;
            } else {
                html += `<div style="font-style: italic; color: #808080; margin-bottom: 0.5em;">No credits marker</div>`;
                html += `<button is="emby-button" type="button" class="raised btnValidationEdit" data-episode-id="${ep.EpisodeId}" data-current-time="" data-duration-seconds="${ep.DurationSeconds || 0}" data-series-id="${data.SeriesId}" style="padding: 0.3em 0.6em; font-size: 0.85em; background-color: #4A9FE5; color: white; width: 100%;">Add Marker</button>`;
            }
            
            html += `</div>`;
        });
        
        html += `</div>`;
        html += `<div style="margin-top: 2em; display: flex; justify-content: flex-end;">`;
        html += `<button is="emby-button" type="button" class="raised button-submit" id="btnCloseValidation" style="padding: 0.6em 1.5em; font-size: 1em; background-color: #4A9FE5; color: white;">Close</button>`;
        html += `</div>`;
        
        content.innerHTML = html;
        
        content.querySelectorAll('.btnValidationPlay').forEach(btn => {
            btn.addEventListener('click', function() {
                const episodeId = this.getAttribute('data-episode-id');
                const timestamp = this.getAttribute('data-timestamp');
                const timestampSeconds = parseTimestamp(timestamp);
                playVideoAtTimestamp(episodeId, timestampSeconds);
            });
        });
        
        content.querySelectorAll('.btnValidationEdit').forEach(btn => {
            btn.addEventListener('click', function() {
                const episodeId = this.getAttribute('data-episode-id');
                const currentTime = this.getAttribute('data-current-time');
                const durationSeconds = parseFloat(this.getAttribute('data-duration-seconds')) || 0;
                const seriesId = this.getAttribute('data-series-id');
                editMarker(instance, view, episodeId, currentTime, seriesId, durationSeconds, function() {
                    refreshValidationModal(instance, view, modal, content, data.SeriesId, data.SeasonNumber);
                });
            });
        });
        
        content.querySelectorAll('.btnValidationApply').forEach(btn => {
            btn.addEventListener('click', function() {
                const episodeId = this.getAttribute('data-episode-id');
                const seriesId = this.getAttribute('data-series-id');
                const seasonNumber = parseInt(this.getAttribute('data-season-number'));
                const episodeName = this.getAttribute('data-episode-name');
                applyToSeason(instance, view, episodeId, seriesId, seasonNumber, episodeName, function() {
                    refreshValidationModal(instance, view, modal, content, data.SeriesId, data.SeasonNumber);
                });
            });
        });
        
        document.getElementById('btnCloseValidation').addEventListener('click', () => {
            document.body.removeChild(modal);
        });
    }

    function showValidationModal(instance, view, data) {
        const modal = document.createElement('div');
        modal.style.cssText = 'position: fixed; top: 0; left: 0; right: 0; bottom: 0; background: rgba(0,0,0,0.85); z-index: 9999; display: flex; align-items: center; justify-content: center; padding: 2em;';
        
        const content = document.createElement('div');
        content.style.cssText = 'background: #181818; color: #e8e8e8; border-radius: 8px; max-width: 1200px; width: 100%; max-height: 90vh; overflow-y: auto; padding: 2em; box-shadow: 0 4px 30px rgba(0,0,0,0.5);';
        
        modal.appendChild(content);
        document.body.appendChild(modal);
        
        updateValidationModalContent(instance, view, modal, content, data);
        
        modal.addEventListener('click', (e) => {
            if (e.target === modal) {
                document.body.removeChild(modal);
            }
        });
    }
    
    return {
        displayMarkers: displayMarkers,
        showSeasonValidation: showSeasonValidation
    };
});
