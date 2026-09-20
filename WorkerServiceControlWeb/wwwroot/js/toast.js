window.AppToast = (function () {
    var container = null;

    function show(title, sub, type, duration) {
        if (!container) container = document.getElementById('toastContainer');
        if (!container) return;

        var el = document.createElement('div');
        el.className = 'toast-notif toast-notif-' + (type || 'info');
        el.innerHTML =
            '<span class="toast-dot"></span>' +
            '<div class="toast-body"><div class="toast-title">' + esc(title) + '</div>' +
            (sub ? '<div class="toast-sub">' + esc(sub) + '</div>' : '') + '</div>' +
            '<button class="toast-close" aria-label="Fermer">&#x2715;</button>';

        el.querySelector('.toast-close').addEventListener('click', function () { dismiss(el); });
        container.appendChild(el);

        requestAnimationFrame(function () {
            requestAnimationFrame(function () { el.classList.add('is-visible'); });
        });

        var ms = (duration !== undefined) ? duration : (type === 'error' ? 8000 : 4000);
        if (ms > 0) setTimeout(function () { dismiss(el); }, ms);
    }

    function dismiss(el) {
        el.classList.remove('is-visible');
        el.classList.add('is-hiding');
        setTimeout(function () { if (el.parentNode) el.parentNode.removeChild(el); }, 280);
    }

    function esc(s) {
        var d = document.createElement('div');
        d.textContent = s;
        return d.innerHTML;
    }

    return { show: show };
})();
