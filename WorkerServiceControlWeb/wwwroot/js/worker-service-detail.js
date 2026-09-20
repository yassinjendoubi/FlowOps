(function () {
    var shell = document.querySelector('.app-shell[data-service]');
    var btnStart = document.getElementById('btnStart');
    var btnStop = document.getElementById('btnStop');
    var btnDelete = document.getElementById('btnDelete');
    var btnClearLogs = document.getElementById('btnClearLogs');
    var dot = document.getElementById('detailDot');
    var statusPill = document.getElementById('detailStatusPill');
    var statusLabel = document.getElementById('detailStatusLabel');
    var statusValue = document.getElementById('detailStatusValue');
    var detailLogCount = document.getElementById('detailLogCount');
    var detailLogLabel = document.getElementById('detailLogLabel');
    var terminalLogCount = document.getElementById('terminalLogCount');
    var terminalLogLabel = document.getElementById('terminalLogLabel');

    if (!shell || !btnStart || !btnStop || !dot) {
        return;
    }

    var serviceKey = shell.dataset.service;
    var serviceName = shell.dataset.serviceName || serviceKey;
    var body = document.getElementById('terminalBody-' + serviceKey);

    var ICONS = {
        success: 'M5 13l4 4L19 7',
        error: 'M6 6l12 12M18 6L6 18',
        warning: 'M12 9v4m0 4h.01M10.3 4.3L2.7 18a1 1 0 0 0 .9 1.5h16.8a1 1 0 0 0 .9-1.5L13.7 4.3a1 1 0 0 0-1.74 0z',
        info: 'M12 16v-4m0-4h.01M12 21a9 9 0 1 0 0-18 9 9 0 0 0 0 18z'
    };

    var STATE_META = {
        running: { label: 'En cours', stateClass: 'state-running', dotClass: 'is-on' },
        waiting: { label: 'Terminé · en attente', stateClass: 'state-waiting', dotClass: 'is-waiting' },
        stopped: { label: 'Arrêté', stateClass: 'state-stopped', dotClass: 'is-off' }
    };

    var EMPTY_STATE_HTML = '<div class="terminal-empty">' +
        '<svg width="40" height="40" viewBox="0 0 24 24" fill="none" xmlns="http://www.w3.org/2000/svg">' +
        '<rect x="3" y="4" width="18" height="14" rx="2" stroke="currentColor" stroke-width="1.5" />' +
        '<path d="M3 18h18M8 21h8" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" />' +
        '<path d="M7 9l3 2-3 2" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round" />' +
        '<path d="M12 13h4" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" />' +
        '</svg>' +
        '<h4>Aucune activité enregistrée</h4>' +
        '<p>Démarrez ce service pour afficher son journal d\'exécution.</p>' +
        '</div>';

    // Quick worker runs can finish before the next poll. Force the green
    // "running" dot to stay visible for a minimum stretch after Start is
    // clicked so the user actually perceives it.
    var MIN_RUNNING_DISPLAY_MS = 1200;
    var forcedRunningUntil = 0;

    function escapeHtml(str) {
        var div = document.createElement('div');
        div.textContent = str;
        return div.innerHTML;
    }

    function classifyLog(log) {
        if (log.indexOf('Erreur') !== -1 || log.indexOf('ERREUR') !== -1) {
            return { cls: 'is-error', icon: ICONS.error };
        }
        if (log.indexOf('Non transféré') !== -1) {
            return { cls: 'is-warning', icon: ICONS.warning };
        }
        if (/STARTED|FINISHED|Interface Web|Service demandé|arrêt demandé|Service démarré|Service arrêté/.test(log)) {
            return { cls: 'is-info', icon: ICONS.info };
        }
        return { cls: 'is-success', icon: ICONS.success };
    }

    function updateStateVisual(state) {
        var meta = STATE_META[state] || STATE_META.stopped;
        var isActive = state !== 'stopped';

        dot.classList.remove('is-on', 'is-waiting', 'is-off');
        dot.classList.add(meta.dotClass);
        dot.dataset.state = state;

        if (statusPill) {
            statusPill.classList.remove('state-running', 'state-waiting', 'state-stopped');
            statusPill.classList.add(meta.stateClass);
            statusPill.dataset.state = state;
        }

        if (statusLabel) {
            statusLabel.textContent = meta.label;
        }

        if (statusValue) {
            statusValue.classList.remove('state-running', 'state-waiting', 'state-stopped');
            statusValue.classList.add(meta.stateClass);
            statusValue.textContent = meta.label;
        }

        btnStart.disabled = isActive;
        btnStop.disabled = !isActive;
        btnStart.setAttribute('aria-disabled', isActive ? 'true' : 'false');
        btnStop.setAttribute('aria-disabled', isActive ? 'false' : 'true');
    }

    function updateLogCount(count) {
        if (detailLogCount) {
            detailLogCount.textContent = count;
        }

        if (detailLogLabel) {
            detailLogLabel.textContent = 'ligne' + (count > 1 ? 's' : '');
        }

        if (terminalLogCount) {
            terminalLogCount.textContent = count;
        }

        if (terminalLogLabel) {
            terminalLogLabel.textContent = 'ligne' + (count > 1 ? 's' : '');
        }
    }

    function renderLogs(logs) {
        var count = Array.isArray(logs) ? logs.length : 0;
        updateLogCount(count);

        if (!logs || logs.length === 0) {
            body.innerHTML = EMPTY_STATE_HTML;
            return;
        }

        var wasScrolledToBottom = body.scrollTop + body.clientHeight >= body.scrollHeight - 10;

        body.innerHTML = logs.map(function (log) {
            var meta = classifyLog(log);
            return '<div class="log-row ' + meta.cls + '">' +
                '<svg class="log-badge" viewBox="0 0 24 24" fill="none" xmlns="http://www.w3.org/2000/svg">' +
                '<path d="' + meta.icon + '" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round" /></svg>' +
                '<span>' + escapeHtml(log) + '</span>' +
                '</div>';
        }).join('');

        if (wasScrolledToBottom) {
            body.scrollTop = body.scrollHeight;
        }
    }

    function poll() {
        fetch('/Worker/Status?key=' + encodeURIComponent(serviceKey))
            .then(function (r) { return r.json(); })
            .then(function (status) {
                var forced = Date.now() < forcedRunningUntil;
                var displayState = forced ? 'running' : status.state;

                updateStateVisual(displayState);
                renderLogs(status.logs);

                if (window.FlowOpsServiceTools) {
                    window.FlowOpsServiceTools.syncStatus(status);
                }
            })
            .catch(function () { /* ignore transient polling errors */ });
    }

    btnStart.addEventListener('click', function () {
        btnStart.disabled = true;

        fetch('/Worker/Start?service=' + encodeURIComponent(serviceKey), { method: 'POST' })
            .then(function (r) { return r.json(); })
            .then(function (data) {
                if (data.success) {
                    forcedRunningUntil = Date.now() + MIN_RUNNING_DISPLAY_MS;
                    if (window.AppToast) AppToast.show(serviceName, 'Service démarré', 'success');
                } else {
                    btnStart.disabled = false;
                    if (window.AppToast) AppToast.show(serviceName, data.error || 'Erreur au démarrage', 'error', 0);
                }
                poll();
            })
            .catch(function () {
                btnStart.disabled = false;
                if (window.AppToast) AppToast.show(serviceName, 'Impossible de démarrer le service', 'error', 0);
                poll();
            });
    });

    btnStop.addEventListener('click', function () {
        btnStop.disabled = true;
        forcedRunningUntil = 0;

        fetch('/Worker/Stop?service=' + encodeURIComponent(serviceKey), { method: 'POST' })
            .then(function (r) { return r.json(); })
            .then(function (data) {
                if (data.success) {
                    if (window.AppToast) AppToast.show(serviceName, 'Service arrêté', 'info');
                } else {
                    btnStop.disabled = false;
                    if (window.AppToast) AppToast.show(serviceName, data.error || 'Arrêt impossible', 'error', 0);
                }
                poll();
            })
            .catch(function () {
                btnStop.disabled = false;
                if (window.AppToast) AppToast.show(serviceName, 'Impossible d’arrêter le service', 'error', 0);
                poll();
            });
    });

    if (btnDelete) {
        btnDelete.addEventListener('click', function () {
            var name = btnDelete.dataset.serviceName || serviceKey;

            if (!window.confirm('Supprimer définitivement le service "' + name + '" ?')) {
                return;
            }

            btnDelete.disabled = true;

            fetch('/Worker/DeleteService?key=' + encodeURIComponent(serviceKey), { method: 'POST' })
                .then(function (r) { return r.json(); })
                .then(function (data) {
                    if (data.success) {
                        window.location.href = '/Worker/Index';
                    } else {
                        btnDelete.disabled = false;
                        window.alert(data.error || 'Suppression impossible.');
                    }
                })
                .catch(function () {
                    btnDelete.disabled = false;
                    window.alert('Suppression impossible.');
                });
        });
    }

    if (btnClearLogs) {
        btnClearLogs.addEventListener('click', function () {
            if (!window.confirm('Vider tous les logs de ce service ?')) {
                return;
            }

            btnClearLogs.disabled = true;

            fetch('/Worker/ClearLogs?key=' + encodeURIComponent(serviceKey), { method: 'POST' })
                .then(function (r) { return r.json(); })
                .then(function (data) {
                    btnClearLogs.disabled = false;

                    if (data.success) {
                        if (window.AppToast) AppToast.show(serviceName, 'Journaux effacés', 'info');
                        poll();
                    } else {
                        window.alert(data.error || 'Impossible de vider les logs.');
                    }
                })
                .catch(function () {
                    btnClearLogs.disabled = false;
                    window.alert('Impossible de vider les logs.');
                });
        });
    }

    poll();
    setInterval(poll, 700);
})();
