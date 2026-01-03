define(['loading', 'toast'], function (loading, toast) {
    'use strict';
    
    function displayMarkers(view) {
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
                markersSeriesName.textContent = response.SeriesName || 'Series Markers';
                markersContent.innerHTML = '';
                
                if (!response.Episodes || response.Episodes.length === 0) {
                    markersContent.innerHTML = '<div style="padding: 1em; color: #999;">No episodes found for this series.</div>';
                    return;
                }
                
                // Group by season (exclude Season 0 specials)
                const episodesBySeason = {};
                response.Episodes.forEach(ep => {
                    const season = ep.Season || 0;
                    // Skip TV specials (Season 0)
                    if (season === 0) return;
                    
                    if (!episodesBySeason[season]) {
                        episodesBySeason[season] = [];
                    }
                    episodesBySeason[season].push(ep);
                });
                
                // Count episodes with markers
                const episodesWithMarkers = response.Episodes.filter(ep => ep.HasCreditsMarker).length;
                
                // Display by season
                Object.keys(episodesBySeason).sort((a, b) => Number(a) - Number(b)).forEach(season => {
                    const seasonDiv = document.createElement('div');
                    seasonDiv.style.cssText = 'margin-bottom: 1.5em;';
                    
                    const seasonHeader = document.createElement('h4');
                    seasonHeader.style.cssText = 'color: #4A9FE5; margin-bottom: 0.5em;';
                    seasonHeader.textContent = `Season ${season}`;
                    seasonDiv.appendChild(seasonHeader);
                    
                    episodesBySeason[season].forEach(episode => {
                        const episodeDiv = document.createElement('div');
                        const hasMarkers = episode.HasCreditsMarker;
                        episodeDiv.style.cssText = `padding: 1em; margin-bottom: 0.5em; border: 1px solid #444; border-radius: 4px; background: rgba(255,255,255,0.05);`;
                        
                        let markersHtml = '';
                        if (hasMarkers && episode.Markers && episode.Markers.length > 0) {
                            episode.Markers.forEach(marker => {
                                markersHtml += `<div style="margin-top: 0.5em; color: #ccc;">
                                    <strong style="color: #52b54b;">${marker.MarkerType || 'Credits'}</strong>: ${marker.StartTime}
                                    <button class="btnEditMarker" data-episode-id="${episode.EpisodeId}" data-current-time="${marker.StartTime}" style="margin-left: 1em; padding: 0.25em 0.75em; background: #4A9FE5; border: none; border-radius: 3px; color: white; cursor: pointer; font-size: 0.9em;">Edit</button>
                                </div>`;
                            });
                        } else {
                            markersHtml = `<div style="margin-top: 0.5em; color: #999; font-style: italic;">No credits marker <button class="btnEditMarker" data-episode-id="${episode.EpisodeId}" data-current-time="" style="margin-left: 1em; padding: 0.25em 0.75em; background: #52B54B; border: none; border-radius: 3px; color: white; cursor: pointer; font-size: 0.9em;">Add Marker</button></div>`;
                        }
                        
                        episodeDiv.innerHTML = `
                            <div><strong style="color: ${hasMarkers ? '#52b54b' : '#999'};">${episode.SeasonEpisode}</strong> - ${episode.EpisodeName || 'Unknown'} <span style="color: #888; font-size: 0.9em;">(${episode.Duration})</span></div>
                            ${markersHtml}
                        `;
                        
                        seasonDiv.appendChild(episodeDiv);
                    });
                    
                    markersContent.appendChild(seasonDiv);
                });
                
                // Add click handlers for edit buttons
                markersContent.querySelectorAll('.btnEditMarker').forEach(btn => {
                    btn.addEventListener('click', function() {
                        const episodeId = this.getAttribute('data-episode-id');
                        const currentTime = this.getAttribute('data-current-time');
                        editMarker(view, episodeId, currentTime, seriesId);
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
    
    function parseTimeToSeconds(timeStr) {
        // Parse HH:MM:SS or MM:SS format to seconds
        const parts = timeStr.split(':').map(p => parseInt(p, 10));
        if (parts.length === 3) {
            return parts[0] * 3600 + parts[1] * 60 + parts[2];
        } else if (parts.length === 2) {
            return parts[0] * 60 + parts[1];
        }
        return 0;
    }
    
    function editMarker(view, episodeId, currentTime, seriesId) {
        const currentSeconds = currentTime ? parseTimeToSeconds(currentTime) : 0;
        const promptMessage = currentTime ? 
            `Edit credits start time for this episode.\nCurrent: ${currentTime}\n\nEnter new time (format: HH:MM:SS or MM:SS):` :
            `Add credits start time for this episode.\n\nEnter time (format: HH:MM:SS or MM:SS):`;
        
        const newTime = prompt(promptMessage, currentTime || '');
        
        if (newTime === null) return; // User cancelled
        
        if (!newTime || !/^\d{1,2}:\d{2}(:\d{2})?$/.test(newTime)) {
            require(['toast'], (toast) => {
                toast({ type: 'error', text: 'Invalid time format. Use HH:MM:SS or MM:SS' });
            });
            return;
        }
        
        const newSeconds = parseTimeToSeconds(newTime);
        if (newSeconds < 0) {
            require(['toast'], (toast) => {
                toast({ type: 'error', text: 'Time must be positive' });
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
                    CreditsStartSeconds: newSeconds
                })
            })
            .then(response => response.json())
            .then(result => {
                loading.hide();
                if (result.Success) {
                    toast({ type: 'success', text: result.Message });
                    // Refresh the markers display
                    displayMarkers(view);
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
    
    return {
        displayMarkers: displayMarkers
    };
});
