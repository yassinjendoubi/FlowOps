// Please see documentation at https://learn.microsoft.com/aspnet/core/client-side/bundling-and-minification
// for details on configuring this project to bundle and minify static web assets.

// Write your JavaScript code.

(function () {
    var toggle = document.getElementById('themeToggle');
    if (!toggle) {
        return;
    }

    var label = document.getElementById('themeLabel');

    function syncThemeControl() {
        var isDark = document.documentElement.getAttribute('data-theme') === 'dark';
        if (label) {
            label.textContent = isDark ? 'Mode clair' : 'Mode sombre';
        }
        toggle.setAttribute('aria-label', isDark ? 'Activer le thème clair' : 'Activer le thème sombre');
        toggle.setAttribute('aria-pressed', isDark ? 'true' : 'false');
    }

    syncThemeControl();

    toggle.addEventListener('click', function () {
        var current = document.documentElement.getAttribute('data-theme');
        var next = current === 'dark' ? 'light' : 'dark';
        document.documentElement.setAttribute('data-theme', next);
        localStorage.setItem('theme', next);
        syncThemeControl();
    });
})();

(function () {
    var sidebar = document.getElementById('appSidebar');
    var toggleBtn = document.getElementById('sidebarToggle');

    if (sidebar && toggleBtn) {
        var isMobile = function () {
            return window.matchMedia('(max-width: 900px)').matches;
        };

        var closeMobileSidebar = function () {
            sidebar.classList.remove('is-open');
            document.body.classList.remove('sidebar-is-open');
            toggleBtn.setAttribute('aria-expanded', 'false');
        };

        toggleBtn.setAttribute(
            'aria-expanded',
            !isMobile() && !document.documentElement.classList.contains('sidebar-collapsed') ? 'true' : 'false'
        );

        toggleBtn.addEventListener('click', function () {
            if (isMobile()) {
                var isOpen = sidebar.classList.toggle('is-open');
                document.body.classList.toggle('sidebar-is-open', isOpen);
                toggleBtn.setAttribute('aria-expanded', isOpen ? 'true' : 'false');
            } else {
                var collapsed = document.documentElement.classList.toggle('sidebar-collapsed');
                localStorage.setItem('sidebarCollapsed', collapsed ? 'true' : 'false');
                toggleBtn.setAttribute('aria-expanded', collapsed ? 'false' : 'true');
            }
        });

        document.addEventListener('click', function (e) {
            if (sidebar.classList.contains('is-open') && !sidebar.contains(e.target) && e.target !== toggleBtn && !toggleBtn.contains(e.target)) {
                closeMobileSidebar();
            }
        });

        document.addEventListener('keydown', function (e) {
            if (e.key === 'Escape' && sidebar.classList.contains('is-open')) {
                closeMobileSidebar();
                toggleBtn.focus();
            }
        });

        window.addEventListener('resize', function () {
            if (!isMobile()) {
                closeMobileSidebar();
                toggleBtn.setAttribute('aria-expanded', document.documentElement.classList.contains('sidebar-collapsed') ? 'false' : 'true');
            }
        });
    }

    var sidebarLinks = document.querySelectorAll('.sidebar-sublink[data-service]');
    if (!sidebarLinks.length) {
        return;
    }

    var previousStates = {};
    var firstSidebarPoll = true;

    function pollSidebarStatuses() {
        fetch('/Worker/StatusAll')
            .then(function (r) { return r.json(); })
            .then(function (statuses) {
                statuses.forEach(function (status) {
                    var link = document.querySelector('.sidebar-sublink[data-service="' + status.key + '"]');
                    if (link) {
                        var dot = link.querySelector('[data-role="dot"]');
                        if (dot) {
                            dot.classList.toggle('is-on', status.state === 'running');
                            dot.classList.toggle('is-waiting', status.state === 'waiting');
                            dot.classList.toggle('is-off', status.state === 'stopped');
                            dot.dataset.state = status.state;
                        }

                        var accessibleState = link.querySelector('.visually-hidden');
                        if (accessibleState) {
                            accessibleState.textContent = '— ' + (status.state === 'running' ? 'En cours' : status.state === 'waiting' ? 'Terminé · en attente' : 'Arrêté');
                        }
                    }

                    if (!firstSidebarPoll && window.AppToast) {
                        var prev = previousStates[status.key];
                        if (prev !== undefined && prev !== status.state) {
                            var name = status.displayName || status.key;
                            if (prev === 'running' && status.state === 'waiting') {
                                AppToast.show(name, 'Transfert terminé avec succès', 'warning');
                            } else if (prev === 'stopped' && status.state === 'running') {
                                AppToast.show(name, 'Service démarré', 'success');
                            }
                        }
                    }

                    previousStates[status.key] = status.state;
                });

                firstSidebarPoll = false;
            })
            .catch(function () { /* ignore transient polling errors */ });
    }

    pollSidebarStatuses();
    setInterval(pollSidebarStatuses, 1500);
})();

(function () {
    var list = document.getElementById('sidebarServiceList');
    if (!list) {
        return;
    }

    list.addEventListener('click', function (e) {
        var btn = e.target.closest('[data-action="delete-service"]');
        if (!btn) {
            return;
        }

        e.preventDefault();
        e.stopPropagation();

        var key = btn.dataset.service;
        var name = btn.dataset.serviceName || key;

        if (!window.confirm('Supprimer définitivement le service "' + name + '" ?')) {
            return;
        }

        btn.disabled = true;

        fetch('/Worker/DeleteService?key=' + encodeURIComponent(key), { method: 'POST' })
            .then(function (r) { return r.json(); })
            .then(function (data) {
                if (data.success) {
                    // Not a reload: if we're currently on this very service's
                    // detail page, reloading would re-request a now-deleted
                    // route and show a 404. Always land back on the index.
                    window.location.href = '/Worker/Index';
                } else {
                    btn.disabled = false;
                    window.alert(data.error || 'Suppression impossible.');
                }
            })
            .catch(function () {
                btn.disabled = false;
                window.alert('Suppression impossible.');
            });
    });
})();
