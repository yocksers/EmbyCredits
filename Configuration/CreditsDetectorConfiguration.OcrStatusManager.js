define([], function () {
    'use strict';

    const GREEN = '#52B54B';
    const YELLOW = '#FDD835';
    const RED = '#E53935';

    const OCR_MODES = ['OcrOnly', 'OcrWithHashFallback', 'HashWithOcrFallback'];

    let indicatorInterval = null;
    let guideStatusInterval = null;

    function setDot(dotEl, textEl, color, text) {
        if (dotEl) dotEl.style.background = color;
        if (textEl) textEl.textContent = text;
    }

    function fetchEngineStatus(ocrEngine, ocrEndpoint) {
        return ApiClient.fetch({
            type: 'POST',
            url: ApiClient.getUrl('CreditsDetector/GetOcrEngineStatus'),
            dataType: 'json',
            contentType: 'application/json',
            data: JSON.stringify({ OcrEndpoint: ocrEndpoint || '', OcrEngine: ocrEngine || 'Tesseract' })
        });
    }

    function refreshStartIndicator(view) {
        const indicator = view.querySelector('#ocrEngineReadyIndicator');
        const dot = view.querySelector('#ocrEngineReadyDot');
        const text = view.querySelector('#ocrEngineReadyText');
        if (!indicator || !dot || !text) return;

        const detectionMode = view.querySelector('#selectDetectionMode');
        const mode = detectionMode ? detectionMode.value : '';

        if (OCR_MODES.indexOf(mode) === -1) {
            indicator.style.display = 'none';
            return;
        }

        indicator.style.display = 'flex';

        const ocrEngineSelect = view.querySelector('#selectOcrEngine');
        const ocrEngine = ocrEngineSelect ? ocrEngineSelect.value : 'Tesseract';
        const isLocal = ocrEngine === 'LocalTesseract';
        const txtOcrEndpoint = view.querySelector('#txtOcrEndpoint');
        const ocrEndpoint = isLocal ? '' : (txtOcrEndpoint ? txtOcrEndpoint.value : '');

        if (!isLocal && !ocrEndpoint) {
            setDot(dot, text, RED, 'OCR engine: no endpoint configured (see Settings tab)');
            return;
        }

        setDot(dot, text, YELLOW, 'Checking OCR engine status...');

        fetchEngineStatus(ocrEngine, ocrEndpoint).then(response => {
            if (response && response.Success) {
                setDot(dot, text, GREEN, `OCR engine ready (${ocrEngine})`);
            } else {
                setDot(dot, text, RED, `OCR engine not ready: ${(response && response.Message) || 'unreachable'}`);
            }
        }).catch(() => {
            setDot(dot, text, RED, 'OCR engine not ready: request failed');
        });
    }

    function startIndicatorPolling(view) {
        stopIndicatorPolling();
        refreshStartIndicator(view);
        indicatorInterval = setInterval(() => refreshStartIndicator(view), 20000);
    }

    function stopIndicatorPolling() {
        if (indicatorInterval) {
            clearInterval(indicatorInterval);
            indicatorInterval = null;
        }
    }

    function engineDetails(engine) {
        return engine === 'PaddleOCR'
            ? { image: 'yock1/embycreditpaddle', containerName: 'paddleocr', serviceName: 'paddleocr', volumePath: '/root/.paddleocr' }
            : { image: 'yock1/embycreditocr', containerName: 'tesseract-ocr', serviceName: 'tesseract', volumePath: null };
    }

    function buildRunCommand(engine, gpu, port, persist) {
        const details = engineDetails(engine);
        const parts = ['docker run -d'];
        if (engine === 'PaddleOCR' && gpu) parts.push('--gpus all');
        parts.push(`--name ${details.containerName}`);
        parts.push(`-p ${port}:8884`);
        if (engine === 'PaddleOCR' && persist && details.volumePath) {
            parts.push(`-v ./${details.serviceName}-models:${details.volumePath}`);
        }
        parts.push('--restart unless-stopped');
        parts.push(details.image);
        return parts.join(' ');
    }

    function buildComposeCommand(engine, gpu, port, persist) {
        const details = engineDetails(engine);
        const lines = [
            `version: '3.8'`,
            `services:`,
            `  ${details.serviceName}:`,
            `    image: ${details.image}`,
            `    container_name: ${details.containerName}`,
            `    ports:`,
            `      - "${port}:8884"`
        ];
        if (engine === 'PaddleOCR' && persist && details.volumePath) {
            lines.push(`    volumes:`);
            lines.push(`      - ./${details.serviceName}-models:${details.volumePath}`);
        }
        if (engine === 'PaddleOCR' && gpu) {
            lines.push(`    deploy:`);
            lines.push(`      resources:`);
            lines.push(`        reservations:`);
            lines.push(`          devices:`);
            lines.push(`            - driver: nvidia`);
            lines.push(`              count: all`);
            lines.push(`              capabilities: [gpu]`);
        }
        lines.push(`    restart: unless-stopped`);
        return lines.join('\n');
    }

    function regenerateDockerCommands(view) {
        const engineSelect = view.querySelector('#selectDockerGenEngine');
        const gpuCheckbox = view.querySelector('#chkDockerGenGpu');
        const gpuContainer = view.querySelector('#dockerGenGpuContainer');
        const persistCheckbox = view.querySelector('#chkDockerGenPersist');
        const portInput = view.querySelector('#txtDockerGenPort');
        const runOutput = view.querySelector('#dockerGenRunCommand');
        const composeOutput = view.querySelector('#dockerGenComposeCommand');

        if (!engineSelect || !runOutput || !composeOutput) return;

        const engine = engineSelect.value;
        const isPaddle = engine === 'PaddleOCR';
        if (gpuContainer) gpuContainer.style.display = isPaddle ? '' : 'none';

        const gpu = isPaddle && gpuCheckbox ? gpuCheckbox.checked : false;
        const persist = isPaddle && persistCheckbox ? persistCheckbox.checked : false;
        const port = (portInput && portInput.value) ? portInput.value : '8884';

        runOutput.textContent = buildRunCommand(engine, gpu, port, persist);
        composeOutput.textContent = buildComposeCommand(engine, gpu, port, persist);
    }

    function copyToClipboard(text) {
        if (navigator.clipboard && navigator.clipboard.writeText) {
            navigator.clipboard.writeText(text).catch(() => fallbackCopy(text));
        } else {
            fallbackCopy(text);
        }
    }

    function fallbackCopy(text) {
        const textarea = document.createElement('textarea');
        textarea.value = text;
        textarea.style.position = 'fixed';
        textarea.style.opacity = '0';
        document.body.appendChild(textarea);
        textarea.select();
        try { document.execCommand('copy'); } catch (e) { /* ignore */ }
        document.body.removeChild(textarea);
    }

    function refreshGuideStatus(view) {
        const dot = view.querySelector('#dockerGenStatusDot');
        const text = view.querySelector('#dockerGenStatusText');
        const engineSelect = view.querySelector('#selectDockerGenEngine');
        const portInput = view.querySelector('#txtDockerGenPort');
        if (!dot || !text || !engineSelect) return;

        const engine = engineSelect.value;
        const port = (portInput && portInput.value) ? portInput.value : '8884';
        const endpoint = `http://localhost:${port}`;

        setDot(dot, text, YELLOW, 'Checking readiness...');

        fetchEngineStatus(engine, endpoint).then(response => {
            if (response && response.Success) {
                setDot(dot, text, GREEN, `Ready - reachable at ${endpoint}`);
            } else {
                setDot(dot, text, RED, `Not reachable yet at ${endpoint} (${(response && response.Message) || 'no response'})`);
            }
        }).catch(() => {
            setDot(dot, text, RED, `Not reachable yet at ${endpoint}`);
        });
    }

    function startGuideStatusPolling(view) {
        stopGuideStatusPolling();
        refreshGuideStatus(view);
        guideStatusInterval = setInterval(() => refreshGuideStatus(view), 5000);
    }

    function stopGuideStatusPolling() {
        if (guideStatusInterval) {
            clearInterval(guideStatusInterval);
            guideStatusInterval = null;
        }
    }

    function initDockerGenerator(view) {
        const engineSelect = view.querySelector('#selectDockerGenEngine');
        const gpuCheckbox = view.querySelector('#chkDockerGenGpu');
        const persistCheckbox = view.querySelector('#chkDockerGenPersist');
        const portInput = view.querySelector('#txtDockerGenPort');
        const btnCopyRun = view.querySelector('#btnCopyDockerRun');
        const btnCopyCompose = view.querySelector('#btnCopyDockerCompose');

        if (!engineSelect) return;

        const regenerate = () => {
            regenerateDockerCommands(view);
            startGuideStatusPolling(view);
        };

        engineSelect.addEventListener('change', regenerate);
        if (gpuCheckbox) gpuCheckbox.addEventListener('change', () => regenerateDockerCommands(view));
        if (persistCheckbox) persistCheckbox.addEventListener('change', () => regenerateDockerCommands(view));
        if (portInput) portInput.addEventListener('change', regenerate);

        if (btnCopyRun) {
            btnCopyRun.addEventListener('click', () => {
                const el = view.querySelector('#dockerGenRunCommand');
                if (el) copyToClipboard(el.textContent);
            });
        }
        if (btnCopyCompose) {
            btnCopyCompose.addEventListener('click', () => {
                const el = view.querySelector('#dockerGenComposeCommand');
                if (el) copyToClipboard(el.textContent);
            });
        }

        regenerateDockerCommands(view);
        startGuideStatusPolling(view);
    }

    function init(view) {
        initDockerGenerator(view);
        startIndicatorPolling(view);

        const detectionModeSelect = view.querySelector('#selectDetectionMode');
        if (detectionModeSelect) {
            detectionModeSelect.addEventListener('change', () => refreshStartIndicator(view));
        }
        const ocrEngineSelect = view.querySelector('#selectOcrEngine');
        if (ocrEngineSelect) {
            ocrEngineSelect.addEventListener('change', () => refreshStartIndicator(view));
        }
        const txtOcrEndpoint = view.querySelector('#txtOcrEndpoint');
        if (txtOcrEndpoint) {
            txtOcrEndpoint.addEventListener('change', () => refreshStartIndicator(view));
        }
    }

    function destroy() {
        stopIndicatorPolling();
        stopGuideStatusPolling();
    }

    return {
        init: init,
        destroy: destroy
    };
});
