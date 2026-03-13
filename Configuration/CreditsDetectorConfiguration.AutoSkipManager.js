define(['loading', 'toast'], function (loading, toast) {
    'use strict';

    function loadAutoSkipSeriesList(view, excludedIds) {
        const listContainer = view.querySelector('#autoSkipSeriesList');
        if (!listContainer) return;

        listContainer.innerHTML = '<div style="padding: 1em; opacity: 0.5;">Loading TV shows...</div>';

        ApiClient.getJSON(ApiClient.getUrl('CreditsDetector/GetAllSeries')).then(response => {
            const series = response.Series || [];
            series.sort((a, b) => a.Name.localeCompare(b.Name));

            if (series.length === 0) {
                listContainer.innerHTML = '<div style="padding: 1em; opacity: 0.5;">No TV shows found</div>';
                return;
            }

            listContainer.innerHTML = '';
            series.forEach(s => {
                const isExcluded = excludedIds && excludedIds.includes(s.Id);
                const div = document.createElement('div');
                div.className = 'checkboxContainer autoSkipSeriesItem';
                div.setAttribute('data-series-name', s.Name.toLowerCase());
                div.innerHTML = `
                    <label>
                        <input is="emby-checkbox" type="checkbox" class="chkAutoSkipSeries" data-series-id="${s.Id}" ${isExcluded ? 'checked' : ''} />
                        <span>${s.Name}</span>
                    </label>
                `;
                listContainer.appendChild(div);
            });
        }).catch(error => {
            console.error('Error loading series for auto skip:', error);
            listContainer.innerHTML = '<div style="padding: 1em; opacity: 0.5;">Failed to load TV shows</div>';
        });
    }

    function getAutoSkipExcludedIds(view) {
        const checkboxes = view.querySelectorAll('.chkAutoSkipSeries:checked');
        const ids = [];
        checkboxes.forEach(cb => {
            ids.push(cb.getAttribute('data-series-id'));
        });
        return ids;
    }

    function hasSeriesLoaded(view) {
        return view.querySelectorAll('.chkAutoSkipSeries').length > 0;
    }

    function filterSeries(view, searchTerm) {
        const lower = searchTerm.toLowerCase();
        const items = view.querySelectorAll('.autoSkipSeriesItem');
        items.forEach(item => {
            const name = item.getAttribute('data-series-name') || '';
            item.style.display = name.includes(lower) ? '' : 'none';
        });
    }

    function selectAll(view) {
        view.querySelectorAll('.chkAutoSkipSeries').forEach(cb => {
            cb.checked = true;
        });
    }

    function deselectAll(view) {
        view.querySelectorAll('.chkAutoSkipSeries').forEach(cb => {
            cb.checked = false;
        });
    }

    return {
        loadAutoSkipSeriesList: loadAutoSkipSeriesList,
        getAutoSkipExcludedIds: getAutoSkipExcludedIds,
        hasSeriesLoaded: hasSeriesLoaded,
        filterSeries: filterSeries,
        selectAll: selectAll,
        deselectAll: deselectAll
    };
});
