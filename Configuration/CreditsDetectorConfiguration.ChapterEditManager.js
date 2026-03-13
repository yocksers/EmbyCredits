define(['loading', 'toast'], function (loading, toast) {
    'use strict';

    const CHAPTER_TYPES = ['Chapter', 'IntroStart', 'IntroEnd', 'CreditsStart'];

    let _view = null;
    let _navStack = []; // [{id, name, type}]
    let _currentEpisodeId = null;
    let _isDirty = false;
    let _isSearchMode = false;
    let _searchTimeout = null;
    let _allEpisodeItems = null; // cached episode list with Chapters for filtering

    function q(id) { return _view.querySelector('#' + id); }

    function pad(n, len) { return String(n).padStart(len, '0'); }

    function ticksToTime(ticks) {
        var totalMs = Math.floor(ticks / 10000);
        var ms = totalMs % 1000;
        var totalSecs = Math.floor(totalMs / 1000);
        var ss = totalSecs % 60;
        var mm = Math.floor(totalSecs / 60) % 60;
        var hh = Math.floor(totalSecs / 3600);
        return pad(hh, 2) + ':' + pad(mm, 2) + ':' + pad(ss, 2) + '.' + pad(ms, 3);
    }

    function hmsmsToTicks(hh, mm, ss, ms) {
        var totalMs = (hh * 3600 + mm * 60 + ss) * 1000 + ms;
        return totalMs * 10000;
    }

    function formatRuntime(ticks) {
        var totalSecs = Math.floor(ticks / 10000000);
        var h = Math.floor(totalSecs / 3600);
        var m = Math.floor((totalSecs % 3600) / 60);
        var s = totalSecs % 60;
        return h > 0
            ? h + ':' + pad(m, 2) + ':' + pad(s, 2)
            : pad(m, 2) + ':' + pad(s, 2);
    }

    function escapeAttr(str) {
        if (!str) return '';
        return String(str)
            .replace(/&/g, '&amp;')
            .replace(/"/g, '&quot;')
            .replace(/</g, '&lt;')
            .replace(/>/g, '&gt;');
    }

    // ------------------------------------------------------------------ //
    //  Navigation / Browser
    // ------------------------------------------------------------------ //

    function renderPath() {
        var pathEl = q('chapterBrowserPath');
        var html = '<span class="chapter-path-crumb" data-nav-idx="-1">[root]</span>';
        _navStack.forEach(function (item, idx) {
            html += ' <span style="opacity:0.35;margin:0 2px;">/</span>';
            html += '<span class="chapter-path-crumb" data-nav-idx="' + idx + '">[' + escapeAttr(item.name) + ']</span>';
        });
        pathEl.innerHTML = 'Path &nbsp; ' + html;

        pathEl.querySelectorAll('.chapter-path-crumb').forEach(function (el) {
            el.addEventListener('click', function () {
                var idx = parseInt(el.getAttribute('data-nav-idx'));
                navigateToIndex(idx);
            });
        });
    }

    function navigateToIndex(idx) {
        if (idx < 0) {
            _navStack = [];
        } else {
            _navStack = _navStack.slice(0, idx + 1);
        }
        _isSearchMode = false;
        var searchEl = q('chapterBrowserSearch');
        if (searchEl) searchEl.value = '';
        loadCurrentLevel();
    }

    function renderBrowserList(items) {
        var listEl = q('chapterBrowserList');
        listEl.innerHTML = '';

        if (!items || items.length === 0) {
            listEl.innerHTML = '<div style="text-align:center;padding:2em 0.5em;opacity:0.38;font-size:0.85em;">No items found</div>';
            return;
        }

        items.forEach(function (item) {
            var isEpisode = item.Type === 'Episode';
            var div = document.createElement('div');
            div.className = 'chapter-browser-item ' + (isEpisode ? 'is-episode' : 'is-folder');

            if (item.Id === _currentEpisodeId) {
                div.classList.add('selected');
            }

            var label = item.Name || '';
            if (isEpisode) {
                var ep = item.IndexNumber != null ? pad(item.IndexNumber, 2) : null;
                var sn = item.ParentIndexNumber != null ? pad(item.ParentIndexNumber, 2) : null;
                if (sn && ep) label = 'S' + sn + 'E' + ep + ' - ' + label;
                else if (ep) label = ep + ' - ' + label;
            }

            var iconSpan = document.createElement('span');
            iconSpan.className = 'chapter-item-icon';
            var labelSpan = document.createElement('span');
            labelSpan.className = 'chapter-item-label';
            labelSpan.textContent = label;
            labelSpan.title = label;

            div.appendChild(iconSpan);
            div.appendChild(labelSpan);

            div.addEventListener('click', function () {
                if (isEpisode) {
                    _view.querySelectorAll('.chapter-browser-item').forEach(function (el) {
                        el.classList.remove('selected');
                    });
                    div.classList.add('selected');
                    loadEpisodeChapters(item.Id, label);
                } else {
                    _navStack.push({ id: item.Id, name: item.Name, type: item.Type });
                    loadCurrentLevel();
                }
            });

            listEl.appendChild(div);
        });
    }

    function loadCurrentLevel() {
        renderPath();
        var listEl = q('chapterBrowserList');
        listEl.innerHTML = '<div style="text-align:center;padding:2em 0.5em;opacity:0.38;font-size:0.85em;">Loading...</div>';

        if (_navStack.length === 0) {
            q('chapterBrowserFilters').style.display = 'none';
            _allEpisodeItems = null;
            // Root: show TV libraries
            ApiClient.getJSON(ApiClient.getUrl('Library/MediaFolders'))
                .then(function (response) {
                    var libs = (response.Items || []).filter(function (l) {
                        return l.CollectionType === 'tvshows' || l.CollectionType === 'mixed' || !l.CollectionType;
                    }).sort(function (a, b) { return a.Name.localeCompare(b.Name); });
                    renderBrowserList(libs);
                })
                .catch(function () { renderBrowserList([]); });
            return;
        }

        var current = _navStack[_navStack.length - 1];
        var includeTypes;
        var sortBy = 'SortName';

        if (current.type === 'CollectionFolder') {
            includeTypes = 'Series';
        } else if (current.type === 'Series') {
            includeTypes = 'Season';
            sortBy = 'IndexNumber';
        } else if (current.type === 'Season') {
            includeTypes = 'Episode';
            sortBy = 'IndexNumber';
        } else {
            q('chapterBrowserFilters').style.display = 'none';
            _allEpisodeItems = null;
            renderBrowserList([]);
            return;
        }

        var isEpisodeLevel = includeTypes === 'Episode';
        q('chapterBrowserFilters').style.display = isEpisodeLevel ? 'block' : 'none';
        if (!isEpisodeLevel) _allEpisodeItems = null;

        var params = {
            ParentId: current.id,
            IncludeItemTypes: includeTypes,
            SortBy: sortBy,
            SortOrder: 'Ascending',
            Fields: isEpisodeLevel ? 'ParentIndexNumber,IndexNumber,Chapters' : 'ParentIndexNumber,IndexNumber',
            Limit: 1000
        };

        ApiClient.getJSON(ApiClient.getUrl('Items', params))
            .then(function (response) {
                var items = response.Items || [];
                if (isEpisodeLevel) {
                    _allEpisodeItems = items;
                    applyBrowserFilter();
                } else {
                    renderBrowserList(items);
                }
            })
            .catch(function () { renderBrowserList([]); });
    }

    function applyBrowserFilter() {
        if (!_allEpisodeItems) return;

        var noChaptersOnly = q('chapterFilterNoChapters').checked;
        var maxCountRaw = q('chapterFilterMaxCount').value.trim();
        var minGapRaw = q('chapterFilterMinGap').value.trim();
        var hasMaxCount = maxCountRaw !== '' && !isNaN(parseInt(maxCountRaw, 10));
        var hasMinGap = minGapRaw !== '' && !isNaN(parseInt(minGapRaw, 10));
        var maxCount = hasMaxCount ? parseInt(maxCountRaw, 10) : null;
        var minGapTicks = hasMinGap ? parseInt(minGapRaw, 10) * 10000000 : null;

        var anyFilterActive = noChaptersOnly || hasMaxCount || hasMinGap;

        if (!anyFilterActive) {
            renderBrowserList(_allEpisodeItems);
            return;
        }

        var filtered = _allEpisodeItems.filter(function (item) {
            var chapters = item.Chapters || [];
            var count = chapters.length;

            if (noChaptersOnly && count !== 0) return false;
            if (hasMaxCount && count >= maxCount) return false;
            if (hasMinGap) {
                var hasLargeGap = false;
                for (var i = 1; i < chapters.length; i++) {
                    if ((chapters[i].StartPositionTicks - chapters[i - 1].StartPositionTicks) > minGapTicks) {
                        hasLargeGap = true;
                        break;
                    }
                }
                if (!hasLargeGap) return false;
            }

            return true;
        });

        renderBrowserList(filtered);
    }

    function handleSearch(query) {
        if (!query || query.trim().length < 2) {
            if (_isSearchMode) {
                _isSearchMode = false;
                loadCurrentLevel();
            }
            return;
        }

        _isSearchMode = true;
        var listEl = q('chapterBrowserList');
        listEl.innerHTML = '<div style="text-align:center;padding:2em 0.5em;opacity:0.38;font-size:0.85em;">Searching...</div>';

        var params = {
            SearchTerm: query.trim(),
            IncludeItemTypes: 'Episode',
            Recursive: true,
            SortBy: 'SortName',
            SortOrder: 'Ascending',
            Fields: 'ParentIndexNumber,IndexNumber,SeriesName',
            Limit: 100
        };

        ApiClient.getJSON(ApiClient.getUrl('Items', params))
            .then(function (response) {
                if (_isSearchMode) renderBrowserList(response.Items || []);
            })
            .catch(function () {
                if (_isSearchMode) renderBrowserList([]);
            });
    }

    // ------------------------------------------------------------------ //
    //  Chapter Editor
    // ------------------------------------------------------------------ //

    function loadEpisodeChapters(episodeId, displayName) {
        _currentEpisodeId = episodeId;
        _isDirty = false;

        q('chapterEditorEmpty').style.display = 'none';
        q('chapterEditorContent').style.display = 'block';
        q('chapterEpisodeTitle').textContent = displayName;
        q('chapterEpisodeSubtitle').textContent = 'Episode';
        q('chapterEpisodeRuntime').textContent = '';
        q('chapterTableBody').innerHTML =
            '<div style="text-align:center;padding:1.5em;opacity:0.4;font-size:0.85em;">Loading chapters...</div>';
        q('chapterUnsavedNote').style.opacity = '0';

        // Use the native Emby Items API — this is the authoritative source for chapter data
        var userId = ApiClient.getCurrentUserId();
        ApiClient.getJSON(ApiClient.getUrl('Users/' + userId + '/Items/' + episodeId, {
            Fields: 'Chapters'
        }))
        .then(function (response) {
            if (response.SeriesName) {
                var sub = response.SeriesName;
                if (response.ParentIndexNumber != null) sub += ' · Season ' + response.ParentIndexNumber;
                if (response.IndexNumber != null) sub += ' · Episode ' + response.IndexNumber;
                q('chapterEpisodeSubtitle').textContent = sub;
            }
            if (response.RunTimeTicks) {
                q('chapterEpisodeRuntime').textContent = 'Runtime: ' + formatRuntime(response.RunTimeTicks);
            } else {
                q('chapterEpisodeRuntime').textContent = '';
            }
            var chapters = (response.Chapters || []).map(function (c) {
                return {
                    Name: c.Name || '',
                    MarkerType: c.MarkerType || 'Chapter',
                    StartPositionTicks: c.StartPositionTicks || 0
                };
            });
            renderChapters(chapters);
        })
        .catch(function (err) {
            console.error('Error loading chapters:', err);
            toast({ type: 'error', text: 'Failed to load chapters' });
            q('chapterTableBody').innerHTML =
                '<div style="text-align:center;padding:1.5em;color:#ef9a9a;opacity:0.8;font-size:0.85em;">Failed to load chapters</div>';
        });
    }

    function renderChapters(chapters) {
        var body = q('chapterTableBody');
        body.innerHTML = '';

        if (!chapters || chapters.length === 0) {
            body.innerHTML = '<div style="text-align:center;padding:1.5em;opacity:0.38;font-size:0.85em;">No chapters — use the Add form above to create one.</div>';
            return;
        }

        chapters.forEach(function (c) {
            body.appendChild(buildChapterRow(c.Name || '', c.MarkerType || 'Chapter', c.StartPositionTicks));
        });
    }

    function buildChapterRow(name, markerType, ticks) {
        var timeStr = ticksToTime(ticks);
        var hh = parseInt(timeStr.substring(0, 2), 10);
        var mm = parseInt(timeStr.substring(3, 5), 10);
        var ss = parseInt(timeStr.substring(6, 8), 10);
        var ms = parseInt(timeStr.substring(9, 12), 10);

        var row = document.createElement('div');
        row.className = 'chapter-row';

        var typeOpts = CHAPTER_TYPES.map(function (t) {
            return '<option value="' + t + '"' + (t === markerType ? ' selected' : '') + '>' + t + '</option>';
        }).join('');

        row.innerHTML =
            '<label style="display:flex;align-items:center;justify-content:center;cursor:pointer;">' +
                '<input type="checkbox" class="chapter-row-check" style="cursor:pointer;" />' +
            '</label>' +
            '<input type="text" class="chapter-inp chapter-row-name" value="' + escapeAttr(name) + '" placeholder="Name" />' +
            '<select class="chapter-type-sel chapter-row-type">' + typeOpts + '</select>' +
            '<div class="chapter-time-group">' +
                '<input type="number" class="chapter-inp chapter-time-num chapter-hh" min="0" max="99" value="' + hh + '" title="Hours" />' +
                '<span class="chapter-time-sep">:</span>' +
                '<input type="number" class="chapter-inp chapter-time-num chapter-mm" min="0" max="59" value="' + mm + '" title="Minutes" />' +
                '<span class="chapter-time-sep">:</span>' +
                '<input type="number" class="chapter-inp chapter-time-num chapter-ss" min="0" max="59" value="' + ss + '" title="Seconds" />' +
                '<span class="chapter-time-sep">.</span>' +
                '<input type="number" class="chapter-inp chapter-time-ms chapter-ms" min="0" max="999" value="' + ms + '" title="Milliseconds" />' +
            '</div>' +
            '<button type="button" class="chapter-del-btn" title="Delete this chapter">⊗</button>';

        // Mark dirty on any edit
        row.querySelectorAll('input, select').forEach(function (el) {
            el.addEventListener('change', markDirty);
            el.addEventListener('input', markDirty);
        });

        // Inline row delete
        row.querySelector('.chapter-del-btn').addEventListener('click', function () {
            row.remove();
            refreshEmptyState();
            markDirty();
        });

        return row;
    }

    function refreshEmptyState() {
        var body = q('chapterTableBody');
        if (body.querySelectorAll('.chapter-row').length === 0) {
            body.innerHTML = '<div style="text-align:center;padding:1.5em;opacity:0.38;font-size:0.85em;">No chapters — use the Add form above to create one.</div>';
        }
    }

    function markDirty() {
        _isDirty = true;
        q('chapterUnsavedNote').style.opacity = '1';
    }

    function addChapter() {
        if (!_currentEpisodeId) return;

        var name = q('chapterNewName').value;
        var type = q('chapterNewType').value;
        var hh  = Math.max(0, parseInt(q('chapterNewHH').value) || 0);
        var mm  = Math.max(0, Math.min(59, parseInt(q('chapterNewMM').value) || 0));
        var ss  = Math.max(0, Math.min(59, parseInt(q('chapterNewSS').value) || 0));
        var ms  = Math.max(0, Math.min(999, parseInt(q('chapterNewMS').value) || 0));
        var ticks = hmsmsToTicks(hh, mm, ss, ms);

        var body = q('chapterTableBody');
        // Remove empty placeholder
        var placeholder = body.querySelector('div:not(.chapter-row)');
        if (placeholder) placeholder.remove();

        body.appendChild(buildChapterRow(name, type, ticks));

        // Reset add form
        q('chapterNewName').value = '';
        q('chapterNewType').value = 'Chapter';
        q('chapterNewHH').value = '0';
        q('chapterNewMM').value = '0';
        q('chapterNewSS').value = '0';
        q('chapterNewMS').value = '0';
        q('chapterNewName').focus();

        markDirty();
    }

    function deleteSelected() {
        var body = q('chapterTableBody');
        var checked = body.querySelectorAll('.chapter-row-check:checked');
        if (checked.length === 0) {
            toast({ type: 'warning', text: 'No chapters selected' });
            return;
        }
        checked.forEach(function (cb) { cb.closest('.chapter-row').remove(); });
        refreshEmptyState();
        markDirty();
    }

    function collectChapters() {
        var rows = q('chapterTableBody').querySelectorAll('.chapter-row');
        var chapters = [];
        rows.forEach(function (row) {
            var name       = row.querySelector('.chapter-row-name').value;
            var markerType = row.querySelector('.chapter-row-type').value;
            var hh  = Math.max(0, parseInt(row.querySelector('.chapter-hh').value) || 0);
            var mm  = Math.max(0, Math.min(59, parseInt(row.querySelector('.chapter-mm').value) || 0));
            var ss  = Math.max(0, Math.min(59, parseInt(row.querySelector('.chapter-ss').value) || 0));
            var ms  = Math.max(0, Math.min(999, parseInt(row.querySelector('.chapter-ms').value) || 0));
            chapters.push({
                Name: name,
                MarkerType: markerType,
                StartPositionTicks: hmsmsToTicks(hh, mm, ss, ms)
            });
        });
        // Sort by ticks ascending
        chapters.sort(function (a, b) { return a.StartPositionTicks - b.StartPositionTicks; });
        return chapters;
    }

    function saveChapters() {
        if (!_currentEpisodeId) return;

        var chapters = collectChapters();
        loading.show();

        fetch(ApiClient.getUrl('CreditsDetector/SaveEpisodeChapters'), {
            method: 'POST',
            headers: {
                'X-Emby-Token': ApiClient.accessToken(),
                'Content-Type': 'application/json'
            },
            body: JSON.stringify({
                EpisodeId: _currentEpisodeId,
                Chapters: chapters
            })
        })
        .then(function (r) { return r.json(); })
        .then(function (result) {
            loading.hide();
            if (result.Success) {
                _isDirty = false;
                q('chapterUnsavedNote').style.opacity = '0';
                toast({ type: 'success', text: 'Saved ' + chapters.length + ' chapter(s)' });
                // Re-render sorted result
                renderChapters(chapters);
            } else {
                toast({ type: 'error', text: 'Save failed: ' + (result.Message || 'Unknown error') });
            }
        })
        .catch(function (err) {
            loading.hide();
            console.error('Error saving chapters:', err);
            toast({ type: 'error', text: 'Failed to save chapters' });
        });
    }

    // ------------------------------------------------------------------ //
    //  Init
    // ------------------------------------------------------------------ //

    function init(view) {
        _view = view;
        _navStack = [];
        _currentEpisodeId = null;
        _isDirty = false;
        _isSearchMode = false;

        // Load root libraries immediately
        loadCurrentLevel();

        // Search input with debounce
        var searchEl = q('chapterBrowserSearch');
        searchEl.addEventListener('input', function () {
            clearTimeout(_searchTimeout);
            var val = searchEl.value;
            if (!val || val.trim().length < 2) {
                if (_isSearchMode) {
                    _isSearchMode = false;
                    loadCurrentLevel();
                }
                return;
            }
            _searchTimeout = setTimeout(function () { handleSearch(val); }, 400);
        });

        // Clear search on Escape
        searchEl.addEventListener('keydown', function (e) {
            if (e.key === 'Escape') {
                searchEl.value = '';
                if (_isSearchMode) {
                    _isSearchMode = false;
                    loadCurrentLevel();
                }
            }
        });

        // Filter inputs — re-apply filter on any change
        var filterNoChaps = q('chapterFilterNoChapters');
        if (filterNoChaps) filterNoChaps.addEventListener('change', applyBrowserFilter);
        var filterMaxCount = q('chapterFilterMaxCount');
        if (filterMaxCount) filterMaxCount.addEventListener('input', applyBrowserFilter);
        var filterMinGap = q('chapterFilterMinGap');
        if (filterMinGap) filterMinGap.addEventListener('input', applyBrowserFilter);

        // Add chapter button
        var btnAdd = q('btnAddChapter');
        if (btnAdd) btnAdd.addEventListener('click', addChapter);

        // Add on Enter in name field
        var nameField = q('chapterNewName');
        if (nameField) {
            nameField.addEventListener('keydown', function (e) {
                if (e.key === 'Enter') { e.preventDefault(); addChapter(); }
            });
        }

        // Delete selected
        var btnDel = q('btnDeleteSelectedChapters');
        if (btnDel) btnDel.addEventListener('click', deleteSelected);

        // Save
        var btnSave = q('btnSaveChapters');
        if (btnSave) btnSave.addEventListener('click', saveChapters);
    }

    return { init: init };
});
