define([], function () {
    'use strict';

    let rulesData = [];
    
    const detectionModeMap = {
        0: 'OcrOnly',
        1: 'HashOnly',
        2: 'OcrWithHashFallback',
        3: 'HashWithOcrFallback',
        'OcrOnly': 0,
        'HashOnly': 1,
        'OcrWithHashFallback': 2,
        'HashWithOcrFallback': 3
    };
    
    const ocrEngineMap = {
        0: 'Tesseract',
        1: 'PaddleOCR',
        'Tesseract': 0,
        'PaddleOCR': 1
    };
    
    const animeMethodMap = {
        0: 'BlackFrame',
        1: 'Ocr',
        'BlackFrame': 0,
        'Ocr': 1
    };

    function loadRules(view, config) {
        rulesData = config.DetectionRules || [];
        rulesData.forEach(rule => {
            if (!rule.Tags) rule.Tags = [];
            if (!rule.Studios) rule.Studios = [];
            
            if (typeof rule.DetectionMode === 'number') {
                rule.DetectionMode = detectionModeMap[rule.DetectionMode] || null;
            }
            if (typeof rule.OcrEngine === 'number') {
                rule.OcrEngine = ocrEngineMap[rule.OcrEngine] || null;
            }
            if (typeof rule.AnimeDetectionMethod === 'number') {
                rule.AnimeDetectionMethod = animeMethodMap[rule.AnimeDetectionMethod] || null;
            }
        });
        renderRules(view);
    }

    function saveRules(config) {
        const serializedRules = rulesData.map(rule => {
            const serialized = { ...rule };
            
            if (serialized.DetectionMode && typeof serialized.DetectionMode === 'string') {
                serialized.DetectionMode = detectionModeMap[serialized.DetectionMode];
            }
            if (serialized.OcrEngine && typeof serialized.OcrEngine === 'string') {
                serialized.OcrEngine = ocrEngineMap[serialized.OcrEngine];
            }
            if (serialized.AnimeDetectionMethod && typeof serialized.AnimeDetectionMethod === 'string') {
                serialized.AnimeDetectionMethod = animeMethodMap[serialized.AnimeDetectionMethod];
            }
            
            return serialized;
        });
        
        config.DetectionRules = serializedRules;
    }

    function renderRules(view) {
        const container = view.querySelector('#rulesContainer');
        if (!container) return;

        container.innerHTML = '';

        rulesData.forEach((rule, index) => {
            const ruleCard = createRuleCard(rule, index, view);
            container.appendChild(ruleCard);
        });
    }

    function createRuleCard(rule, index, view) {
        const template = view.querySelector('#ruleTemplate');
        const clone = template.content.cloneNode(true);
        const card = clone.querySelector('.rule-card');
        
        card.setAttribute('data-rule-id', rule.Id);
        card.setAttribute('data-rule-index', index);
        
        const nameDisplay = card.querySelector('[data-rule-name]');
        nameDisplay.textContent = rule.Name || 'New Rule';
        
        const nameInput = card.querySelector('.rule-name-input');
        nameInput.value = rule.Name || '';
        nameInput.addEventListener('input', (e) => {
            rule.Name = e.target.value;
            nameDisplay.textContent = e.target.value || 'New Rule';
        });
        
        setupTagsInput(card.querySelector('.tags-container'), rule.Tags, rule);
        setupTagsInput(card.querySelector('.studios-container'), rule.Studios, rule);
        
        setupRuleSettings(card, rule);
        
        const deleteBtn = card.querySelector('.rule-delete-btn');
        deleteBtn.addEventListener('click', () => {
            if (confirm('Are you sure you want to delete this rule?')) {
                rulesData.splice(index, 1);
                renderRules(view);
            }
        });
        
        const collapsibleHeader = card.querySelector('.collapsible-header-rules');
        const collapsibleContent = card.querySelector('.collapsible-content-rules');
        const collapseIcon = card.querySelector('.collapse-icon');
        
        collapsibleHeader.addEventListener('click', () => {
            const isHidden = collapsibleContent.style.display === 'none';
            collapsibleContent.style.display = isHidden ? 'block' : 'none';
            collapseIcon.style.transform = isHidden ? 'rotate(180deg)' : 'rotate(0deg)';
        });
        
        return card;
    }

    function setupTagsInput(container, dataArray, rule) {
        const input = container.querySelector('.tag-input');
        
        if (dataArray && dataArray.length > 0) {
            dataArray.forEach(tag => {
                addTagChip(container, tag, dataArray);
            });
        }
        
        input.addEventListener('keydown', (e) => {
            if (e.key === 'Enter' && input.value.trim()) {
                e.preventDefault();
                const tag = input.value.trim();
                if (dataArray && !dataArray.includes(tag)) {
                    dataArray.push(tag);
                    addTagChip(container, tag, dataArray);
                }
                input.value = '';
            }
        });
    }

    function addTagChip(container, tag, dataArray) {
        const input = container.querySelector('.tag-input');
        const chip = document.createElement('span');
        chip.className = 'tag-chip';
        chip.innerHTML = `
            <span>${tag}</span>
            <span class="tag-remove">×</span>
        `;
        
        chip.querySelector('.tag-remove').addEventListener('click', () => {
            const idx = dataArray.indexOf(tag);
            if (idx > -1) {
                dataArray.splice(idx, 1);
            }
            chip.remove();
        });
        
        container.insertBefore(chip, input);
    }

    function setupRuleSettings(card, rule) {
        const detectionModeSelect = card.querySelector('.rule-detection-mode');
        detectionModeSelect.value = rule.DetectionMode || '';
        detectionModeSelect.addEventListener('change', (e) => {
            rule.DetectionMode = e.target.value || null;
        });
        
        const ocrEngineSelect = card.querySelector('.rule-ocr-engine');
        ocrEngineSelect.value = rule.OcrEngine || '';
        ocrEngineSelect.addEventListener('change', (e) => {
            rule.OcrEngine = e.target.value || null;
        });
        
        setupNumberInput(card, '.rule-ocr-search-start', rule, 'OcrSearchStartValue');
        setupNumberInput(card, '.rule-ocr-minutes-from-end', rule, 'OcrMinutesFromEnd');
        setupNumberInput(card, '.rule-ocr-frame-rate', rule, 'OcrFrameRate');
        setupNumberInput(card, '.rule-ocr-minimum-matches', rule, 'OcrMinimumMatches', true);
        setupNumberInput(card, '.rule-ocr-max-frames', rule, 'OcrMaxFramesToProcess', true);
        setupNumberInput(card, '.rule-ocr-max-duration', rule, 'OcrMaxAnalysisDuration');
        setupNumberInput(card, '.rule-ocr-stop-seconds', rule, 'OcrStopSecondsFromEnd');
        setupNumberInput(card, '.rule-ocr-comparison-tolerance', rule, 'OcrEpisodeComparisonTolerance');
        setupNumberInput(card, '.rule-ocr-comparison-min-episodes', rule, 'OcrEpisodeComparisonMinimumEpisodes', true);
        setupNumberInput(card, '.rule-black-frame-percentage', rule, 'BlackFrameMinimumPercentage', true);
        setupNumberInput(card, '.rule-black-frame-threshold', rule, 'BlackFrameThreshold', true);
        setupNumberInput(card, '.rule-character-density-threshold', rule, 'OcrCharacterDensityThreshold', true);
        setupNumberInput(card, '.rule-character-density-frames', rule, 'OcrCharacterDensityConsecutiveFrames', true);
        setupNumberInput(card, '.rule-density-keyword-window', rule, 'OcrDensityKeywordWindowSeconds');
        setupNumberInput(card, '.rule-density-min-duration', rule, 'OcrDensityMinimumDurationSeconds');
        setupNumberInput(card, '.rule-density-style-threshold', rule, 'OcrDensityStyleConsistencyThreshold');
        
        const keywordsInput = card.querySelector('.rule-ocr-keywords');
        keywordsInput.value = rule.OcrDetectionKeywords || '';
        keywordsInput.addEventListener('input', (e) => {
            rule.OcrDetectionKeywords = e.target.value || null;
        });
        
        setupBooleanSelect(card, '.rule-ocr-enable-episode-comparison', rule, 'OcrEnableEpisodeComparison');
        setupBooleanSelect(card, '.rule-enable-anime-detection', rule, 'EnableAnimeDetection');
        setupBooleanSelect(card, '.rule-ocr-enable-character-density', rule, 'OcrEnableCharacterDensityDetection');
        setupBooleanSelect(card, '.rule-character-density-primary', rule, 'OcrCharacterDensityPrimaryMethod');
        setupBooleanSelect(card, '.rule-density-require-keyword', rule, 'OcrDensityRequireKeyword');
        setupBooleanSelect(card, '.rule-density-temporal-consistency', rule, 'OcrDensityRequireTemporalConsistency');
        setupBooleanSelect(card, '.rule-density-style-consistency', rule, 'OcrDensityRequireStyleConsistency');
        
        const animeMethodSelect = card.querySelector('.rule-anime-detection-method');
        animeMethodSelect.value = rule.AnimeDetectionMethod || '';
        animeMethodSelect.addEventListener('change', (e) => {
            rule.AnimeDetectionMethod = e.target.value || null;
        });
    }

    function setupNumberInput(card, selector, rule, property, isInt = false) {
        const input = card.querySelector(selector);
        if (rule[property] !== null && rule[property] !== undefined) {
            input.value = rule[property];
        }
        input.addEventListener('input', (e) => {
            if (e.target.value === '') {
                rule[property] = null;
            } else {
                const value = isInt ? parseInt(e.target.value, 10) : parseFloat(e.target.value);
                rule[property] = isNaN(value) ? null : value;
            }
        });
    }

    function setupCheckbox(card, selector, rule, property) {
        const checkbox = card.querySelector(selector);
        if (rule[property] !== null && rule[property] !== undefined) {
            checkbox.checked = rule[property];
        }
        checkbox.addEventListener('change', (e) => {
            rule[property] = e.target.checked ? true : null;
        });
    }

    function setupBooleanSelect(card, selector, rule, property) {
        const select = card.querySelector(selector);
        if (rule[property] === true) {
            select.value = 'true';
        } else if (rule[property] === false) {
            select.value = 'false';
        } else {
            select.value = '';
        }
        select.addEventListener('change', (e) => {
            if (e.target.value === 'true') {
                rule[property] = true;
            } else if (e.target.value === 'false') {
                rule[property] = false;
            } else {
                rule[property] = null;
            }
        });
    }

    function bindRulesEvents(view, instance) {
        const addBtn = view.querySelector('#btnAddRule');
        if (addBtn) {
            addBtn.addEventListener('click', () => {
                const newRule = {
                    Id: generateGuid(),
                    Name: '',
                    Tags: [],
                    Studios: []
                };
                rulesData.push(newRule);
                renderRules(view);
            });
        }
        
        const saveBtn = view.querySelector('#btnSaveRules');
        if (saveBtn) {
            saveBtn.addEventListener('click', () => {
                if (instance && instance.config) {
                    saveRules(instance.config);
                    require(['configurationpage?name=CreditsDetectorConfigurationDataManager'], (dataManager) => {
                        dataManager.saveData(instance, view);
                    });
                }
            });
        }
    }

    function generateGuid() {
        return 'xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx'.replace(/[xy]/g, function(c) {
            const r = Math.random() * 16 | 0;
            const v = c === 'x' ? r : (r & 0x3 | 0x8);
            return v.toString(16);
        });
    }

    return {
        loadRules: loadRules,
        saveRules: saveRules,
        bindRulesEvents: bindRulesEvents,
        getRulesData: function() { return rulesData; }
    };
});
