(function () {
    var sidebarList  = document.getElementById('filesSidebarList');
    var emptyState   = document.getElementById('filesEmptyState');
    var filesContent = document.getElementById('filesContent');
    var breadcrumb   = document.getElementById('filesBreadcrumb');
    var fileTree     = document.getElementById('fileTree');
    var fileViewer   = document.getElementById('fileViewer');
    var fileViewerName = document.getElementById('fileViewerName');
    var fileViewerPre  = document.getElementById('fileViewerPre');
    var fileViewerClose = document.getElementById('fileViewerClose');

    if (!sidebarList) return;

    var TEXT_EXTENSIONS = ['.json', '.xml', '.config', '.txt', '.yaml', '.yml',
                           '.ini', '.bat', '.cmd', '.cs', '.sql', '.log', '.md',
                           '.env', '.properties', '.toml', '.csv'];

    var currentKey     = null;
    var currentSubpath = '';

    function isTextFile(name) {
        var ext = name.lastIndexOf('.');
        if (ext === -1) return false;
        return TEXT_EXTENSIONS.indexOf(name.slice(ext).toLowerCase()) !== -1;
    }

    function formatSize(bytes) {
        if (bytes < 1024)       return bytes + ' o';
        if (bytes < 1024*1024)  return (bytes / 1024).toFixed(1) + ' Ko';
        return (bytes / (1024*1024)).toFixed(1) + ' Mo';
    }

    function escHtml(s) {
        var d = document.createElement('div');
        d.textContent = s;
        return d.innerHTML;
    }

    // ── Breadcrumb ───────────────────────────────────────────
    function renderBreadcrumb(key, subpath) {
        var parts = subpath ? subpath.replace(/\\/g, '/').split('/').filter(Boolean) : [];
        var html = '<button type="button" class="bc-item bc-root" data-subpath="">' + escHtml(key) + '</button>';

        parts.forEach(function (part, i) {
            var sp = parts.slice(0, i + 1).join('/');
            html += '<span class="bc-sep">/</span>';
            if (i === parts.length - 1) {
                html += '<span class="bc-item bc-current">' + escHtml(part) + '</span>';
            } else {
                html += '<button type="button" class="bc-item" data-subpath="' + escHtml(sp) + '">' + escHtml(part) + '</button>';
            }
        });

        breadcrumb.innerHTML = html;

        breadcrumb.querySelectorAll('.bc-item[data-subpath]').forEach(function (el) {
            el.addEventListener('click', function () {
                loadTree(key, el.dataset.subpath);
            });
        });
    }

    // ── File Tree ────────────────────────────────────────────
    function loadTree(key, subpath) {
        currentKey     = key;
        currentSubpath = subpath || '';

        closeViewer();
        fileTree.innerHTML = '<div class="file-tree-loading">Chargement…</div>';
        fileTree.setAttribute('aria-busy', 'true');
        renderBreadcrumb(key, currentSubpath);

        fetch('/Worker/ServiceFiles?key=' + encodeURIComponent(key) +
              (currentSubpath ? '&subpath=' + encodeURIComponent(currentSubpath) : ''))
            .then(function (r) { return r.json(); })
            .then(function (entries) {
                fileTree.setAttribute('aria-busy', 'false');
                renderTree(entries);
            })
            .catch(function () {
                fileTree.setAttribute('aria-busy', 'false');
                fileTree.innerHTML = '<div class="file-tree-error">Erreur lors du chargement.</div>';
            });
    }

    function renderTree(entries) {
        if (!entries.length) {
            fileTree.innerHTML = '<div class="file-tree-empty">Ce dossier est vide.</div>';
            return;
        }

        var html = '';
        entries.forEach(function (entry) {
            if (entry.isDirectory) {
                html += '<button type="button" class="file-tree-item file-tree-dir" data-subpath="' + escHtml(entry.relativePath) + '" aria-label="Ouvrir le dossier ' + escHtml(entry.name) + '">' +
                    '<svg class="ft-icon" viewBox="0 0 24 24" fill="none" xmlns="http://www.w3.org/2000/svg">' +
                    '<path d="M3 7a2 2 0 0 1 2-2h4l2 2h8a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V7z" stroke="currentColor" stroke-width="1.5" stroke-linejoin="round"/></svg>' +
                    '<span class="ft-name">' + escHtml(entry.name) + '</span>' +
                    '<svg class="ft-chevron" viewBox="0 0 24 24" fill="none"><path d="M9 6l6 6-6 6" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round"/></svg>' +
                    '</button>';
            } else {
                var canView = isTextFile(entry.name);
                html += '<button type="button" class="file-tree-item file-tree-file' + (canView ? '' : ' is-binary') + '"' +
                    (canView ? ' data-relpath="' + escHtml(entry.relativePath) + '" aria-label="Lire le fichier ' + escHtml(entry.name) + '"' : ' disabled aria-label="Fichier binaire ' + escHtml(entry.name) + '"') + '>' +
                    '<svg class="ft-icon" viewBox="0 0 24 24" fill="none" xmlns="http://www.w3.org/2000/svg">' +
                    '<path d="M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8l-6-6z" stroke="currentColor" stroke-width="1.5" stroke-linejoin="round"/>' +
                    '<path d="M14 2v6h6" stroke="currentColor" stroke-width="1.5" stroke-linejoin="round"/>' +
                    '</svg>' +
                    '<span class="ft-name">' + escHtml(entry.name) + '</span>' +
                    '<span class="ft-size">' + formatSize(entry.size) + '</span>' +
                    '</button>';
            }
        });

        fileTree.innerHTML = html;

        fileTree.querySelectorAll('.file-tree-dir').forEach(function (el) {
            el.addEventListener('click', function () {
                loadTree(currentKey, el.dataset.subpath);
            });
        });

        fileTree.querySelectorAll('.file-tree-file:not(.is-binary)').forEach(function (el) {
            el.addEventListener('click', function () {
                openFile(el.dataset.relpath);
            });
        });
    }

    // ── File Viewer ──────────────────────────────────────────
    function openFile(relativePath) {
        var name = relativePath.replace(/\\/g, '/').split('/').pop();
        fileViewerName.textContent = name;
        fileViewerPre.textContent  = 'Chargement…';
        fileViewer.classList.remove('d-none');

        fetch('/Worker/ServiceFileContent?key=' + encodeURIComponent(currentKey) +
              '&path=' + encodeURIComponent(relativePath))
            .then(function (r) {
                if (!r.ok) throw new Error('not ok');
                return r.text();
            })
            .then(function (text) {
                fileViewerPre.textContent = text;
            })
            .catch(function () {
                fileViewerPre.textContent = '(Impossible de lire ce fichier)';
            });
    }

    function closeViewer() {
        fileViewer.classList.add('d-none');
        fileViewerPre.textContent = '';
    }

    if (fileViewerClose) {
        fileViewerClose.addEventListener('click', closeViewer);
    }

    // ── Service selection ────────────────────────────────────
    sidebarList.querySelectorAll('.files-service-item').forEach(function (btn) {
        btn.addEventListener('click', function () {
            sidebarList.querySelectorAll('.files-service-item').forEach(function (b) {
                b.classList.remove('is-active');
            });
            btn.classList.add('is-active');

            emptyState.classList.add('d-none');
            filesContent.classList.remove('d-none');

            loadTree(btn.dataset.key, '');
        });
    });
})();
