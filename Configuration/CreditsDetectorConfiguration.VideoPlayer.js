define(['dialogHelper'], function (dialogHelper) {
    'use strict';

    var TICKS_PER_SEC = 10000000;
    var PREROLL_SEC = 30;

    function getPrerollSec() {
        var stored = parseInt(localStorage.getItem('creditsDetector_prerollSeconds'), 10);
        return (stored > 0) ? stored : PREROLL_SEC;
    }

    function pad(n, num) {
        return num.toString().padStart(n, '0');
    }

    function formatTime(sec) {
        sec = Math.max(0, sec || 0);
        var h = Math.floor(sec / 3600);
        var m = Math.floor((sec % 3600) / 60);
        var s = Math.floor(sec % 60);
        var ms = Math.floor((sec % 1) * 1000);
        return pad(2, h) + ':' + pad(2, m) + ':' + pad(2, s) + '.' + pad(3, ms);
    }

    function escapeHtml(str) {
        return (str || '').replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
    }

    function stopSession(itemId, sessionId) {
        try {
            fetch(ApiClient.getUrl('Sessions/Playing/Stopped'), {
                method: 'POST',
                headers: {
                    'X-Emby-Token': ApiClient.accessToken(),
                    'Content-Type': 'application/json'
                },
                body: JSON.stringify({ ItemId: itemId, PlaySessionId: sessionId })
            }).catch(function () {});
        } catch (e) {}
    }

    function buildVideoUrl(itemId, startTicks, sessionId) {
        var path = 'Videos/' + itemId + '/stream.mp4'
            + '?StartTimeTicks=' + startTicks
            + '&VideoCodec=h264'
            + '&AudioCodec=mp3'
            + '&VideoBitrate=2000000'
            + '&AudioBitrate=128000'
            + '&allowVideoStreamCopy=false'
            + '&MaxWidth=854'
            + '&PlaySessionId=' + sessionId
            + '&api_key=' + ApiClient.accessToken()
            + '&n=' + Date.now();
        return ApiClient.getUrl(path);
    }

    /**
     * Opens a video preview popup dialog.
     *
     * @param {string} itemId              Emby item GUID for the episode
     * @param {number} startSeconds        Timestamp in seconds to seek to; stream begins PREROLL_SEC before this
     * @param {object} [opts]
     *   opts.title        {string}        Dialog header text
     *   opts.onTimestampSelected {function(seconds)}
     *       When provided, a "Set Credits Here" button is shown.
     *       The video is paused and this callback is called with the current position in
     *       seconds (relative to the full file, not the stream segment) when the user clicks it.
     */
    function openVideoDialog(itemId, startSeconds, opts) {
        opts = opts || {};
        startSeconds = Math.max(0, startSeconds || 0);

        var prerollSec = Math.max(0, startSeconds - getPrerollSec());
        var startTicks = Math.round(prerollSec * TICKS_PER_SEC);
        var sessionId = '' + Date.now();
        var videoUrl = buildVideoUrl(itemId, startTicks, sessionId);

        var hasCallback = typeof opts.onTimestampSelected === 'function';
        var dlgTitle = escapeHtml(opts.title || 'Preview');

        var dlg = dialogHelper.createDialog({ removeOnClose: true, size: 'large' });
        dlg.classList.add('ui-body-a', 'background-theme-a', 'formDialog');
        dlg.style.maxWidth = '900px';
        dlg.style.width = '95%';

        var html = '';
        html += '<div class="formDialogHeader">';
        html += '<button is="paper-icon-button-light" class="btnCancel autoSize" tabindex="-1"><i class="md-icon">&#xE5C4;</i></button>';
        html += '<h3 class="formDialogHeaderTitle">' + dlgTitle + '</h3>';
        html += '</div>';

        html += '<div class="formDialogContent" style="padding:0.5em 0.75em 0.75em;">';
        html += '<video id="cpVideoEl" style="width:100%;max-height:70vh;background:#000;display:block;" autoplay controls webkit-playsinline playsinline crossorigin="anonymous" src="' + videoUrl + '"></video>';
        html += '<div style="margin-top:0.4em;display:flex;align-items:center;gap:0.5em;flex-wrap:wrap;">';
        html += '<div id="cpTimeDisplay" style="font-family:monospace;font-size:1.05em;background:rgba(0,0,0,0.45);padding:0.3em 0.8em;border-radius:4px;min-width:13em;text-align:center;letter-spacing:0.04em;">--:--:--.---</div>';

        if (hasCallback) {
            html += '<button is="emby-button" type="button" id="cpCaptureBtn" class="raised button-submit" style="padding:0.5em 1.2em;font-size:0.92em;">';
            html += '<i class="md-icon" style="vertical-align:middle;margin-right:0.25em;">flag</i>Set Credits Here';
            html += '</button>';
        }

        html += '<button is="emby-button" type="button" class="btnCancel raised" style="padding:0.5em 1em;font-size:0.88em;margin-left:auto;">Close</button>';
        html += '</div>';

        html += '</div>';

        dlg.innerHTML = html;

        var video = dlg.querySelector('#cpVideoEl');
        var timeDisplay = dlg.querySelector('#cpTimeDisplay');
        var captureBtn = dlg.querySelector('#cpCaptureBtn');

        video.addEventListener('timeupdate', function () {
            var realSec = this.currentTime + prerollSec;
            timeDisplay.textContent = formatTime(realSec);
        });

        var seekTimer = null;
        var seekReloading = false;

        video.addEventListener('seeking', function () {
            if (seekReloading) return;
            var targetStreamTime = video.currentTime;
            var buffered = video.buffered;
            var isBuffered = false;
            for (var i = 0; i < buffered.length; i++) {
                if (targetStreamTime >= buffered.start(i) - 0.5 && targetStreamTime <= buffered.end(i) + 1) {
                    isBuffered = true;
                    break;
                }
            }
            if (!isBuffered) {
                if (seekTimer) clearTimeout(seekTimer);
                seekTimer = setTimeout(function () {
                    var realSec = video.currentTime + prerollSec;
                    prerollSec = Math.max(0, realSec - getPrerollSec());
                    var ticks = Math.round(prerollSec * TICKS_PER_SEC);
                    var targetTime = realSec - prerollSec;
                    seekReloading = true;
                    video.src = buildVideoUrl(itemId, ticks, sessionId);
                    video.addEventListener('loadedmetadata', function onMeta() {
                        video.removeEventListener('loadedmetadata', onMeta);
                        video.currentTime = targetTime;
                        video.play().catch(function () {});
                        setTimeout(function () { seekReloading = false; }, 500);
                    });
                }, 400);
            }
        });

        if (captureBtn) {
            captureBtn.addEventListener('click', function () {
                video.pause();
                var realSec = video.currentTime + prerollSec;
                dialogHelper.close(dlg);
                opts.onTimestampSelected(realSec);
            });
        }

        dlg.addEventListener('closing', function () {
            stopSession(itemId, sessionId);
        });

        dlg.querySelectorAll('.btnCancel').forEach(function (btn) {
            btn.addEventListener('click', function () { dialogHelper.close(dlg); });
        });

        dialogHelper.open(dlg);
    }

    return { openVideoDialog: openVideoDialog };
});
