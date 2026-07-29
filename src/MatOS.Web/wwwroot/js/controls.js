// Matstat UI-Controls – Vanilla JS, per Instanz-ID (data-mat-*) gescopt.
(function () {
    // Tab-Umschaltung (Client-Modus)
    document.addEventListener('click', function (e) {
        const btn = e.target.closest('.mat-tab');
        if (!btn || btn.hasAttribute('disabled')) return;
        const bar = btn.closest('[data-mat-tabbar]');
        if (!bar) return;
        const key = btn.getAttribute('data-mat-tab');

        bar.querySelectorAll('.mat-tab').forEach(function (b) {
            const on = b === btn;
            b.classList.toggle('active', on);
            b.setAttribute('aria-selected', on ? 'true' : 'false');
        });
        bar.querySelectorAll('[data-mat-panel]').forEach(function (p) {
            const on = p.getAttribute('data-mat-panel') === key;
            p.classList.toggle('active', on);
            if (on) p.removeAttribute('hidden'); else p.setAttribute('hidden', '');
        });
    });
})();
