(function () {
    'use strict';

    var modal = document.getElementById('addServiceModal');
    if (!modal) return;

    var dropZone = document.getElementById('serviceDropZone');
    var folderInput = document.getElementById('serviceFolderInput');
    var dropState = document.getElementById('serviceDropState');
    var dropStateTitle = document.getElementById('serviceDropStateTitle');
    var dropStateText = document.getElementById('serviceDropStateText');
    var uploadProgressBar = document.getElementById('serviceUploadProgressBar');
    var uploadTokenInput = document.getElementById('serviceUploadToken');
    var uploadedExeInput = document.getElementById('uploadedExeRelativePath');
    var executablePanel = document.getElementById('uploadedExecutablePanel');
    var executableSelect = document.getElementById('uploadedExecutableSelect');
    var executableHelp = document.getElementById('uploadedExecutableHelp');
    var advancedPathToggle = document.getElementById('advancedPathToggle');
    var advancedPathSection = document.getElementById('advancedPathSection');
    var exePathInput = document.getElementById('exePathInput');
    var browseToggleBtn = document.getElementById('browseToggleBtn');
    var fileBrowserPanel = document.getElementById('fileBrowserPanel');
    var fileBrowserPath = document.getElementById('fileBrowserPath');
    var fileBrowserList = document.getElementById('fileBrowserList');
    var detectStatus = document.getElementById('detectStatus');
    var detectResult = document.getElementById('detectResult');
    var manualToggleBtn = document.getElementById('manualToggleBtn');
    var manualSection = document.getElementById('manualSection');
    var manualRows = document.getElementById('manualRows');
    var addManualRowBtn = document.getElementById('addManualRowBtn');
    var submitBtn = document.getElementById('submitAddServiceBtn');
    var messageEl = document.getElementById('addServiceMessage');
    var antiforgeryInput = document.querySelector('#addServiceAntiforgery input[name="__RequestVerificationToken"]');

    var ignoredFolders = ['.git', '.vs', 'node_modules', 'packages', 'obj', 'testresults', '.settings-backups'];
    var lastDetectedKey = null;
    var uploadRequest = null;
    var dragDepth = 0;
    var uploadCompleted = false;

    function antiforgeryHeaders(headers) {
        var result = headers || {};
        if (antiforgeryInput) result.RequestVerificationToken = antiforgeryInput.value;
        return result;
    }

    function escapeHtml(value) {
        var div = document.createElement('div');
        div.textContent = value == null ? '' : String(value);
        return div.innerHTML;
    }

    function setMessage(text, isError) {
        messageEl.textContent = text || '';
        messageEl.classList.toggle('is-error', Boolean(isError));
    }

    function setDropState(state, title, detail, progress) {
        dropState.classList.remove('d-none', 'is-uploading', 'is-success', 'is-error');
        if (state) dropState.classList.add('is-' + state);
        dropStateTitle.textContent = title || '';
        dropStateText.textContent = detail || '';
        uploadProgressBar.style.width = Math.max(0, Math.min(100, progress || 0)) + '%';
    }

    function formatBytes(bytes) {
        if (!bytes) return '0 Ko';
        if (bytes >= 1024 * 1024) return (bytes / (1024 * 1024)).toFixed(1).replace('.', ',') + ' Mo';
        return Math.max(1, Math.round(bytes / 1024)) + ' Ko';
    }

    function newManualRow() {
        var row = document.createElement('div');
        row.className = 'manual-row';
        row.innerHTML = '<input type="text" class="form-control" placeholder="Clé du service" data-role="manual-key" aria-label="Clé du service" />' +
            '<input type="text" class="form-control" placeholder="Nom affiché (optionnel)" data-role="manual-name" aria-label="Nom affiché du service" />';
        return row;
    }

    function discardUpload(token) {
        if (!token) return Promise.resolve();

        return fetch('/Worker/DiscardServiceUpload', {
            method: 'POST',
            headers: antiforgeryHeaders({ 'Content-Type': 'application/json' }),
            body: JSON.stringify({ uploadToken: token, executableRelativePath: '' }),
            keepalive: true
        }).catch(function () { /* the server expiry cleanup is the fallback */ });
    }

    function clearUploadedProject(shouldDiscard) {
        var token = uploadTokenInput.value;
        uploadTokenInput.value = '';
        uploadedExeInput.value = '';
        executablePanel.classList.add('d-none');
        executableSelect.innerHTML = '';
        lastDetectedKey = null;

        if (shouldDiscard && token) discardUpload(token);
    }

    function resetModal() {
        if (uploadRequest) {
            uploadRequest.abort();
            uploadRequest = null;
        }

        clearUploadedProject(true);
        uploadCompleted = false;
        dragDepth = 0;
        folderInput.value = '';
        dropZone.classList.remove('is-dragging', 'is-busy');
        dropState.className = 'service-drop-state d-none';
        uploadProgressBar.style.width = '0%';
        exePathInput.value = '';
        advancedPathSection.classList.add('d-none');
        advancedPathToggle.setAttribute('aria-expanded', 'false');
        fileBrowserPanel.classList.add('d-none');
        fileBrowserList.innerHTML = '';
        detectStatus.innerHTML = '';
        detectResult.innerHTML = '';
        manualSection.classList.add('d-none');
        manualRows.innerHTML = '';
        manualRows.appendChild(newManualRow());
        submitBtn.disabled = false;
        setMessage('', false);
    }

    modal.addEventListener('show.bs.modal', resetModal);
    modal.addEventListener('hidden.bs.modal', function () {
        if (!uploadCompleted) clearUploadedProject(true);
    });

    function loadBrowser(path) {
        var url = '/Worker/Browse' + (path ? '?path=' + encodeURIComponent(path) : '');

        fetch(url)
            .then(function (response) {
                if (!response.ok) throw new Error();
                return response.json();
            })
            .then(function (data) {
                fileBrowserPath.textContent = data.currentPath || 'Lecteurs';
                fileBrowserList.innerHTML = '';

                if (data.parentPath !== null && data.parentPath !== undefined) {
                    fileBrowserList.appendChild(makeBrowserRow('.. (dossier parent)', data.parentPath, 'dir'));
                }

                (data.directories || []).forEach(function (directory) {
                    fileBrowserList.appendChild(makeBrowserRow(directory.name, directory.path, 'dir'));
                });

                (data.executables || []).forEach(function (file) {
                    fileBrowserList.appendChild(makeBrowserRow(file.name, file.path, 'exe'));
                });

                if (!fileBrowserList.children.length) {
                    fileBrowserList.innerHTML = '<li class="file-browser-empty">Dossier vide.</li>';
                }
            })
            .catch(function () {
                fileBrowserList.innerHTML = '<li class="file-browser-empty">Impossible de lire ce dossier.</li>';
            });
    }

    function makeBrowserRow(name, path, type) {
        var row = document.createElement('li');
        row.className = 'file-browser-item';
        row.innerHTML = '<span class="file-browser-item-icon" aria-hidden="true">' + (type === 'dir' ? 'DIR' : 'EXE') + '</span><span>' + escapeHtml(name) + '</span>';

        row.addEventListener('click', function () {
            if (type === 'dir') {
                loadBrowser(path);
                return;
            }

            clearUploadedProject(true);
            exePathInput.value = path;
            fileBrowserPanel.classList.add('d-none');
            detectForLocalPath(path);
        });

        return row;
    }

    browseToggleBtn.addEventListener('click', function () {
        var show = fileBrowserPanel.classList.contains('d-none');
        fileBrowserPanel.classList.toggle('d-none');
        if (show) loadBrowser(null);
    });

    advancedPathToggle.addEventListener('click', function () {
        var show = advancedPathSection.classList.contains('d-none');
        advancedPathSection.classList.toggle('d-none');
        advancedPathToggle.setAttribute('aria-expanded', show ? 'true' : 'false');
    });

    function renderDetectResults(services) {
        if (!services || services.length === 0) {
            detectResult.innerHTML = '';
            return;
        }

        detectResult.innerHTML = '<div class="detect-result-heading"><span class="modal-guide-number">2</span><div><strong>Services détectés</strong><p>Décochez les flux que vous ne souhaitez pas ajouter.</p></div></div>' +
            services.map(function (key) {
                var safeKey = escapeHtml(key);
                return '<div class="detect-row">' +
                    '<input type="checkbox" class="form-check-input" data-role="detect-check" checked aria-label="Ajouter le service ' + safeKey + '" />' +
                    '<input type="text" class="form-control detect-key-input" value="' + safeKey + '" data-role="detect-key" readonly aria-label="Clé du service détecté" />' +
                    '<input type="text" class="form-control" placeholder="Nom affiché (optionnel)" data-role="detect-name" aria-label="Nom affiché du service ' + safeKey + '" />' +
                    '</div>';
            }).join('');
    }

    function showDetectionResponse(data) {
        if (data.success) {
            var methodLabel = data.method === 'source'
                ? 'identifié(s) dans le code source, sans exécuter le programme.'
                : 'identifié(s) à partir de l’exécutable.';
            detectStatus.innerHTML = '<span class="add-service-status-ok">' + data.services.length + ' service(s) ' + methodLabel + '</span>';
            renderDetectResults(data.services);
            manualSection.classList.add('d-none');
            return;
        }

        detectStatus.innerHTML = '<span class="add-service-status-warn">' +
            escapeHtml(data.error || 'Aucun service détecté automatiquement.') +
            ' Vous pouvez l’ajouter manuellement ci-dessous.</span>';
        detectResult.innerHTML = '';
        manualSection.classList.remove('d-none');
    }

    function detectForLocalPath(exePath) {
        var key = 'local:' + exePath;
        if (!exePath || key === lastDetectedKey) return;

        lastDetectedKey = key;
        detectStatus.innerHTML = '<span class="add-service-status-loading">Détection des services en cours…</span>';
        detectResult.innerHTML = '';

        fetch('/Worker/DetectServices', {
            method: 'POST',
            headers: antiforgeryHeaders({ 'Content-Type': 'application/json' }),
            body: JSON.stringify({ exePath: exePath })
        })
            .then(function (response) { return response.json(); })
            .then(showDetectionResponse)
            .catch(function () {
                showDetectionResponse({ success: false, error: 'Détection impossible.' });
            });
    }

    function detectUploadedExecutable(relativePath) {
        var token = uploadTokenInput.value;
        var key = 'upload:' + token + ':' + relativePath;
        if (!token || !relativePath || key === lastDetectedKey) return;

        uploadedExeInput.value = relativePath;
        lastDetectedKey = key;
        detectStatus.innerHTML = '<span class="add-service-status-loading">Détection des services en cours…</span>';
        detectResult.innerHTML = '';

        fetch('/Worker/DetectUploadedServices', {
            method: 'POST',
            headers: antiforgeryHeaders({ 'Content-Type': 'application/json' }),
            body: JSON.stringify({ uploadToken: token, executableRelativePath: relativePath })
        })
            .then(function (response) {
                return response.json().then(function (data) {
                    if (!response.ok) throw new Error(data.error || 'Détection impossible.');
                    return data;
                });
            })
            .then(showDetectionResponse)
            .catch(function (error) {
                showDetectionResponse({ success: false, error: error.message });
            });
    }

    exePathInput.addEventListener('blur', function () {
        var path = exePathInput.value.trim();
        if (path) {
            clearUploadedProject(true);
            detectForLocalPath(path);
        }
    });

    exePathInput.addEventListener('keydown', function (event) {
        if (event.key !== 'Enter') return;
        event.preventDefault();
        var path = exePathInput.value.trim();
        if (path) {
            clearUploadedProject(true);
            detectForLocalPath(path);
        }
    });

    function isIgnoredPath(path) {
        return path.replace(/\\/g, '/').split('/').some(function (segment) {
            return ignoredFolders.indexOf(segment.toLowerCase()) !== -1;
        });
    }

    function normalizeSelection(items) {
        var unique = {};
        return items.filter(function (item) {
            if (!item.file || isIgnoredPath(item.relativePath)) return false;
            var key = item.relativePath.toLowerCase();
            if (unique[key]) return false;
            unique[key] = true;
            return true;
        });
    }

    function fileFromEntry(entry) {
        return new Promise(function (resolve, reject) {
            entry.file(resolve, reject);
        });
    }

    function readDirectoryEntries(reader) {
        return new Promise(function (resolve, reject) {
            var all = [];

            function readBatch() {
                reader.readEntries(function (entries) {
                    if (!entries.length) {
                        resolve(all);
                        return;
                    }
                    all = all.concat(Array.prototype.slice.call(entries));
                    readBatch();
                }, reject);
            }

            readBatch();
        });
    }

    function walkEntry(entry, prefix, files) {
        var relativePath = prefix ? prefix + '/' + entry.name : entry.name;
        if (isIgnoredPath(relativePath)) return Promise.resolve();

        if (entry.isFile) {
            return fileFromEntry(entry).then(function (file) {
                files.push({ file: file, relativePath: relativePath });
            });
        }

        if (!entry.isDirectory) return Promise.resolve();

        return readDirectoryEntries(entry.createReader()).then(function (entries) {
            return Promise.all(entries.map(function (child) {
                return walkEntry(child, relativePath, files);
            }));
        });
    }

    function collectDroppedFiles(dataTransfer) {
        var entries = [];
        var items = Array.prototype.slice.call(dataTransfer.items || []);

        items.forEach(function (item) {
            var entry = item.webkitGetAsEntry ? item.webkitGetAsEntry() : null;
            if (entry) entries.push(entry);
        });

        if (!entries.length) {
            return Promise.resolve(Array.prototype.slice.call(dataTransfer.files || []).map(function (file) {
                return { file: file, relativePath: file.name };
            }));
        }

        var files = [];
        return Promise.all(entries.map(function (entry) {
            return walkEntry(entry, '', files);
        })).then(function () { return files; });
    }

    function renderExecutableChoices(executables) {
        executableSelect.innerHTML = '';

        executables.forEach(function (candidate) {
            var option = document.createElement('option');
            option.value = candidate.relativePath;
            option.textContent = candidate.fileName + ' — ' + candidate.folder;
            option.selected = Boolean(candidate.recommended);
            executableSelect.appendChild(option);
        });

        executablePanel.classList.remove('d-none');
        executableHelp.textContent = executables.length > 1
            ? executables.length + ' exécutables trouvés. Vérifiez la sélection proposée.'
            : 'Un exécutable trouvé et sélectionné automatiquement.';
        executableSelect.disabled = executables.length === 1;
        detectUploadedExecutable(executableSelect.value);
    }

    function uploadProject(items) {
        items = normalizeSelection(items);

        if (!items.length) {
            setDropState('error', 'Dossier non reconnu', 'Aucun fichier exploitable n’a été trouvé.', 0);
            return;
        }

        if (items.length > 5000) {
            setDropState('error', 'Projet trop volumineux', 'La limite est de 5 000 fichiers.', 0);
            return;
        }

        if (uploadRequest) uploadRequest.abort();
        clearUploadedProject(true);
        exePathInput.value = '';
        detectStatus.innerHTML = '';
        detectResult.innerHTML = '';
        setMessage('', false);

        var totalBytes = items.reduce(function (sum, item) { return sum + item.file.size; }, 0);
        setDropState('uploading', 'Importation et analyse…', items.length + ' fichiers · ' + formatBytes(totalBytes), 2);
        dropZone.classList.add('is-busy');
        submitBtn.disabled = true;

        var formData = new FormData();
        items.forEach(function (item) {
            formData.append('files', item.file, item.file.name);
            formData.append('relativePaths', item.relativePath);
        });

        var xhr = new XMLHttpRequest();
        uploadRequest = xhr;
        xhr.open('POST', '/Worker/UploadServiceFolder');
        if (antiforgeryInput) xhr.setRequestHeader('RequestVerificationToken', antiforgeryInput.value);

        xhr.upload.addEventListener('progress', function (event) {
            if (!event.lengthComputable) return;
            var percentage = Math.max(3, Math.round((event.loaded / event.total) * 92));
            uploadProgressBar.style.width = percentage + '%';
        });

        xhr.addEventListener('load', function () {
            uploadRequest = null;
            dropZone.classList.remove('is-busy');
            submitBtn.disabled = false;

            var data;
            try {
                data = JSON.parse(xhr.responseText);
            } catch (_) {
                data = { success: false, error: 'Réponse du serveur illisible.' };
            }

            if (xhr.status < 200 || xhr.status >= 300 || !data.success) {
                setDropState('error', 'Analyse impossible', data.error || 'Le projet n’a pas pu être importé.', 0);
                return;
            }

            uploadTokenInput.value = data.uploadToken;
            setDropState('success', 'Projet prêt', data.fileCount + ' fichiers analysés · ' + data.executables.length + ' exécutable(s) trouvé(s)', 100);
            renderExecutableChoices(data.executables || []);
        });

        xhr.addEventListener('error', function () {
            uploadRequest = null;
            dropZone.classList.remove('is-busy');
            submitBtn.disabled = false;
            setDropState('error', 'Importation interrompue', 'Vérifiez la taille du projet et réessayez.', 0);
        });

        xhr.addEventListener('abort', function () {
            uploadRequest = null;
            dropZone.classList.remove('is-busy');
            submitBtn.disabled = false;
        });

        xhr.send(formData);
    }

    dropZone.addEventListener('click', function () {
        if (!dropZone.classList.contains('is-busy')) folderInput.click();
    });

    dropZone.addEventListener('keydown', function (event) {
        if (event.key === 'Enter' || event.key === ' ') {
            event.preventDefault();
            if (!dropZone.classList.contains('is-busy')) folderInput.click();
        }
    });

    folderInput.addEventListener('change', function () {
        var files = Array.prototype.slice.call(folderInput.files || []).map(function (file) {
            return { file: file, relativePath: file.webkitRelativePath || file.name };
        });
        uploadProject(files);
        folderInput.value = '';
    });

    ['dragenter', 'dragover', 'dragleave', 'drop'].forEach(function (eventName) {
        dropZone.addEventListener(eventName, function (event) {
            event.preventDefault();
            event.stopPropagation();
        });
    });

    dropZone.addEventListener('dragenter', function () {
        dragDepth += 1;
        dropZone.classList.add('is-dragging');
    });

    dropZone.addEventListener('dragover', function (event) {
        event.dataTransfer.dropEffect = 'copy';
    });

    dropZone.addEventListener('dragleave', function () {
        dragDepth = Math.max(0, dragDepth - 1);
        if (dragDepth === 0) dropZone.classList.remove('is-dragging');
    });

    dropZone.addEventListener('drop', function (event) {
        dragDepth = 0;
        dropZone.classList.remove('is-dragging');
        if (dropZone.classList.contains('is-busy')) return;

        setDropState('uploading', 'Lecture du dossier…', 'Préparation des fichiers à analyser.', 1);
        collectDroppedFiles(event.dataTransfer)
            .then(uploadProject)
            .catch(function () {
                setDropState('error', 'Lecture impossible', 'Ce dossier ne peut pas être lu par le navigateur.', 0);
            });
    });

    executableSelect.addEventListener('change', function () {
        lastDetectedKey = null;
        detectUploadedExecutable(executableSelect.value);
    });

    manualToggleBtn.addEventListener('click', function () {
        manualSection.classList.toggle('d-none');
    });

    addManualRowBtn.addEventListener('click', function () {
        manualRows.appendChild(newManualRow());
    });

    submitBtn.addEventListener('click', function () {
        var uploadToken = uploadTokenInput.value;
        var uploadedExe = uploadedExeInput.value;
        var exePath = exePathInput.value.trim();

        if ((!uploadToken || !uploadedExe) && !exePath) {
            setMessage('Déposez un projet ou indiquez le chemin d’un fichier .exe.', true);
            return;
        }

        var services = [];
        detectResult.querySelectorAll('.detect-row').forEach(function (row) {
            if (!row.querySelector('[data-role="detect-check"]').checked) return;
            var key = row.querySelector('[data-role="detect-key"]').value.trim();
            var name = row.querySelector('[data-role="detect-name"]').value.trim();
            if (key) services.push({ key: key, displayName: name });
        });

        manualRows.querySelectorAll('.manual-row').forEach(function (row) {
            var key = row.querySelector('[data-role="manual-key"]').value.trim();
            var name = row.querySelector('[data-role="manual-name"]').value.trim();
            if (key) services.push({ key: key, displayName: name });
        });

        if (!services.length) {
            setMessage('Sélectionnez ou ajoutez au moins un service avant de continuer.', true);
            return;
        }

        submitBtn.disabled = true;
        setMessage('Ajout des services à FlowOps…', false);

        fetch('/Worker/AddServices', {
            method: 'POST',
            headers: antiforgeryHeaders({ 'Content-Type': 'application/json' }),
            body: JSON.stringify({
                exePath: exePath,
                uploadToken: uploadToken,
                uploadedExeRelativePath: uploadedExe,
                services: services
            })
        })
            .then(function (response) {
                return response.json().then(function (data) {
                    if (!response.ok && !data.errors) throw new Error('Erreur lors de l’ajout.');
                    return data;
                });
            })
            .then(function (data) {
                if (data.success) {
                    uploadCompleted = true;
                    uploadTokenInput.value = '';
                    window.location.reload();
                    return;
                }

                submitBtn.disabled = false;
                setMessage((data.errors || []).join(' / ') || 'Erreur lors de l’ajout.', true);
            })
            .catch(function (error) {
                submitBtn.disabled = false;
                setMessage(error.message || 'Erreur lors de l’ajout.', true);
            });
    });
})();
