define(['loading', 'toast'], function (loading, toast) {
    'use strict';

    const CHAPTER_TYPES = ['Chapter', 'IntroStart', 'IntroEnd', 'CreditsStart'];

    function isAllLibraries() { return _view.querySelector('#chkAllLibraries').checked; }

    let _view = null;
    let _navStack = []; // [{id, name, type}]
    let _currentEpisodeId = null;
    let _currentEpisodeRuntimeTicks = 0;
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
            var isLeaf = item.Type === 'Episode' || item.Type === 'Movie';
            var div = document.createElement('div');
            div.className = 'chapter-browser-item ' + (isLeaf ? 'is-episode' : 'is-folder');

            if (item.Id === _currentEpisodeId) {
                div.classList.add('selected');
            }

            var label = item.Name || '';
            if (item.Type === 'Episode') {
                var ep = item.IndexNumber != null ? pad(item.IndexNumber, 2) : null;
                var sn = item.ParentIndexNumber != null ? pad(item.ParentIndexNumber, 2) : null;
                if (sn && ep) label = 'S' + sn + 'E' + ep + ' - ' + label;
                else if (ep) label = ep + ' - ' + label;
            } else if (item.Type === 'Movie') {
                label = '🎬 ' + label;
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
                if (isLeaf) {
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

    function fetchAllItems(params, onDone, onError) {
        var pageSize = 1000;
        var allItems = [];
        function fetchPage(startIndex) {
            var pageParams = Object.assign({}, params, { Limit: pageSize, StartIndex: startIndex });
            ApiClient.getJSON(ApiClient.getUrl('Items', pageParams))
                .then(function (response) {
                    var items = response.Items || [];
                    allItems = allItems.concat(items);
                    var total = response.TotalRecordCount || allItems.length;
                    if (allItems.length < total && items.length > 0) {
                        fetchPage(allItems.length);
                    } else {
                        onDone(allItems);
                    }
                })
                .catch(onError || function () { onDone(allItems); });
        }
        fetchPage(0);
    }

    function loadCurrentLevel() {
        renderPath();
        var listEl = q('chapterBrowserList');
        listEl.innerHTML = '<div style="text-align:center;padding:2em 0.5em;opacity:0.38;font-size:0.85em;">Loading...</div>';

        // Check if any filter is active
        var noChaptersOnly = q('chapterFilterNoChapters').checked;
        var maxCountRaw = q('chapterFilterMaxCount').value.trim();
        var minGapRaw = q('chapterFilterMinGap').value.trim();
        var introFilter = q('chapterFilterIntro').value;
        var creditsFilter = q('chapterFilterCredits').value;
        var anyFilterActive = noChaptersOnly ||
            (maxCountRaw !== '' && !isNaN(parseInt(maxCountRaw, 10))) ||
            (minGapRaw !== '' && !isNaN(parseInt(minGapRaw, 10))) ||
            introFilter !== '' || creditsFilter !== '';

        if (_navStack.length === 0) {
            _allEpisodeItems = null;
            if (anyFilterActive) {
                fetchAllEpisodesAndFilter(null);
                return;
            }
            ApiClient.getJSON(ApiClient.getUrl('Library/MediaFolders'))
                .then(function (response) {
                    var libs = (response.Items || []).filter(function (l) {
                        if (isAllLibraries()) return true;
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

        if (current.type === 'CollectionFolder' || current.type === 'UserView') {
            includeTypes = isAllLibraries() ? 'Series,Movie' : 'Series';
        } else if (current.type === 'Series') {
            includeTypes = 'Season';
            sortBy = 'IndexNumber';
        } else if (current.type === 'Season') {
            includeTypes = 'Episode';
            sortBy = 'IndexNumber';
        } else {
            _allEpisodeItems = null;
            renderBrowserList([]);
            return;
        }

        var isEpisodeLevel = includeTypes === 'Episode';
        var isMixedLeafLevel = includeTypes === 'Series,Movie';

        if (!isEpisodeLevel && !isMixedLeafLevel && anyFilterActive) {
            _allEpisodeItems = null;
            fetchAllEpisodesAndFilter(current.id);
            return;
        }

        if (!isEpisodeLevel && !isMixedLeafLevel) _allEpisodeItems = null;

        var params = {
            ParentId: current.id,
            IncludeItemTypes: includeTypes,
            Recursive: isMixedLeafLevel,
            SortBy: sortBy,
            SortOrder: 'Ascending',
            Fields: (isEpisodeLevel || isMixedLeafLevel) ? 'ParentIndexNumber,IndexNumber,Chapters' : 'ParentIndexNumber,IndexNumber'
        };

        fetchAllItems(params, function (items) {
            if (isEpisodeLevel) {
                _allEpisodeItems = items.slice().sort(sortEpisodes);
                applyBrowserFilter();
            } else if (isMixedLeafLevel) {
                var movies = items.filter(function (i) { return i.Type === 'Movie'; });
                var nonMovies = items.filter(function (i) { return i.Type !== 'Movie'; });
                if (anyFilterActive) {
                    _allEpisodeItems = movies;
                    applyBrowserFilter(nonMovies);
                } else {
                    renderBrowserList(items);
                }
            } else {
                renderBrowserList(items);
            }
        }, function () { renderBrowserList([]); });
    }

    function fetchAllEpisodesAndFilter(parentId) {
        var listEl = q('chapterBrowserList');
        listEl.innerHTML = '<div style="text-align:center;padding:2em 0.5em;opacity:0.38;font-size:0.85em;">Filtering...</div>';

        var includeTypes = isAllLibraries() ? 'Episode,Movie' : 'Episode';

        var params = {
            IncludeItemTypes: includeTypes,
            Recursive: true,
            SortBy: 'SeriesName,ParentIndexNumber,IndexNumber',
            SortOrder: 'Ascending',
            Fields: 'ParentIndexNumber,IndexNumber,SeriesName,Chapters'
        };
        if (parentId) params.ParentId = parentId;

        fetchAllItems(params, function (items) {
            _allEpisodeItems = items.filter(function (e) {
                if (e.Type === 'Movie') return true;
                return e.Type === 'Episode';
            });
            applyBrowserFilter();
        }, function () { renderBrowserList([]); });
    }

    function applyBrowserFilter(prependItems) {
        if (!_allEpisodeItems) return;

        var noChaptersOnly = q('chapterFilterNoChapters').checked;
        var maxCountRaw = q('chapterFilterMaxCount').value.trim();
        var minGapRaw = q('chapterFilterMinGap').value.trim();
        var introFilter = q('chapterFilterIntro').value;
        var creditsFilter = q('chapterFilterCredits').value;
        var hasMaxCount = maxCountRaw !== '' && !isNaN(parseInt(maxCountRaw, 10));
        var hasMinGap = minGapRaw !== '' && !isNaN(parseInt(minGapRaw, 10));
        var maxCount = hasMaxCount ? parseInt(maxCountRaw, 10) : null;
        var minGapTicks = hasMinGap ? parseInt(minGapRaw, 10) * 10000000 : null;

        var anyFilterActive = noChaptersOnly || hasMaxCount || hasMinGap || introFilter !== '' || creditsFilter !== '';

        if (!anyFilterActive) {
            renderBrowserList((prependItems || []).concat(_allEpisodeItems.slice().sort(sortEpisodes)));
            return;
        }

        var filtered = _allEpisodeItems.filter(function (item) {
            var chapters = item.Chapters || [];
            var count = chapters.length;

            if (noChaptersOnly && count !== 0) return false;
            if (hasMaxCount && count >= maxCount) return false;
            if (introFilter === 'has' && !chapters.some(function (c) { return (c.MarkerType || 'Chapter') === 'IntroStart'; })) return false;
            if (introFilter === 'missing' && chapters.some(function (c) { return (c.MarkerType || 'Chapter') === 'IntroStart'; })) return false;
            if (creditsFilter === 'has' && !chapters.some(function (c) { return (c.MarkerType || 'Chapter') === 'CreditsStart'; })) return false;
            if (creditsFilter === 'missing' && chapters.some(function (c) { return (c.MarkerType || 'Chapter') === 'CreditsStart'; })) return false;
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

        renderBrowserList((prependItems || []).concat(filtered.sort(sortEpisodes)));
    }

    function sortEpisodes(a, b) {
        var sn1 = a.ParentIndexNumber != null ? a.ParentIndexNumber : 9999;
        var sn2 = b.ParentIndexNumber != null ? b.ParentIndexNumber : 9999;
        if (sn1 !== sn2) return sn1 - sn2;
        var ep1 = a.IndexNumber != null ? a.IndexNumber : 9999;
        var ep2 = b.IndexNumber != null ? b.IndexNumber : 9999;
        return ep1 - ep2;
    }

    function closeSearchDropdown() {
        var dd = q('chapterSearchDropdown');
        if (dd) {
            dd.classList.remove('open');
            dd.innerHTML = '';
        }
    }

    function renderSearchDropdown(items) {
        var dd = q('chapterSearchDropdown');
        if (!dd) return;
        dd.innerHTML = '';

        if (!items || items.length === 0) {
            var empty = document.createElement('div');
            empty.className = 'chapter-search-option';
            empty.style.opacity = '0.45';
            empty.style.cursor = 'default';
            empty.textContent = 'No results found';
            dd.appendChild(empty);
            dd.classList.add('open');
            return;
        }

        items.forEach(function (item) {
            var opt = document.createElement('div');
            opt.className = 'chapter-search-option';
            opt.textContent = item.Name || '';
            opt.title = item.Name || '';
            opt.addEventListener('mousedown', function (e) {
                e.preventDefault();
                clearTimeout(_searchTimeout);
                closeSearchDropdown();
                _isSearchMode = false;
                q('chapterBrowserSearch').value = '';
                if (item.Type === 'Movie' || item.Type === 'Episode') {
                    _view.querySelectorAll('.chapter-browser-item').forEach(function (el) {
                        el.classList.remove('selected');
                    });
                    loadEpisodeChapters(item.Id, item.Name);
                } else {
                    _navStack = [{ id: item.Id, name: item.Name, type: item.Type }];
                    loadCurrentLevel();
                }
            });
            dd.appendChild(opt);
        });

        dd.classList.add('open');
    }

    function handleSearch(query) {
        if (!query || query.trim().length < 2) {
            closeSearchDropdown();
            if (_isSearchMode) {
                _isSearchMode = false;
                loadCurrentLevel();
            }
            return;
        }

        _isSearchMode = true;

        var params = {
            SearchTerm: query.trim(),
            IncludeItemTypes: isAllLibraries() ? 'Series,Movie' : 'Series',
            Recursive: true,
            SortBy: 'SortName',
            SortOrder: 'Ascending',
            Fields: 'SortName',
            Limit: 100
        };

        ApiClient.getJSON(ApiClient.getUrl('Items', params))
            .then(function (response) {
                if (!_isSearchMode) return;
                var items = (response.Items || []).sort(function (a, b) {
                    return (a.Name || '').localeCompare(b.Name || '');
                });
                renderSearchDropdown(items);
            })
            .catch(function () {
                if (_isSearchMode) closeSearchDropdown();
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
        _currentEpisodeRuntimeTicks = 0;

        // Use the native Emby Items API — this is the authoritative source for chapter data
        var userId = ApiClient.getCurrentUserId();
        var capturedId = episodeId;
        ApiClient.getJSON(ApiClient.getUrl('Users/' + userId + '/Items/' + episodeId, {
            Fields: 'Chapters'
        }))
        .then(function (response) {
            if (_currentEpisodeId !== capturedId) return;
            if (response.SeriesName) {
                var sub = response.SeriesName;
                if (response.ParentIndexNumber != null) sub += ' · Season ' + response.ParentIndexNumber;
                if (response.IndexNumber != null) sub += ' · Episode ' + response.IndexNumber;
                q('chapterEpisodeSubtitle').textContent = sub;
            } else if (response.Type === 'Movie') {
                q('chapterEpisodeSubtitle').textContent = 'Movie';
                ApiClient.getJSON(ApiClient.getUrl('Items/' + capturedId + '/Ancestors', { UserId: userId }))
                    .then(function (ancestors) {
                        if (_currentEpisodeId !== capturedId) return;
                        var library = (ancestors || []).find(function (a) {
                            return a.Type === 'CollectionFolder' || a.Type === 'UserView';
                        });
                        if (library) {
                            q('chapterEpisodeSubtitle').textContent = 'Movie · ' + library.Name;
                        }
                    })
                    .catch(function () {});
            }
            if (response.RunTimeTicks) {
                _currentEpisodeRuntimeTicks = response.RunTimeTicks;
                q('chapterEpisodeRuntime').textContent = 'Runtime: ' + formatRuntime(response.RunTimeTicks);
            } else {
                _currentEpisodeRuntimeTicks = 0;
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
        row.setAttribute('data-chapter-type', markerType);

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
                '<div class="chapter-time-field">' +
                    '<input type="number" class="chapter-inp chapter-time-num chapter-hh" min="0" max="99" value="' + hh + '" />' +
                    '<span class="chapter-time-unit">H</span>' +
                '</div>' +
                '<span class="chapter-time-sep">:</span>' +
                '<div class="chapter-time-field">' +
                    '<input type="number" class="chapter-inp chapter-time-num chapter-mm" min="0" max="59" value="' + mm + '" />' +
                    '<span class="chapter-time-unit">M</span>' +
                '</div>' +
                '<span class="chapter-time-sep">:</span>' +
                '<div class="chapter-time-field">' +
                    '<input type="number" class="chapter-inp chapter-time-num chapter-ss" min="0" max="59" value="' + ss + '" />' +
                    '<span class="chapter-time-unit">S</span>' +
                '</div>' +
                '<span class="chapter-time-sep">.</span>' +
                '<div class="chapter-time-field">' +
                    '<input type="number" class="chapter-inp chapter-time-ms chapter-ms" min="0" max="999" value="' + ms + '" />' +
                    '<span class="chapter-time-unit">ms</span>' +
                '</div>' +
            '</div>' +
            '<button type="button" class="chapter-del-btn" title="Delete this chapter">⊗</button>';

        // Update row color when type changes
        row.querySelector('.chapter-row-type').addEventListener('change', function () {
            row.setAttribute('data-chapter-type', this.value);
        });

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

    function addChaptersEveryInterval() {
        if (!_currentEpisodeId) return;

        if (!_currentEpisodeRuntimeTicks) {
            toast({ type: 'warning', text: 'Episode runtime unknown — open an episode first' });
            return;
        }

        var intervalMins = parseInt(q('chapterIntervalMin').value) || 5;
        if (intervalMins < 1) intervalMins = 1;
        var prefix = q('chapterIntervalPrefix').value.trim() || 'Chapter';

        var intervalTicks = intervalMins * 60 * 10000000;
        var body = q('chapterTableBody');

        // Remove empty placeholder if present
        var placeholder = body.querySelector('div:not(.chapter-row)');
        if (placeholder) placeholder.remove();

        var count = 1;
        var t = 0;
        var added = 0;
        while (t < _currentEpisodeRuntimeTicks) {
            body.appendChild(buildChapterRow(prefix + ' ' + count, 'Chapter', t));
            count++;
            t += intervalTicks;
            added++;
        }

        markDirty();
        toast({ type: 'success', text: added + ' chapter' + (added === 1 ? '' : 's') + ' generated' });
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

        searchEl.addEventListener('blur', function () {
            setTimeout(closeSearchDropdown, 150);
        });

        // Clear search on Escape
        searchEl.addEventListener('keydown', function (e) {
            if (e.key === 'Escape') {
                closeSearchDropdown();
                searchEl.value = '';
                if (_isSearchMode) {
                    _isSearchMode = false;
                    loadCurrentLevel();
                }
            }
        });

        // All-libraries toggle — reload browser from root
        var allLibsChk = q('chkAllLibraries');
        if (allLibsChk) allLibsChk.addEventListener('change', function () {
            _navStack = [];
            _isSearchMode = false;
            searchEl.value = '';
            loadCurrentLevel();
        });

        // Filter inputs — reload the current level on any change so the list always updates
        var filterNoChaps = q('chapterFilterNoChapters');
        if (filterNoChaps) filterNoChaps.addEventListener('change', loadCurrentLevel);

        function setupNumberFilter(inputId, clearBtnId) {
            var inp = q(inputId);
            var btn = q(clearBtnId);
            if (!inp || !btn) return;
            function onInput() {
                btn.style.display = inp.value !== '' ? 'inline' : 'none';
                clearTimeout(_searchTimeout);
                _searchTimeout = setTimeout(loadCurrentLevel, 400);
            }
            inp.addEventListener('input', onInput);
            btn.addEventListener('click', function () {
                inp.value = '';
                btn.style.display = 'none';
                loadCurrentLevel();
            });
        }
        setupNumberFilter('chapterFilterMaxCount', 'chapterFilterMaxCountClear');
        setupNumberFilter('chapterFilterMinGap', 'chapterFilterMinGapClear');
        var filterIntro = q('chapterFilterIntro');
        if (filterIntro) filterIntro.addEventListener('change', loadCurrentLevel);
        var filterCredits = q('chapterFilterCredits');
        if (filterCredits) filterCredits.addEventListener('change', loadCurrentLevel);

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

        // Generate chapters by interval
        var btnInterval = q('btnAddChaptersInterval');
        if (btnInterval) btnInterval.addEventListener('click', addChaptersEveryInterval);

        // Save
        var btnSave = q('btnSaveChapters');
        if (btnSave) btnSave.addEventListener('click', saveChapters);
    }

    return { init: init };
});
