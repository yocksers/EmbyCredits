define(['loading', 'toast'], function (loading, toast) {
    'use strict';

    var _view = null;
    var _refreshInterval = null;
    var pluginId = 'b1a65a73-a620-432a-9f5b-285038031c26';

    function q(id) { return _view.querySelector('#' + id); }

    function formatDate(isoStr) {
        try {
            var d = new Date(isoStr);
            return d.toLocaleDateString() + ' ' + d.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
        } catch (e) {
            return '';
        }
    }

    function escapeHtml(str) {
        return (str || '').replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
    }

    function saveTracerSetting() {
        var pluginId = 'b1a65a73-a620-432a-9f5b-285038031c26';
        ApiClient.getPluginConfiguration(pluginId).then(function (cfg) {
            cfg.EnableTracerMode = q('chkEnableTracerMode').checked;
            ApiClient.updatePluginConfiguration(pluginId, cfg).then(function () {
                refresh();
            }).catch(function () {
                toast({ type: 'error', text: 'Failed to save setting' });
            });
        }).catch(function () {
            toast({ type: 'error', text: 'Failed to load configuration' });
        });
    }

    function fetchEpisodes() {
        return ApiClient.getJSON(ApiClient.getUrl('CreditsDetector/GetTracerEpisodes'));
    }

    function refresh() {
        var chkEnable = q('chkEnableTracerMode');
        var enabled = chkEnable ? chkEnable.checked : false;
        var disabledNotice = q('tracerDisabledNotice');
        var countLine     = q('tracerCountLine');
        var listEl        = q('tracerEpisodeList');
        var btnRunAll     = q('btnTracerRunAll');
        var btnClear      = q('btnTracerClear');
        var detectedSection = q('tracerDetectedSection');
        var failedSection = q('tracerFailedSection');

        disabledNotice.style.display = enabled ? 'none' : 'block';
        if (btnRunAll) btnRunAll.disabled = !enabled;
        if (btnClear)  btnClear.disabled  = !enabled;

        if (!enabled) {
            listEl.innerHTML = '<div id="tracerEmptyMsg" style="text-align:center;padding:2.5em 1em;opacity:0.38;font-size:0.9em;">Tracer is disabled.</div>';
            countLine.style.display = 'none';
            if (detectedSection) detectedSection.style.display = 'none';
            if (failedSection) failedSection.style.display = 'none';
            return;
        }

        fetchEpisodes()
            .then(function (result) {
                var episodes = result.Episodes || [];
                var detected = result.Detected || [];
                var failed = result.Failed || [];

                if (episodes.length === 0) {
                    listEl.innerHTML = '<div id="tracerEmptyMsg" style="text-align:center;padding:2.5em 1em;opacity:0.38;font-size:0.9em;">No pending episodes — all caught up!</div>';
                    countLine.style.display = 'none';
                } else {
                    countLine.textContent = episodes.length + ' episode' + (episodes.length === 1 ? '' : 's') + ' pending detection';
                    countLine.style.display = 'block';

                    listEl.innerHTML = '';
                    episodes.forEach(function (ep) {
                        var seLabel = 'S' + String(ep.SeasonNumber).padStart(2, '0') + 'E' + String(ep.EpisodeNumber).padStart(2, '0');
                        var item = document.createElement('div');
                        item.className = 'tracer-item';
                        item.dataset.episodeId = ep.EpisodeId;
                        item.innerHTML =
                            '<div class="tracer-item-label">' +
                                '<span class="tracer-item-series">' + escapeHtml(ep.SeriesName) + '</span>' +
                                '<span class="tracer-item-ep">' + seLabel + (ep.EpisodeName ? ' \u2014 ' + escapeHtml(ep.EpisodeName) : '') + '</span>' +
                            '</div>' +
                            '<span class="tracer-item-added">' + formatDate(ep.AddedUtc) + '</span>' +
                            '<div class="tracer-item-actions">' +
                                '<button is="emby-button" type="button" class="raised button-submit tracer-run-btn" title="Run detection for this episode">' +
                                    '<i class="md-icon" style="font-size:1em;vertical-align:middle;">play_arrow</i>' +
                                '</button>' +
                                '<button is="emby-button" type="button" class="raised tracer-dismiss-btn" title="Dismiss (remove from list without detecting)">' +
                                    '<i class="md-icon" style="font-size:1em;vertical-align:middle;">close</i>' +
                                '</button>' +
                            '</div>';

                        item.querySelector('.tracer-run-btn').addEventListener('click', function () {
                            runEpisode(ep.EpisodeId, item);
                        });

                        item.querySelector('.tracer-dismiss-btn').addEventListener('click', function () {
                            dismissEpisode(ep.EpisodeId, item);
                        });

                        listEl.appendChild(item);
                    });
                }

                // Render detected history section
                if (detectedSection) {
                    if (detected.length === 0) {
                        detectedSection.style.display = 'none';
                    } else {
                        detectedSection.style.display = 'block';
                        var detectedList = q('tracerDetectedList');
                        var detectedCount = q('tracerDetectedCount');
                        if (detectedCount) {
                            detectedCount.textContent = detected.length + ' episode' + (detected.length === 1 ? '' : 's') + ' automatically detected';
                        }
                        if (detectedList) {
                            detectedList.innerHTML = '';
                            detected.forEach(function (ep) {
                                var seLabel = 'S' + String(ep.SeasonNumber).padStart(2, '0') + 'E' + String(ep.EpisodeNumber).padStart(2, '0');
                                var item = document.createElement('div');
                                item.className = 'tracer-item tracer-detected-item';
                                item.innerHTML =
                                    '<i class="md-icon tracer-detected-icon">check_circle</i>' +
                                    '<div class="tracer-item-label">' +
                                        '<span class="tracer-item-series">' + escapeHtml(ep.SeriesName) + '</span>' +
                                        '<span class="tracer-item-ep">' + seLabel + (ep.EpisodeName ? ' \u2014 ' + escapeHtml(ep.EpisodeName) : '') + '</span>' +
                                    '</div>' +
                                    '<span class="tracer-item-added" title="Detected at">' + formatDate(ep.DetectedUtc) + '</span>';
                                detectedList.appendChild(item);
                            });
                        }
                    }
                }

                // Render failed history section
                if (failedSection) {
                    if (failed.length === 0) {
                        failedSection.style.display = 'none';
                    } else {
                        failedSection.style.display = 'block';
                        var failedList = q('tracerFailedList');
                        var failedCount = q('tracerFailedCount');
                        if (failedCount) {
                            failedCount.textContent = failed.length + ' episode' + (failed.length === 1 ? '' : 's') + ' failed detection';
                        }
                        if (failedList) {
                            failedList.innerHTML = '';
                            failed.forEach(function (ep) {
                                var seLabel = 'S' + String(ep.SeasonNumber).padStart(2, '0') + 'E' + String(ep.EpisodeNumber).padStart(2, '0');
                                var item = document.createElement('div');
                                item.className = 'tracer-item tracer-failed-item';
                                item.dataset.episodeId = ep.EpisodeId;
                                item.innerHTML =
                                    '<i class="md-icon tracer-failed-icon">error</i>' +
                                    '<div class="tracer-item-label">' +
                                        '<span class="tracer-item-series">' + escapeHtml(ep.SeriesName) + '</span>' +
                                        '<span class="tracer-item-ep">' + seLabel + (ep.EpisodeName ? ' \u2014 ' + escapeHtml(ep.EpisodeName) : '') + '</span>' +
                                        (ep.FailureReason ? '<span class="tracer-failed-reason">' + escapeHtml(ep.FailureReason) + '</span>' : '') +
                                    '</div>' +
                                    '<span class="tracer-item-added" title="Failed at">' + formatDate(ep.FailedUtc) + '</span>' +
                                    '<div class="tracer-item-actions">' +
                                        '<button is="emby-button" type="button" class="raised button-submit tracer-run-btn" title="Retry detection">' +
                                            '<i class="md-icon" style="font-size:1em;vertical-align:middle;">replay</i>' +
                                        '</button>' +
                                    '</div>';

                                item.querySelector('.tracer-run-btn').addEventListener('click', function () {
                                    retryFailed(ep.EpisodeId, item);
                                });

                                failedList.appendChild(item);
                            });
                        }
                    }
                }
            })
            .catch(function () {
                listEl.innerHTML = '<div style="text-align:center;padding:2em;color:#ef9a9a;font-size:0.88em;">Failed to load tracer episodes.</div>';
                countLine.style.display = 'none';
            });
    }

    function runEpisode(episodeId, itemEl) {
        var btn = itemEl.querySelector('.tracer-run-btn');
        if (btn) { btn.disabled = true; btn.innerHTML = '<i class="md-icon" style="font-size:1em;vertical-align:middle;">hourglass_empty</i>'; }

        fetch(ApiClient.getUrl('CreditsDetector/ProcessEpisode'), {
            method: 'POST',
            headers: {
                'X-Emby-Token': ApiClient.accessToken(),
                'Content-Type': 'application/json'
            },
            body: JSON.stringify({ ItemId: episodeId, SkipExistingMarkers: false })
        })
        .then(function (r) { return r.json(); })
        .then(function (result) {
            if (result.Success) {
                toast({ type: 'success', text: 'Detection queued' });
                // Dismiss from list optimistically — detection service will also call MarkDetected
                dismissEpisode(episodeId, itemEl);
            } else {
                toast({ type: 'error', text: 'Failed: ' + (result.Message || 'Unknown error') });
                if (btn) { btn.disabled = false; btn.innerHTML = '<i class="md-icon" style="font-size:1em;vertical-align:middle;">play_arrow</i>'; }
            }
        })
        .catch(function () {
            toast({ type: 'error', text: 'Request failed' });
            if (btn) { btn.disabled = false; btn.innerHTML = '<i class="md-icon" style="font-size:1em;vertical-align:middle;">play_arrow</i>'; }
        });
    }

    function dismissEpisode(episodeId, itemEl) {
        fetch(ApiClient.getUrl('CreditsDetector/DismissTracerEpisode'), {
            method: 'POST',
            headers: {
                'X-Emby-Token': ApiClient.accessToken(),
                'Content-Type': 'application/json'
            },
            body: JSON.stringify({ EpisodeId: episodeId })
        })
        .then(function () {
            itemEl.remove();
            var remaining = q('tracerEpisodeList').querySelectorAll('.tracer-item').length;
            var countLine = q('tracerCountLine');
            if (remaining === 0) {
                q('tracerEpisodeList').innerHTML = '<div style="text-align:center;padding:2.5em 1em;opacity:0.38;font-size:0.9em;">No pending episodes — all caught up!</div>';
                countLine.style.display = 'none';
            } else {
                countLine.textContent = remaining + ' episode' + (remaining === 1 ? '' : 's') + ' pending detection';
            }
        })
        .catch(function () {
            toast({ type: 'error', text: 'Failed to dismiss episode' });
        });
    }

    function runAll() {
        fetchEpisodes().then(function (result) {
            var episodes = result.Episodes || [];
            if (episodes.length === 0) {
                toast({ type: 'warning', text: 'No pending episodes' });
                return;
            }

            loading.show();
            var ids = episodes.map(function (e) { return e.EpisodeId; });

            // Queue all via ProcessEpisode sequentially to avoid hammering the server
            var chain = Promise.resolve();
            ids.forEach(function (id) {
                chain = chain.then(function () {
                    return fetch(ApiClient.getUrl('CreditsDetector/ProcessEpisode'), {
                        method: 'POST',
                        headers: {
                            'X-Emby-Token': ApiClient.accessToken(),
                            'Content-Type': 'application/json'
                        },
                        body: JSON.stringify({ ItemId: id, SkipExistingMarkers: false })
                    });
                });
            });

            chain.then(function () {
                loading.hide();
                toast({ type: 'success', text: 'Queued ' + ids.length + ' episode(s) for detection' });
                // Clear the list in the UI — the backend will also clean up as each finishes
                fetch(ApiClient.getUrl('CreditsDetector/ClearTracerList'), {
                    method: 'POST',
                    headers: { 'X-Emby-Token': ApiClient.accessToken(), 'Content-Type': 'application/json' },
                    body: '{}'
                }).then(function () { refresh(); });
            }).catch(function () {
                loading.hide();
                toast({ type: 'error', text: 'One or more episodes failed to queue' });
                refresh();
            });
        }).catch(function () {
            toast({ type: 'error', text: 'Failed to fetch episodes' });
        });
    }

    function clearAll() {
        if (!confirm('Remove all episodes from the tracer list? This will not delete any data.')) return;
        fetch(ApiClient.getUrl('CreditsDetector/ClearTracerList'), {
            method: 'POST',
            headers: { 'X-Emby-Token': ApiClient.accessToken(), 'Content-Type': 'application/json' },
            body: '{}'
        })
        .then(function () { toast({ type: 'success', text: 'Tracer list cleared' }); refresh(); })
        .catch(function () { toast({ type: 'error', text: 'Failed to clear list' }); });
    }

    function clearDetectedHistory() {
        if (!confirm('Clear the detected history list?')) return;
        fetch(ApiClient.getUrl('CreditsDetector/ClearDetectedTracerList'), {
            method: 'POST',
            headers: { 'X-Emby-Token': ApiClient.accessToken(), 'Content-Type': 'application/json' },
            body: '{}'
        })
        .then(function () { toast({ type: 'success', text: 'Detected history cleared' }); refresh(); })
        .catch(function () { toast({ type: 'error', text: 'Failed to clear detected history' }); });
    }

    function clearFailedHistory() {
        if (!confirm('Clear the failed detection history list?')) return;
        fetch(ApiClient.getUrl('CreditsDetector/ClearFailedTracerList'), {
            method: 'POST',
            headers: { 'X-Emby-Token': ApiClient.accessToken(), 'Content-Type': 'application/json' },
            body: '{}'
        })
        .then(function () { toast({ type: 'success', text: 'Failed history cleared' }); refresh(); })
        .catch(function () { toast({ type: 'error', text: 'Failed to clear failed history' }); });
    }

    function retryFailed(episodeId, itemEl) {
        var btn = itemEl.querySelector('.tracer-run-btn');
        if (btn) { btn.disabled = true; btn.innerHTML = '<i class="md-icon" style="font-size:1em;vertical-align:middle;">hourglass_empty</i>'; }

        fetch(ApiClient.getUrl('CreditsDetector/ProcessEpisode'), {
            method: 'POST',
            headers: {
                'X-Emby-Token': ApiClient.accessToken(),
                'Content-Type': 'application/json'
            },
            body: JSON.stringify({ ItemId: episodeId, SkipExistingMarkers: false })
        })
        .then(function (r) { return r.json(); })
        .then(function (result) {
            if (result.Success) {
                toast({ type: 'success', text: 'Detection queued' });
                itemEl.remove();
                var remaining = q('tracerFailedList').querySelectorAll('.tracer-failed-item').length;
                if (remaining === 0) q('tracerFailedSection').style.display = 'none';
            } else {
                toast({ type: 'error', text: 'Failed: ' + (result.Message || 'Unknown error') });
                if (btn) { btn.disabled = false; btn.innerHTML = '<i class="md-icon" style="font-size:1em;vertical-align:middle;">replay</i>'; }
            }
        })
        .catch(function () {
            toast({ type: 'error', text: 'Request failed' });
            if (btn) { btn.disabled = false; btn.innerHTML = '<i class="md-icon" style="font-size:1em;vertical-align:middle;">replay</i>'; }
        });
    }

    function init(view) {
        _view = view;

        var btnRefresh = q('btnTracerRefresh');
        if (btnRefresh) btnRefresh.addEventListener('click', refresh);

        var btnRunAll = q('btnTracerRunAll');
        if (btnRunAll) btnRunAll.addEventListener('click', runAll);

        var btnClear = q('btnTracerClear');
        if (btnClear) btnClear.addEventListener('click', clearAll);

        var btnClearDetected = q('btnTracerClearDetected');
        if (btnClearDetected) btnClearDetected.addEventListener('click', clearDetectedHistory);

        var btnClearFailed = q('btnTracerClearFailed');
        if (btnClearFailed) btnClearFailed.addEventListener('click', clearFailedHistory);

        var btnSave = q('btnTracerSave');
        if (btnSave) btnSave.addEventListener('click', saveTracerSetting);

        // Re-render immediately when the toggle is flipped
        var chkEnable = q('chkEnableTracerMode');
        if (chkEnable) chkEnable.addEventListener('change', refresh);

        // Fetch config to correctly set the checkbox before the first render,
        // since loadData() is async and may not have run yet
        ApiClient.getPluginConfiguration(pluginId).then(function (cfg) {
            var chk = q('chkEnableTracerMode');
            if (chk) chk.checked = cfg.EnableTracerMode || false;
            refresh();
        }).catch(function () {
            refresh();
        });

        // Auto-refresh every 30 s while on this tab
        if (_refreshInterval) clearInterval(_refreshInterval);
        _refreshInterval = setInterval(refresh, 30000);
    }

    function destroy() {
        if (_refreshInterval) {
            clearInterval(_refreshInterval);
            _refreshInterval = null;
        }
    }

    return { init: init, destroy: destroy };
});
