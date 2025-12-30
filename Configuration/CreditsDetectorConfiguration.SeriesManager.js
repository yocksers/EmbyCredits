define(['loading', 'toast'], function (loading, toast) {
    'use strict';

    function loadSeriesList(view, libraryId = '') {
        loading.show();
        const selectSeries = view.querySelector('#selectSeries');
        const selectSeriesForMarkers = view.querySelector('#selectSeriesForMarkers');
        
        selectSeries.innerHTML = '<option value="">-- All TV Shows --</option>';
        selectSeriesForMarkers.innerHTML = '<option value="">-- Select a TV Show --</option>';

        let url = ApiClient.getUrl('CreditsDetector/GetAllSeries');
        if (libraryId) {
            url = ApiClient.getUrl('CreditsDetector/GetAllSeries', { LibraryId: libraryId });
        }

        ApiClient.getJSON(url).then(response => {
            const series = response.Series || [];
            series.sort((a, b) => a.Name.localeCompare(b.Name));
            series.forEach(s => {
                const option = document.createElement('option');
                option.value = s.Id;
                option.textContent = s.Name;
                selectSeries.appendChild(option);

                const option2 = option.cloneNode(true);
                selectSeriesForMarkers.appendChild(option2);
            });
            loading.hide();
        }).catch(error => {
            loading.hide();
            console.error('Error loading series:', error);
            toast({ type: 'error', text: 'Failed to load TV shows' });
        });
    }

    function loadLibraryFilter(view) {
        ApiClient.getJSON(ApiClient.getUrl('Library/MediaFolders')).then(response => {
            const selectLibraryFilter = view.querySelector('#selectLibraryFilter');
            selectLibraryFilter.innerHTML = '<option value="">-- All Libraries --</option>';

            const tvLibraries = response.Items.filter(library => {
                return library.CollectionType === 'tvshows' || library.CollectionType === 'mixed' || !library.CollectionType;
            });

            tvLibraries.sort((a, b) => a.Name.localeCompare(b.Name));

            tvLibraries.forEach(library => {
                const option = document.createElement('option');
                option.value = library.Id;
                option.textContent = library.Name;
                selectLibraryFilter.appendChild(option);
            });
        }).catch(error => {
            console.error('Error loading library filter:', error);
        });
    }

    function loadLibraries(view, config) {
        const librariesContainer = view.querySelector('#creditsLibraries');
        if (!librariesContainer) return;

        librariesContainer.innerHTML = '';
        const libraryIds = config.LibraryIds || [];

        ApiClient.getJSON(ApiClient.getUrl('Library/MediaFolders')).then(response => {
            const tvLibraries = response.Items.filter(library => {
                return library.CollectionType === 'tvshows' || library.CollectionType === 'mixed' || !library.CollectionType;
            });

            tvLibraries.sort((a, b) => a.Name.localeCompare(b.Name));
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

    function loadEpisodesForSeries(view, seriesId) {
        if (!seriesId) {
            view.querySelector('#selectEpisode').innerHTML = '<option value="">-- Select Show First --</option>';
            return;
        }

        loading.show();
        ApiClient.getJSON(ApiClient.getUrl('Items', {
            ParentId: seriesId,
            IncludeItemTypes: 'Episode',
            Recursive: true,
            Fields: 'SeasonUserData',
            SortBy: 'SortName'
        })).then(response => {
            const episodes = response.Items || [];
            
            // Sort episodes by season number, then episode number
            episodes.sort((a, b) => {
                const seasonA = a.ParentIndexNumber || 0;
                const seasonB = b.ParentIndexNumber || 0;
                if (seasonA !== seasonB) {
                    return seasonA - seasonB;
                }
                return (a.IndexNumber || 0) - (b.IndexNumber || 0);
            });
            
            const selectEpisode = view.querySelector('#selectEpisode');
            selectEpisode.innerHTML = '<option value="">-- All Episodes --</option>';
            
            episodes.forEach(ep => {
                const option = document.createElement('option');
                option.value = ep.Id;
                const seasonNum = (ep.ParentIndexNumber || 0).toString().padStart(2, '0');
                const episodeNum = (ep.IndexNumber || 0).toString().padStart(2, '0');
                option.textContent = `S${seasonNum}E${episodeNum} - ${ep.Name}`;
                selectEpisode.appendChild(option);
            });
            loading.hide();
        }).catch(error => {
            loading.hide();
            console.error('Error loading episodes:', error);
            toast({ type: 'error', text: 'Failed to load episodes.' });
        });
    }

    return {
        loadSeriesList: loadSeriesList,
        loadLibraryFilter: loadLibraryFilter,
        loadLibraries: loadLibraries,
        loadEpisodesForSeries: loadEpisodesForSeries
    };
});
