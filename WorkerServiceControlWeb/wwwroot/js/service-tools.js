(function () {
    'use strict';

    var HISTORY_TAKE = 50;
    var RECENT_TAKE = 10;
    var EVENT_META = {
        start_requested: {
            label: 'D\u00e9marrage demand\u00e9',
            className: 'history-event-started',
            marker: '\u25b7'
        },
        started: {
            label: 'D\u00e9marr\u00e9',
            className: 'history-event-started',
            marker: '\u25b6'
        },
        start_failed: {
            label: '\u00c9chec du d\u00e9marrage',
            className: 'history-event-failed',
            marker: '!'
        },
        start_rejected: {
            label: 'D\u00e9marrage refus\u00e9',
            className: 'history-event-failed',
            marker: '!'
        },
        completed: {
            label: 'Traitement termin\u00e9 \u00b7 en attente',
            className: 'history-event-completed',
            marker: '\u2713'
        },
        stop_requested: {
            label: 'Arr\u00eat demand\u00e9',
            className: 'history-event-stopped',
            marker: '\u25a1'
        },
        stopped: {
            label: 'Arr\u00eat\u00e9',
            className: 'history-event-stopped',
            marker: '\u25a0'
        },
        stop_failed: {
            label: '\u00c9chec de l\u2019arr\u00eat',
            className: 'history-event-failed',
            marker: '!'
        },
        exited_unexpectedly: {
            label: 'Interruption inattendue',
            className: 'history-event-failed',
            marker: '!'
        },
        recovered: {
            label: 'Service retrouv\u00e9 actif',
            className: 'history-event-recovered',
            marker: '\u21ba'
        }
    };

    var DEFAULT_EVENT_META = {
        label: '\u00c9v\u00e9nement du service',
        className: 'history-event-unknown',
        marker: '\u2022'
    };

    var historyCache = new Map();
    var historyRequests = new Map();
    var statusVersions = new Map();
    var serviceCards = new Map();

    document.querySelectorAll('[data-service-card][data-service-key]').forEach(function (card) {
        serviceCards.set(card.dataset.serviceKey, card);
    });

    var detailPanel = document.querySelector('[data-history-panel][data-service-key]');
    var detailKey = detailPanel ? detailPanel.dataset.serviceKey : '';
    var detailName = detailPanel ? (detailPanel.dataset.serviceName || detailKey) : '';
    var detailStatusPill = document.getElementById('detailStatusPill');
    var detailStatusLabel = document.getElementById('detailStatusLabel');
    var detailStatusValue = document.getElementById('detailStatusValue');

    var recentBody = document.getElementById('serviceHistoryRecentBody');
    var recentLoading = document.getElementById('serviceHistoryRecentLoading');
    var recentError = document.getElementById('serviceHistoryRecentError');
    var recentEmpty = document.getElementById('serviceHistoryRecentEmpty');
    var recentList = document.getElementById('serviceHistoryRecentList');
    var recentCount = document.getElementById('serviceHistoryRecentCount');
    var recentPlural = document.getElementById('serviceHistoryRecentPlural');

    var historyModal = document.getElementById('serviceHistoryModal');
    var historyModalBody = document.getElementById('serviceHistoryModalBody');
    var historyModalName = document.getElementById('serviceHistoryServiceName');
    var historyModalLoading = document.getElementById('serviceHistoryModalLoading');
    var historyModalError = document.getElementById('serviceHistoryModalError');
    var historyModalEmpty = document.getElementById('serviceHistoryModalEmpty');
    var historyModalList = document.getElementById('serviceHistoryTimeline');
    var historyModalUpdatedAt = document.getElementById('serviceHistoryUpdatedAt');
    var btnClearHistory = document.getElementById('btnClearHistory');
    var modalHistoryKey = '';
    var modalHistoryName = '';

    var fullDateFormatter;
    var compactDateFormatter;

    try {
        fullDateFormatter = new Intl.DateTimeFormat('fr-FR', {
            day: '2-digit',
            month: 'short',
            year: 'numeric',
            hour: '2-digit',
            minute: '2-digit',
            second: '2-digit'
        });
        compactDateFormatter = new Intl.DateTimeFormat('fr-FR', {
            day: '2-digit',
            month: 'short',
            hour: '2-digit',
            minute: '2-digit'
        });
    } catch (_) {
        fullDateFormatter = null;
        compactDateFormatter = null;
    }

    function hasValue(value) {
        return value !== undefined && value !== null && value !== '';
    }

    function normalizeVersion(value) {
        return hasValue(value) ? String(value) : '';
    }

    function normalizeEventType(value) {
        return String(value || '').trim().toLowerCase();
    }

    function eventMeta(eventItem) {
        return EVENT_META[normalizeEventType(eventItem && eventItem.type)] || DEFAULT_EVENT_META;
    }

    function eventTimeValue(eventItem) {
        if (!eventItem) return '';
        return eventItem.occurredAtUtc || eventItem.timestampUtc || eventItem.occurredAt || '';
    }

    function parseDate(value) {
        if (!value) return null;
        var date = new Date(value);
        return isNaN(date.getTime()) ? null : date;
    }

    function formatDate(value, compact) {
        var date = parseDate(value);
        if (!date) return 'Date inconnue';

        var formatter = compact ? compactDateFormatter : fullDateFormatter;
        if (formatter) return formatter.format(date);
        return date.toLocaleString('fr-FR');
    }

    function clearElement(element) {
        if (!element) return;
        while (element.firstChild) {
            element.removeChild(element.firstChild);
        }
    }

    function setHidden(element, hidden) {
        if (element) element.hidden = hidden;
    }

    function requestJson(url, options) {
        return fetch(url, options).then(function (response) {
            return response.json()
                .catch(function () { return {}; })
                .then(function (data) {
                    return { response: response, data: data || {} };
                });
        });
    }

    function createHistoryItem(eventItem) {
        var meta = eventMeta(eventItem);
        var item = document.createElement('li');
        item.className = 'service-history-item ' + meta.className;

        var marker = document.createElement('span');
        marker.className = 'service-history-marker';
        marker.setAttribute('aria-hidden', 'true');
        marker.textContent = meta.marker;

        var content = document.createElement('div');
        content.className = 'service-history-item-content';

        var heading = document.createElement('div');
        heading.className = 'service-history-item-heading';

        var label = document.createElement('strong');
        label.textContent = meta.label;
        heading.appendChild(label);

        var timeValue = eventTimeValue(eventItem);
        var time = document.createElement('time');
        time.className = 'service-history-time';
        time.textContent = formatDate(timeValue, false);
        if (timeValue) time.dateTime = timeValue;
        heading.appendChild(time);
        content.appendChild(heading);

        if (eventItem && eventItem.message) {
            var message = document.createElement('p');
            message.className = 'service-history-message';
            message.textContent = String(eventItem.message);
            content.appendChild(message);
        }

        var contextParts = [];
        if (eventItem && eventItem.actor) contextParts.push('Par ' + String(eventItem.actor));
        if (eventItem && hasValue(eventItem.pid)) contextParts.push('PID ' + String(eventItem.pid));
        if (eventItem && hasValue(eventItem.exitCode)) contextParts.push('Code de sortie ' + String(eventItem.exitCode));

        if (contextParts.length) {
            var context = document.createElement('span');
            context.className = 'service-history-context';
            context.textContent = contextParts.join(' \u00b7 ');
            content.appendChild(context);
        }

        item.appendChild(marker);
        item.appendChild(content);
        return item;
    }

    function renderHistoryList(list, events, take) {
        if (!list) return 0;
        clearElement(list);

        var visibleEvents = events.slice(0, take || events.length);
        var fragment = document.createDocumentFragment();
        visibleEvents.forEach(function (eventItem) {
            fragment.appendChild(createHistoryItem(eventItem));
        });
        list.appendChild(fragment);
        list.hidden = visibleEvents.length === 0;
        return visibleEvents.length;
    }

    function sortEventsNewestFirst(events) {
        return events.slice().sort(function (left, right) {
            var leftDate = parseDate(eventTimeValue(left));
            var rightDate = parseDate(eventTimeValue(right));
            if (!leftDate || !rightDate) return 0;
            return rightDate.getTime() - leftDate.getTime();
        });
    }

    function updateCardLastEvent(key, lastEvent) {
        var card = serviceCards.get(key);
        if (!card) return;

        var container = card.querySelector('[data-role="service-last-event"]');
        if (!container) return;

        if (!lastEvent) {
            container.hidden = true;
            return;
        }

        var meta = eventMeta(lastEvent);
        var marker = container.querySelector('[data-role="last-event-marker"]');
        var label = container.querySelector('[data-role="last-event-label"]');
        var time = container.querySelector('[data-role="last-event-time"]');
        var timeValue = eventTimeValue(lastEvent);

        container.hidden = false;
        container.classList.remove(
            'history-event-started',
            'history-event-completed',
            'history-event-stopped',
            'history-event-failed',
            'history-event-recovered',
            'history-event-unknown'
        );
        container.classList.add(meta.className);

        if (marker) marker.textContent = meta.marker;
        if (label) label.textContent = meta.label;
        if (time) {
            time.textContent = formatDate(timeValue, true);
            if (timeValue) time.dateTime = timeValue;
            else time.removeAttribute('datetime');
        }
    }

    function setRecentLoading(loading) {
        if (!recentBody) return;
        recentBody.setAttribute('aria-busy', loading ? 'true' : 'false');
        setHidden(recentLoading, !loading);
    }

    function setModalLoading(loading) {
        if (!historyModalBody) return;
        historyModalBody.setAttribute('aria-busy', loading ? 'true' : 'false');
        setHidden(historyModalLoading, !loading);
    }

    function showHistoryError(key, message) {
        if (detailKey === key && recentError) {
            setRecentLoading(false);
            recentError.textContent = message;
            recentError.hidden = false;
            if (!historyCache.has(key)) {
                setHidden(recentEmpty, true);
                setHidden(recentList, true);
            }
        }

        if (modalHistoryKey === key && historyModalError) {
            setModalLoading(false);
            historyModalError.textContent = message;
            historyModalError.hidden = false;
            if (!historyCache.has(key)) {
                setHidden(historyModalEmpty, true);
                setHidden(historyModalList, true);
            }
        }
    }

    function renderHistoryData(historyData) {
        var key = historyData.serviceKey;
        var events = historyData.events;

        if (events.length) updateCardLastEvent(key, events[0]);

        if (detailKey === key && recentList) {
            setRecentLoading(false);
            setHidden(recentError, true);
            var renderedCount = renderHistoryList(recentList, events, RECENT_TAKE);
            setHidden(recentEmpty, events.length !== 0);
            if (recentCount) recentCount.textContent = String(renderedCount);
            if (recentPlural) recentPlural.textContent = renderedCount === 1 ? '' : 's';
        }

        if (modalHistoryKey === key && historyModalList) {
            setModalLoading(false);
            setHidden(historyModalError, true);
            renderHistoryList(historyModalList, events, HISTORY_TAKE);
            setHidden(historyModalEmpty, events.length !== 0);
            if (historyModalName) {
                historyModalName.textContent = historyData.displayName || modalHistoryName || key;
            }
            if (historyModalUpdatedAt) {
                historyModalUpdatedAt.textContent = 'Actualis\u00e9 \u00e0 ' + formatDate(new Date().toISOString(), true);
            }
        }
    }

    function loadHistory(key, displayName, force) {
        if (!key) return Promise.resolve(null);

        var cached = historyCache.get(key);
        if (cached) renderHistoryData(cached);
        if (cached && !force) return Promise.resolve(cached);
        if (historyRequests.has(key)) return historyRequests.get(key);

        if (!cached) {
            if (detailKey === key) setRecentLoading(true);
            if (modalHistoryKey === key) setModalLoading(true);
        }

        if (detailKey === key) setHidden(recentError, true);
        if (modalHistoryKey === key) setHidden(historyModalError, true);

        var url = '/Worker/History?key=' + encodeURIComponent(key) + '&take=' + HISTORY_TAKE;
        var request = requestJson(url, {
            method: 'GET',
            credentials: 'same-origin',
            headers: { Accept: 'application/json' }
        }).then(function (result) {
            var payload = result.data;
            if (!result.response.ok || payload.success === false) {
                throw new Error(payload.error || 'Impossible de charger l\u2019historique.');
            }

            var historyData = {
                serviceKey: payload.serviceKey || key,
                displayName: payload.displayName || displayName || key,
                version: normalizeVersion(payload.version),
                events: sortEventsNewestFirst(Array.isArray(payload.events) ? payload.events : [])
            };

            historyCache.set(key, historyData);
            statusVersions.set(key, historyData.version);
            renderHistoryData(historyData);
            return historyData;
        });

        historyRequests.set(key, request);
        request.then(
            function () { historyRequests.delete(key); },
            function (error) {
                historyRequests.delete(key);
                showHistoryError(key, error.message || 'Impossible de charger l\u2019historique.');
            }
        );

        return request;
    }

    function syncStatus(status) {
        if (!status) return;
        var key = status.key || status.serviceKey;
        if (!key) return;
        key = String(key);

        if (Object.prototype.hasOwnProperty.call(status, 'lastEvent')) {
            updateCardLastEvent(key, status.lastEvent);
        }

        var hasHistoryVersion = hasValue(status.historyVersion);
        if (hasHistoryVersion) {
            statusVersions.set(key, normalizeVersion(status.historyVersion));
        }

        var isVisibleDetail = detailKey === key;
        var isOpenModal = modalHistoryKey === key;
        if (!isVisibleDetail && !isOpenModal) return;

        var cached = historyCache.get(key);
        var shouldLoad = !cached;
        if (cached && hasHistoryVersion) {
            shouldLoad = normalizeVersion(cached.version) !== normalizeVersion(status.historyVersion);
        }

        if (shouldLoad) {
            loadHistory(key, status.displayName || key, true).catch(function () { /* state already rendered */ });
        }
    }

    window.FlowOpsServiceTools = {
        syncStatus: syncStatus,
        refreshHistory: function (key, displayName) {
            return loadHistory(key, displayName, true);
        }
    };

    function enforceDetailWaitingLabel() {
        if (!detailStatusPill || detailStatusPill.dataset.state !== 'waiting') return;
        var waitingLabel = 'Termin\u00e9 \u00b7 en attente';
        if (detailStatusLabel && detailStatusLabel.textContent !== waitingLabel) {
            detailStatusLabel.textContent = waitingLabel;
        }
        if (detailStatusValue && detailStatusValue.textContent !== waitingLabel) {
            detailStatusValue.textContent = waitingLabel;
        }
    }

    if (detailStatusPill && window.MutationObserver) {
        enforceDetailWaitingLabel();
        new MutationObserver(enforceDetailWaitingLabel).observe(detailStatusPill, {
            attributes: true,
            attributeFilter: ['data-state', 'class'],
            childList: true,
            subtree: true
        });
    }

    if (historyModal) {
        historyModal.addEventListener('show.bs.modal', function (event) {
            var launcher = event.relatedTarget;
            modalHistoryKey = launcher && launcher.dataset.serviceKey
                ? launcher.dataset.serviceKey
                : detailKey;
            modalHistoryName = launcher && launcher.dataset.serviceName
                ? launcher.dataset.serviceName
                : (detailName || modalHistoryKey);

            if (historyModalName) historyModalName.textContent = modalHistoryName;
            if (historyModalUpdatedAt) historyModalUpdatedAt.textContent = '';
            setHidden(historyModalError, true);
            setHidden(historyModalEmpty, true);
            clearElement(historyModalList);
            setHidden(historyModalList, true);

            loadHistory(modalHistoryKey, modalHistoryName, true)
                .catch(function () { /* state already rendered */ });
        });

        historyModal.addEventListener('hidden.bs.modal', function () {
            modalHistoryKey = '';
            modalHistoryName = '';
            setModalLoading(false);
            setHidden(historyModalError, true);
            setHidden(historyModalEmpty, true);
            clearElement(historyModalList);
            if (historyModalUpdatedAt) historyModalUpdatedAt.textContent = '';
        });
    }

    function clearHistory(key, name, button) {
        if (!key) return;
        name = name || key;

        if (!window.confirm('Supprimer définitivement l’historique de "' + name + '" ?')) {
            return;
        }

        if (button) button.disabled = true;

        fetch('/Worker/ClearHistory?key=' + encodeURIComponent(key), { method: 'POST' })
            .then(function (r) { return r.json(); })
            .then(function (data) {
                if (button) button.disabled = false;

                if (data.success) {
                    historyCache.delete(key);
                    statusVersions.delete(key);
                    updateCardLastEvent(key, null);
                    renderHistoryData({ serviceKey: key, displayName: name, version: '', events: [] });
                    if (window.AppToast) AppToast.show(name, 'Historique supprimé', 'info');
                } else {
                    window.alert(data.error || 'Impossible de supprimer l’historique.');
                }
            })
            .catch(function () {
                if (button) button.disabled = false;
                window.alert('Impossible de supprimer l’historique.');
            });
    }

    if (btnClearHistory) {
        btnClearHistory.addEventListener('click', function () {
            clearHistory(modalHistoryKey, modalHistoryName, btnClearHistory);
        });
    }

    var btnClearHistoryPanel = document.getElementById('btnClearHistoryPanel');
    if (btnClearHistoryPanel) {
        btnClearHistoryPanel.addEventListener('click', function () {
            clearHistory(detailKey, detailName, btnClearHistoryPanel);
        });
    }

    if (detailKey) {
        loadHistory(detailKey, detailName, true).catch(function () { /* state already rendered */ });
    }

    var settingsModal = document.getElementById('appSettingsModal');
    var settingsForm = document.getElementById('appSettingsForm');
    var settingsBody = document.getElementById('appSettingsModalBody');
    var settingsServiceKeyInput = document.getElementById('appSettingsServiceKey');
    var settingsExpectedVersionInput = document.getElementById('appSettingsExpectedVersion');
    var settingsServiceName = document.getElementById('appSettingsServiceName');
    var settingsFileName = document.getElementById('appSettingsFileName');
    var settingsEditor = document.getElementById('appSettingsEditor');
    var settingsValidation = document.getElementById('appSettingsValidation');
    var settingsFeedback = document.getElementById('appSettingsFeedback');
    var settingsReloadButton = document.getElementById('btnReloadAppSettings');
    var settingsFormatButton = document.getElementById('btnFormatAppSettings');
    var settingsSaveButton = document.getElementById('btnSaveAppSettings');
    var antiforgeryInput = document.querySelector('#serviceToolsAntiforgery input[name="__RequestVerificationToken"]');

    var settingsKey = '';
    var settingsName = '';
    var settingsOriginalContent = '';
    var settingsVersion = '';
    var settingsDirty = false;
    var settingsValid = false;
    var settingsLoading = false;
    var settingsSaving = false;
    var settingsConflict = false;
    var settingsLoadSequence = 0;

    function setSettingsFeedback(message, type) {
        if (!settingsFeedback) return;
        settingsFeedback.classList.remove('is-info', 'is-success', 'is-error', 'is-warning');
        if (type) settingsFeedback.classList.add('is-' + type);
        settingsFeedback.textContent = message || '';
    }

    function setSettingsValidation(message, valid) {
        if (!settingsValidation) return;
        settingsValidation.classList.remove('is-valid', 'is-invalid');
        if (valid === true) settingsValidation.classList.add('is-valid');
        if (valid === false) settingsValidation.classList.add('is-invalid');
        settingsValidation.textContent = message;
    }

    function updateSettingsControls() {
        if (settingsBody) settingsBody.setAttribute('aria-busy', settingsLoading || settingsSaving ? 'true' : 'false');
        if (settingsEditor) settingsEditor.disabled = settingsLoading || settingsSaving || !settingsKey;
        if (settingsReloadButton) settingsReloadButton.disabled = settingsLoading || settingsSaving || !settingsKey;
        if (settingsFormatButton) settingsFormatButton.disabled = settingsLoading || settingsSaving || !settingsValid;
        if (settingsSaveButton) {
            settingsSaveButton.disabled = settingsLoading || settingsSaving || !settingsValid || !settingsDirty || settingsConflict;
        }
    }

    function validateSettingsContent() {
        if (!settingsEditor) return false;

        try {
            var parsed = JSON.parse(settingsEditor.value);
            if (parsed === null || typeof parsed !== 'object' || Array.isArray(parsed)) {
                throw new Error('La racine doit \u00eatre un objet JSON.');
            }
            settingsValid = true;
            setSettingsValidation('JSON valide', true);
        } catch (error) {
            settingsValid = false;
            setSettingsValidation('JSON invalide : ' + error.message, false);
        }

        settingsDirty = settingsEditor.value !== settingsOriginalContent;
        updateSettingsControls();
        return settingsValid;
    }

    function loadAppSettings(key, displayName) {
        if (!settingsModal || !settingsEditor || !key) return Promise.resolve(null);

        settingsLoadSequence += 1;
        var sequence = settingsLoadSequence;
        settingsKey = key;
        settingsName = displayName || key;
        settingsLoading = true;
        settingsSaving = false;
        settingsConflict = false;
        settingsDirty = false;
        settingsValid = false;

        if (settingsServiceKeyInput) settingsServiceKeyInput.value = key;
        if (settingsServiceName) settingsServiceName.textContent = settingsName;
        if (settingsExpectedVersionInput) settingsExpectedVersionInput.value = '';
        if (settingsFileName) settingsFileName.textContent = 'appsettings.json';
        settingsEditor.value = '';
        setSettingsValidation('Chargement\u2026', null);
        setSettingsFeedback('Lecture de la configuration\u2026', 'info');
        updateSettingsControls();

        return requestJson('/Worker/AppSettings?key=' + encodeURIComponent(key), {
            method: 'GET',
            credentials: 'same-origin',
            headers: { Accept: 'application/json' }
        }).then(function (result) {
            if (sequence !== settingsLoadSequence) return null;
            var payload = result.data;
            if (!result.response.ok || payload.success === false) {
                throw new Error(payload.error || 'Impossible de charger appsettings.json.');
            }

            settingsVersion = normalizeVersion(payload.version);
            settingsEditor.value = typeof payload.content === 'string' ? payload.content : '{\n}\n';
            // A textarea normalizes CRLF to LF in its value. Store the
            // browser-normalized representation so an untouched Windows JSON
            // file is not incorrectly considered modified.
            settingsOriginalContent = settingsEditor.value;
            settingsLoading = false;
            settingsDirty = false;
            settingsConflict = false;

            if (settingsExpectedVersionInput) settingsExpectedVersionInput.value = settingsVersion;
            if (settingsFileName) settingsFileName.textContent = payload.fileName || 'appsettings.json';
            validateSettingsContent();

            if (payload.exists === false) {
                setSettingsFeedback('Le fichier n\u2019existe pas encore. Il sera cr\u00e9\u00e9 lors de l\u2019enregistrement.', 'info');
            } else if (payload.isValid === false) {
                setSettingsFeedback(payload.validationError || 'Le fichier actuel contient un JSON invalide.', 'warning');
            } else {
                setSettingsFeedback('Configuration charg\u00e9e. Les secrets restent affich\u00e9s uniquement dans cet \u00e9diteur.', 'info');
            }

            window.setTimeout(function () {
                if (settingsModal.classList.contains('show') && !settingsEditor.disabled) settingsEditor.focus();
            }, 80);
            return payload;
        }).catch(function (error) {
            if (sequence !== settingsLoadSequence) return null;
            settingsLoading = false;
            settingsValid = false;
            setSettingsValidation('Configuration indisponible', false);
            setSettingsFeedback(error.message || 'Impossible de charger appsettings.json.', 'error');
            updateSettingsControls();
            return null;
        });
    }

    function saveAppSettings() {
        if (!settingsEditor || !settingsKey || !validateSettingsContent() || !settingsDirty || settingsConflict) {
            return Promise.resolve(null);
        }

        settingsSaving = true;
        setSettingsFeedback('Enregistrement s\u00e9curis\u00e9 en cours\u2026', 'info');
        updateSettingsControls();

        var headers = {
            Accept: 'application/json',
            'Content-Type': 'application/json'
        };
        if (antiforgeryInput && antiforgeryInput.value) {
            headers.RequestVerificationToken = antiforgeryInput.value;
        }

        return requestJson('/Worker/SaveAppSettings', {
            method: 'POST',
            credentials: 'same-origin',
            headers: headers,
            body: JSON.stringify({
                key: settingsKey,
                content: settingsEditor.value,
                expectedVersion: settingsVersion
            })
        }).then(function (result) {
            var payload = result.data;
            settingsSaving = false;

            if (payload.conflict === true || result.response.status === 409) {
                settingsConflict = true;
                setSettingsFeedback(
                    payload.error || 'Le fichier a chang\u00e9 depuis son ouverture. Recharger avant de r\u00e9essayer.',
                    'warning'
                );
                updateSettingsControls();
                return null;
            }

            if (!result.response.ok || payload.success === false) {
                throw new Error(payload.error || 'Impossible d\u2019enregistrer appsettings.json.');
            }

            settingsVersion = normalizeVersion(payload.version);
            settingsOriginalContent = settingsEditor.value;
            settingsDirty = false;
            settingsConflict = false;
            if (settingsExpectedVersionInput) settingsExpectedVersionInput.value = settingsVersion;
            setSettingsFeedback('Configuration enregistr\u00e9e. Elle sera appliqu\u00e9e au prochain d\u00e9marrage.', 'success');
            updateSettingsControls();

            if (window.AppToast) {
                window.AppToast.show(settingsName, 'App settings enregistr\u00e9', 'success');
            }
            return payload;
        }).catch(function (error) {
            settingsSaving = false;
            setSettingsFeedback(error.message || 'Impossible d\u2019enregistrer appsettings.json.', 'error');
            updateSettingsControls();
            return null;
        });
    }

    if (settingsModal && settingsForm && settingsEditor) {
        settingsModal.addEventListener('show.bs.modal', function (event) {
            var launcher = event.relatedTarget;
            var key = launcher && launcher.dataset.serviceKey ? launcher.dataset.serviceKey : detailKey;
            var displayName = launcher && launcher.dataset.serviceName
                ? launcher.dataset.serviceName
                : (detailName || key);
            loadAppSettings(key, displayName);
        });

        settingsModal.addEventListener('hide.bs.modal', function (event) {
            if (settingsDirty && !settingsSaving) {
                var discard = window.confirm('Abandonner les modifications non enregistr\u00e9es ?');
                if (!discard) {
                    event.preventDefault();
                    return;
                }
                settingsDirty = false;
            }
        });

        settingsModal.addEventListener('hidden.bs.modal', function () {
            settingsLoadSequence += 1;
            settingsKey = '';
            settingsName = '';
            settingsVersion = '';
            settingsOriginalContent = '';
            settingsDirty = false;
            settingsValid = false;
            settingsLoading = false;
            settingsSaving = false;
            settingsConflict = false;
            settingsEditor.value = '';
            setSettingsFeedback('', null);
            setSettingsValidation('', null);
            updateSettingsControls();
        });

        settingsEditor.addEventListener('input', function () {
            if (settingsConflict) {
                setSettingsFeedback('Une version plus r\u00e9cente existe. Recharger avant d\u2019enregistrer.', 'warning');
            }
            validateSettingsContent();
        });

        settingsEditor.addEventListener('keydown', function (event) {
            if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 's') {
                event.preventDefault();
                if (!settingsSaveButton.disabled) settingsForm.requestSubmit();
            }

            if (event.key === 'Tab' && !event.ctrlKey && !event.metaKey && !event.altKey) {
                event.preventDefault();
                var start = settingsEditor.selectionStart;
                var end = settingsEditor.selectionEnd;
                settingsEditor.setRangeText('  ', start, end, 'end');
                settingsEditor.dispatchEvent(new Event('input', { bubbles: true }));
            }
        });

        settingsForm.addEventListener('submit', function (event) {
            event.preventDefault();
            saveAppSettings();
        });

        if (settingsFormatButton) {
            settingsFormatButton.addEventListener('click', function () {
                if (!validateSettingsContent()) return;
                settingsEditor.value = JSON.stringify(JSON.parse(settingsEditor.value), null, 2) + '\n';
                validateSettingsContent();
                settingsEditor.focus();
            });
        }

        if (settingsReloadButton) {
            settingsReloadButton.addEventListener('click', function () {
                if (settingsDirty && !window.confirm('Recharger le fichier et abandonner les modifications locales ?')) {
                    return;
                }
                settingsDirty = false;
                loadAppSettings(settingsKey, settingsName);
            });
        }
    }
})();
