define([], function () {
    'use strict';

    function escapeHtml(str) {
        return (str || '').replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
    }

    let rulesData = [];
    let cachedLibraries = [];
    let cachedSeriesNames = [];
    
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
            if (!rule.SeriesNames) rule.SeriesNames = [];
            if (!rule.LibraryIds) rule.LibraryIds = [];
            
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
        var librariesPromise = ApiClient.getJSON(ApiClient.getUrl('Library/MediaFolders')).then(function (response) {
            cachedLibraries = (response.Items || []).filter(function (lib) {
                return lib.CollectionType === 'tvshows' || lib.CollectionType === 'mixed' || !lib.CollectionType;
            }).sort(function (a, b) { return a.Name.localeCompare(b.Name); });
        }).catch(function () {});

        var seriesPromise = ApiClient.getJSON(ApiClient.getUrl('CreditsDetector/GetAllSeries')).then(function (response) {
            cachedSeriesNames = (response.Series || []).map(function (s) { return s.Name; }).sort(function (a, b) { return a.localeCompare(b); });
        }).catch(function () {});

        Promise.all([librariesPromise, seriesPromise]).then(function () {
            renderRules(view);
        });
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

    function checkRuleWarning(rule, card) {
        var warningEl = card.querySelector('.rule-warning');
        if (!warningEl) return;

        var warnings = [];

        var hasSeriesNames = rule.SeriesNames && rule.SeriesNames.length > 0;
        var hasTags = rule.Tags && rule.Tags.length > 0;
        var hasStudios = rule.Studios && rule.Studios.length > 0;
        var hasLibraries = rule.LibraryIds && rule.LibraryIds.length > 0;
        var hasPrimary = hasSeriesNames || hasTags || hasStudios;

        // Rule has no matchers at all — will never apply
        if (!hasPrimary && !hasLibraries) {
            warnings.push('This rule has no series names, tags, studios, or libraries set and will <strong>never match</strong> any show.');
        }

        // Library-only rule with DisableDetection
        if (!hasPrimary && hasLibraries && rule.DisableDetection === true) {
            warnings.push('This rule will disable detection for <strong>every show</strong> in the selected librar' + (rule.LibraryIds.length > 1 ? 'ies' : 'y') + '.');
        }

        // Library-only rule without DisableDetection — broad but not necessarily harmful, still worth a note
        if (!hasPrimary && hasLibraries && rule.DisableDetection !== true) {
            warnings.push('This rule has no series names, tags, or studios set, so it will apply to <strong>every show</strong> in the selected librar' + (rule.LibraryIds.length > 1 ? 'ies' : 'y') + '.');
        }

        // Rule has matchers but also DisableDetection — just a sanity reminder
        if (hasPrimary && hasLibraries && rule.DisableDetection === true) {
            warnings.push('Detection will be disabled only for matching shows that are also inside the selected librar' + (rule.LibraryIds.length > 1 ? 'ies' : 'y') + '.');
        }

        if (warnings.length > 0) {
            warningEl.innerHTML = warnings.join(' ');
            warningEl.style.display = 'block';
        } else {
            warningEl.style.display = 'none';
        }
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
        
        const onMatcherChange = function() { checkRuleWarning(rule, card); };
        setupTagsInput(card.querySelector('.tags-container'), rule.Tags, rule, onMatcherChange);
        setupTagsInput(card.querySelector('.studios-container'), rule.Studios, rule, onMatcherChange);
        setupSeriesNamesInput(card, rule, onMatcherChange);
        setupLibrariesInput(card.querySelector('.rule-libraries-container'), rule, function() { checkRuleWarning(rule, card); });
        
        setupRuleSettings(card, rule, function() { checkRuleWarning(rule, card); });
        
        checkRuleWarning(rule, card);
        
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

    function setupSeriesNamesInput(card, rule, onChangeCallback) {
        var wrapper = card.querySelector('.series-names-container').parentElement;
        wrapper.classList.add('series-names-wrapper');

        var container = card.querySelector('.series-names-container');
        setupTagsInput(container, rule.SeriesNames, rule, onChangeCallback);

        var input = container.querySelector('.tag-input');

        var suggestionList = document.createElement('div');
        suggestionList.className = 'series-suggestion-list';
        suggestionList.style.display = 'none';
        wrapper.appendChild(suggestionList);

        var activeIndex = -1;

        function showSuggestions(query) {
            suggestionList.innerHTML = '';
            activeIndex = -1;
            if (!query) {
                suggestionList.style.display = 'none';
                return;
            }
            var lower = query.toLowerCase();
            var matches = cachedSeriesNames.filter(function (name) {
                return name.toLowerCase().includes(lower) && !rule.SeriesNames.includes(name);
            }).slice(0, 10);
            if (matches.length === 0) {
                suggestionList.style.display = 'none';
                return;
            }
            matches.forEach(function (name) {
                var item = document.createElement('div');
                item.className = 'series-suggestion-item';
                item.textContent = name;
                item.addEventListener('mousedown', function (e) {
                    e.preventDefault();
                    selectSuggestion(name);
                });
                suggestionList.appendChild(item);
            });
            suggestionList.style.display = 'block';
        }

        function selectSuggestion(name) {
            if (!rule.SeriesNames.includes(name)) {
                rule.SeriesNames.push(name);
                addTagChip(container, name, rule.SeriesNames, onChangeCallback);
                if (onChangeCallback) onChangeCallback();
            }
            input.value = '';
            suggestionList.style.display = 'none';
            activeIndex = -1;
        }

        input.addEventListener('input', function () {
            showSuggestions(input.value.trim());
        });

        input.addEventListener('focus', function () {
            if (input.value.trim()) showSuggestions(input.value.trim());
        });

        input.addEventListener('keydown', function (e) {
            var items = suggestionList.querySelectorAll('.series-suggestion-item');
            if (suggestionList.style.display === 'none' || items.length === 0) return;
            if (e.key === 'ArrowDown') {
                e.preventDefault();
                activeIndex = Math.min(activeIndex + 1, items.length - 1);
                items.forEach(function (el, i) { el.classList.toggle('is-active', i === activeIndex); });
            } else if (e.key === 'ArrowUp') {
                e.preventDefault();
                activeIndex = Math.max(activeIndex - 1, 0);
                items.forEach(function (el, i) { el.classList.toggle('is-active', i === activeIndex); });
            } else if (e.key === 'Enter' && activeIndex >= 0) {
                e.preventDefault();
                e.stopPropagation();
                selectSuggestion(items[activeIndex].textContent);
            } else if (e.key === 'Escape') {
                suggestionList.style.display = 'none';
            }
        });

        document.addEventListener('click', function hideSuggestions(e) {
            if (!wrapper.contains(e.target)) {
                suggestionList.style.display = 'none';
            }
        });
    }

    function setupLibrariesInput(container, rule, onChangeCallback) {
        container.innerHTML = '';
        if (cachedLibraries.length === 0) {
            var msg = document.createElement('div');
            msg.style.opacity = '0.6';
            msg.style.fontSize = '0.9em';
            msg.textContent = 'No libraries available';
            container.appendChild(msg);
            return;
        }
        cachedLibraries.forEach(function (library) {
            var div = document.createElement('div');
            div.className = 'checkboxContainer';
            var checked = rule.LibraryIds && rule.LibraryIds.includes(library.Id);
            div.innerHTML = '<label><input is="emby-checkbox" type="checkbox" data-library-id="' + escapeHtml(library.Id) + '" ' + (checked ? 'checked' : '') + ' /><span>' + escapeHtml(library.Name) + '</span></label>';
            div.querySelector('input').addEventListener('change', function (e) {
                if (!rule.LibraryIds) rule.LibraryIds = [];
                if (e.target.checked) {
                    if (!rule.LibraryIds.includes(library.Id)) {
                        rule.LibraryIds.push(library.Id);
                    }
                } else {
                    var idx = rule.LibraryIds.indexOf(library.Id);
                    if (idx > -1) rule.LibraryIds.splice(idx, 1);
                }
                if (onChangeCallback) onChangeCallback();
            });
            container.appendChild(div);
        });
    }

    function setupTagsInput(container, dataArray, rule, onChangeCallback) {
        const input = container.querySelector('.tag-input');
        
        if (dataArray && dataArray.length > 0) {
            dataArray.forEach(tag => {
                addTagChip(container, tag, dataArray, onChangeCallback);
            });
        }
        
        input.addEventListener('keydown', (e) => {
            if (e.key === 'Enter' && input.value.trim()) {
                e.preventDefault();
                const tag = input.value.trim();
                if (dataArray && !dataArray.includes(tag)) {
                    dataArray.push(tag);
                    addTagChip(container, tag, dataArray, onChangeCallback);
                    if (onChangeCallback) onChangeCallback();
                }
                input.value = '';
            }
        });
    }

    function addTagChip(container, tag, dataArray, onChangeCallback) {
        const input = container.querySelector('.tag-input');
        const chip = document.createElement('span');
        chip.className = 'tag-chip';
        chip.innerHTML = `
            <span>${escapeHtml(tag)}</span>
            <span class="tag-remove">×</span>
        `;
        
        chip.querySelector('.tag-remove').addEventListener('click', () => {
            const idx = dataArray.indexOf(tag);
            if (idx > -1) {
                dataArray.splice(idx, 1);
            }
            chip.remove();
            if (onChangeCallback) onChangeCallback();
        });
        
        container.insertBefore(chip, input);
    }

    function setupRuleSettings(card, rule, onChangeCallback) {
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
        setupNumberInput(card, '.rule-chromaprint-analysis-percent', rule, 'ChromaprintAnalysisPercent');
        setupNumberInput(card, '.rule-chromaprint-min-duration', rule, 'ChromaprintMinDuration', true);
        setupNumberInput(card, '.rule-chromaprint-max-duration', rule, 'ChromaprintMaxDuration', true);
        setupNumberInput(card, '.rule-chromaprint-fingerprint-duration', rule, 'ChromaprintFingerprintDuration', true);
        setupNumberInput(card, '.rule-chromaprint-similarity-threshold', rule, 'ChromaprintSimilarityThreshold');
        setupNumberInput(card, '.rule-chromaprint-comparison-tolerance', rule, 'ChromaprintEpisodeComparisonTolerance');
        setupNumberInput(card, '.rule-chromaprint-comparison-min-episodes', rule, 'ChromaprintEpisodeComparisonMinimumEpisodes', true);
        setupNumberInput(card, '.rule-chromaprint-stop-seconds', rule, 'ChromaprintStopSecondsFromEnd');
        setupNumberInput(card, '.rule-timestamp-offset', rule, 'TimestampOffsetSeconds');
        setupNumberInput(card, '.rule-black-frame-min-duration', rule, 'BlackFrameMinDuration');
        setupNumberInput(card, '.rule-ocr-page-segmentation-mode', rule, 'OcrPageSegmentationMode', true);
        setupNumberInput(card, '.rule-ocr-engine-mode', rule, 'OcrEngineMode', true);
        setupNumberInput(card, '.rule-ocr-minimum-confidence', rule, 'OcrMinimumConfidence');
        setupNumberInput(card, '.rule-ocr-consecutive-matches-early-stop', rule, 'OcrConsecutiveMatchesForEarlyStop', true);
        setupNumberInput(card, '.rule-ocr-contrast-enhancement', rule, 'OcrContrastEnhancement');
        setupNumberInput(card, '.rule-ocr-brightness-adjustment', rule, 'OcrBrightnessAdjustment');
        setupNumberInput(card, '.rule-ocr-sharpen-amount', rule, 'OcrSharpenAmount');
        setupNumberInput(card, '.rule-ocr-fuzzy-match-max-distance', rule, 'OcrFuzzyMatchMaxDistance', true);
        setupNumberInput(card, '.rule-ocr-scrolling-min-frames', rule, 'OcrScrollingMinFrames', true);
        setupNumberInput(card, '.rule-ocr-scrolling-overlap-threshold', rule, 'OcrScrollingOverlapThreshold');
        setupNumberInput(card, '.rule-ocr-adaptive-frame-rate-min', rule, 'OcrAdaptiveFrameRateMin');
        setupNumberInput(card, '.rule-ocr-minimum-structure-lines', rule, 'OcrMinimumStructureLines', true);

        const ocrLanguagesInput = card.querySelector('.rule-ocr-languages');
        ocrLanguagesInput.value = rule.OcrLanguages || '';
        ocrLanguagesInput.addEventListener('input', (e) => {
            rule.OcrLanguages = e.target.value || null;
        });

        const ocrRoiRegionSelect = card.querySelector('.rule-ocr-roi-region');
        ocrRoiRegionSelect.value = rule.OcrRoiRegion || '';
        ocrRoiRegionSelect.addEventListener('change', (e) => {
            rule.OcrRoiRegion = e.target.value || null;
        });

        setupBooleanSelect(card, '.rule-chromaprint-enable-episode-comparison', rule, 'ChromaprintEnableEpisodeComparison');
        setupBooleanSelect(card, '.rule-ocr-preserve-interword-spaces', rule, 'OcrPreserveInterwordSpaces');
        setupBooleanSelect(card, '.rule-ocr-enable-smart-frame-skipping', rule, 'OcrEnableSmartFrameSkipping');
        setupBooleanSelect(card, '.rule-ocr-enable-image-preprocessing', rule, 'OcrEnableImagePreprocessing');
        setupBooleanSelect(card, '.rule-ocr-enable-sharpening', rule, 'OcrEnableSharpening');
        setupBooleanSelect(card, '.rule-ocr-enable-roi-detection', rule, 'OcrEnableRoiDetection');
        setupBooleanSelect(card, '.rule-ocr-enable-fuzzy-matching', rule, 'OcrEnableFuzzyMatching');
        setupBooleanSelect(card, '.rule-ocr-enable-scrolling-detection', rule, 'OcrEnableScrollingDetection');
        setupBooleanSelect(card, '.rule-ocr-enable-adaptive-frame-rate', rule, 'OcrEnableAdaptiveFrameRate');
        setupBooleanSelect(card, '.rule-ocr-enable-credit-structure-detection', rule, 'OcrEnableCreditStructureDetection');
        setupBooleanSelect(card, '.rule-black-frame-refine-credits-boundary', rule, 'BlackFrameRefineCreditsBoundary');
        
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
        setupBooleanSelect(card, '.rule-disable-detection', rule, 'DisableDetection');
        if (onChangeCallback) {
            card.querySelector('.rule-disable-detection').addEventListener('change', onChangeCallback);
        }
        
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
                    Studios: [],
                    SeriesNames: [],
                    LibraryIds: []
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
