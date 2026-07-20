// v1.10.53 — moderne Ersätze für window.confirm / window.alert /
// eine kleine Toast-Komponente. Alle drei rendern DOM-basiert unter
// Nutzung des vorhandenen .modal-backdrop/.modal-panel Patterns
// (siehe site.css). Kein CSS-Reload nötig, kein neuer Asset-Load.
//
// API:
//   await window.nimConfirm(message, { title?, confirmText?, cancelText?, danger?, i18nKey? })
//     → resolves to true (confirmed) or false (cancelled)
//   await window.nimAlert(message, { title?, kind?, buttonText? })
//     → resolves to true when user closes
//   window.nimToast(message, { kind?, durationMs? })
//     → non-blocking, fades out. kind: 'success' | 'info' | 'warning' | 'error'
//
// Fallback: wenn irgendwas beim Rendern schiefgeht, greift native
// confirm()/alert() ein — nichts geht verloren. Beim ersten Aufruf wird
// ein Container ins DOM gehängt; Wiederverwendung danach ohne Extra-Kosten.

(function () {
    if (window.__nimDialogsReady) return;
    window.__nimDialogsReady = true;

    // Übersetzungen — via <html data-i18n="..."> injection oder Fallback
    // pro Sprache. Der Server rendert `<html lang="xx">`; Fallback auf
    // Englisch wenn Sprache unbekannt.
    const LOCALE_LABELS = {
        de: { ok: 'OK', cancel: 'Abbrechen', confirm: 'Bestätigen', delete: 'Löschen' },
        en: { ok: 'OK', cancel: 'Cancel', confirm: 'Confirm', delete: 'Delete' },
        fr: { ok: 'OK', cancel: 'Annuler', confirm: 'Confirmer', delete: 'Supprimer' },
        it: { ok: 'OK', cancel: 'Annulla', confirm: 'Conferma', delete: 'Elimina' },
        es: { ok: 'OK', cancel: 'Cancelar', confirm: 'Confirmar', delete: 'Eliminar' },
        nl: { ok: 'OK', cancel: 'Annuleren', confirm: 'Bevestigen', delete: 'Verwijderen' },
    };
    function labels() {
        const lang = (document.documentElement.lang || 'en').slice(0, 2).toLowerCase();
        return LOCALE_LABELS[lang] || LOCALE_LABELS.en;
    }

    function esc(s) {
        return String(s == null ? '' : s).replace(/[&<>"']/g, c =>
            ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
    }

    // Toast Container — einmal anlegen, danach reused.
    function ensureToastRoot() {
        let root = document.getElementById('nimToastRoot');
        if (!root) {
            root = document.createElement('div');
            root.id = 'nimToastRoot';
            root.setAttribute('aria-live', 'polite');
            root.setAttribute('role', 'status');
            document.body.appendChild(root);
        }
        return root;
    }

    window.nimToast = function (message, opts) {
        try {
            const o = opts || {};
            const kind = o.kind || 'info';
            const dur = typeof o.durationMs === 'number' ? o.durationMs : 3800;
            const root = ensureToastRoot();
            const el = document.createElement('div');
            el.className = 'nim-toast nim-toast-' + kind;
            el.textContent = String(message == null ? '' : message);
            root.appendChild(el);
            // Auto-remove
            setTimeout(() => {
                el.classList.add('nim-toast-fade');
                setTimeout(() => el.remove(), 320);
            }, dur);
        } catch (e) {
            // Silent fail — Toast ist non-critical, wir wollen keinen Alert-Kaskade
            console.warn('nimToast failed:', e);
        }
    };

    // Gemeinsame Basis: baut einen modal-backdrop mit Panel, gibt Promise
    // zurück die per resolveFn(val) beendet wird. `kind` ist optional und
    // steuert das Icon oben ('confirm' / 'alert' / 'success' / …).
    function openDialog(opts) {
        return new Promise(function (resolve) {
            try {
                const l = labels();
                const title = opts.title || '';
                const message = opts.message || '';
                const primaryText = opts.primaryText || l.ok;
                const cancelText = opts.cancelText || l.cancel;
                const isConfirm = !!opts.isConfirm;
                const danger = !!opts.danger;
                const kind = opts.kind || (isConfirm ? 'question' : 'info');

                const icons = { question: '❓', info: 'ℹ', warning: '⚠', error: '✕', success: '✓' };
                const icon = icons[kind] || icons.info;

                const backdrop = document.createElement('div');
                backdrop.className = 'modal-backdrop open nim-dialog-backdrop';
                backdrop.innerHTML =
                    '<div class="modal-panel nim-dialog-panel" role="dialog" aria-modal="true">' +
                        '<div class="nim-dialog-head">' +
                            '<span class="nim-dialog-icon nim-dialog-icon-' + esc(kind) + '">' + esc(icon) + '</span>' +
                            (title ? '<h2 class="nim-dialog-title">' + esc(title) + '</h2>' : '') +
                        '</div>' +
                        '<div class="nim-dialog-body">' + esc(message).replace(/\n/g, '<br>') + '</div>' +
                        '<div class="nim-dialog-actions">' +
                            (isConfirm ? '<button type="button" class="btn btn-ghost nim-dialog-cancel">' + esc(cancelText) + '</button>' : '') +
                            '<button type="button" class="btn ' + (danger ? 'nim-dialog-primary-danger' : 'btn-primary') + ' nim-dialog-primary">' + esc(primaryText) + '</button>' +
                        '</div>' +
                    '</div>';

                function close(val) {
                    document.removeEventListener('keydown', onKey);
                    backdrop.classList.remove('open');
                    setTimeout(() => { try { backdrop.remove(); } catch {} }, 160);
                    resolve(val);
                }

                function onKey(ev) {
                    if (ev.key === 'Escape') { ev.preventDefault(); close(isConfirm ? false : true); }
                    else if (ev.key === 'Enter' && document.activeElement?.tagName !== 'TEXTAREA') {
                        // v1.10.55 Safety: bei danger-Confirms fokussiert
                        // openDialog absichtlich den Cancel-Button. Wenn der
                        // User dann Enter drückt, resolve false (nicht true) —
                        // sonst wäre der Delete-Confirm mit Enter genauso
                        // gefährlich wie ohne Dialog. Enter auf einem konkret
                        // fokussierten Button folgt dem Button (activeElement
                        // check), sonst falls kein Focus → sicher-Seite (Cancel
                        // bei danger, OK sonst).
                        ev.preventDefault();
                        const active = document.activeElement;
                        if (isConfirm && active?.classList.contains('nim-dialog-cancel')) {
                            close(false);
                        } else if (isConfirm && danger && !active?.classList.contains('nim-dialog-primary')) {
                            close(false);
                        } else {
                            close(true);
                        }
                    }
                }

                document.body.appendChild(backdrop);
                backdrop.addEventListener('click', (ev) => {
                    if (ev.target === backdrop) close(isConfirm ? false : true);
                });
                backdrop.querySelector('.nim-dialog-primary').addEventListener('click', () => close(true));
                if (isConfirm) {
                    backdrop.querySelector('.nim-dialog-cancel').addEventListener('click', () => close(false));
                }
                document.addEventListener('keydown', onKey);
                // Focus setzen — Cancel bei destruktiven, sonst Primary
                setTimeout(() => {
                    const target = danger
                        ? backdrop.querySelector('.nim-dialog-cancel')
                        : backdrop.querySelector('.nim-dialog-primary');
                    target?.focus();
                }, 30);
            } catch (e) {
                // Wenn irgendwas beim Render kracht → native fallback
                console.warn('nim-dialog render failed, falling back to native:', e);
                if (opts.isConfirm) resolve(window.confirm(opts.message));
                else { window.alert(opts.message); resolve(true); }
            }
        });
    }

    window.nimConfirm = function (message, opts) {
        const o = opts || {};
        return openDialog({
            message: message,
            title: o.title || '',
            primaryText: o.confirmText,
            cancelText: o.cancelText,
            isConfirm: true,
            danger: !!o.danger,
            kind: o.danger ? 'warning' : 'question',
        });
    };

    window.nimAlert = function (message, opts) {
        const o = opts || {};
        return openDialog({
            message: message,
            title: o.title || '',
            primaryText: o.buttonText,
            isConfirm: false,
            kind: o.kind || 'info',
        });
    };
})();
