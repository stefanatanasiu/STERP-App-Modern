/*
 * STERP-WEB browser helpers (classic script, loaded in <head> before the WebAssembly runtime starts).
 *
 * 1. sterpActivity - busy signal for automation that waits for "network idle" after every action (the Agent 07 GUI
 *    parity driver). Playwright reports network idle only once per document, so while the shell is busy (start-up, an
 *    API call, a running job) a hidden about:blank frame with a pending request to /_sterp/hold is kept attached:
 *    Playwright does not consider a page idle while a child frame is busy, so its next wait lasts until the work has
 *    been rendered. Clicks, command keys and selections hold the signal for a short grace period; a modal dialog that
 *    waits for the operator releases it. The signal never changes what the application does (DEVELOPER-GUIDE section 10).
 * 2. sterp - keyboard routing (function keys, menu shortcuts and access keys go to the shell; browser defaults such as
 *    F5 reload or F3 find are suppressed), focus, printing and small DOM helpers used through JS interop.
 */
(function () {
    'use strict';
    if (window.sterpActivity) { return; }

    var count = 0, pauses = 0, frame = null, timer = null, busySince = 0;
    var origFetch = window.fetch ? window.fetch.bind(window) : null;

    // the keeper frame keeps one request to /_sterp/hold pending from its own window: a frame with a pending request is
    // never idle, also while the main thread is blocked by WebAssembly work (start-up, rendering a large grid)
    function hold(f) {
        var w = f.contentWindow;
        if (!w || !w.fetch) { return; }
        try {
            w.fetch(location.origin + '/_sterp/hold', { cache: 'no-store', credentials: 'omit' }).then(function () {
                if (frame === f && timer) { hold(f); }
            }, function () {
                if (frame === f && timer) { setTimeout(function () { if (frame === f && timer) { renew(); } }, 200); }
            });
        } catch (e) { }
    }

    function renew() {
        var f = document.createElement('iframe');
        f.setAttribute('aria-hidden', 'true');
        f.setAttribute('tabindex', '-1');
        f.setAttribute('title', 'activity');
        f.className = 'sterp-activity';
        f.style.cssText = 'position:fixed;left:-20px;top:-20px;width:1px;height:1px;border:0;visibility:hidden;pointer-events:none';
        (document.body || document.documentElement).appendChild(f);
        if (frame && frame.parentNode) { frame.parentNode.removeChild(frame); }
        frame = f;
        hold(f);
    }

    function watch() {
        if (busySince && Date.now() - busySince > 120000) {
            // never hold the signal for ever (failed start-up, lost continuation)
            count = 0; pauses = 0; update();
        }
    }

    function start() {
        busySince = Date.now();
        timer = setInterval(watch, 1000);
        renew();
    }

    function stop() {
        clearInterval(timer);
        timer = null;
        busySince = 0;
        if (origFetch) {
            try { origFetch('/_sterp/settle', { cache: 'no-store', credentials: 'same-origin' }).catch(function () { }); } catch (e) { }
        }
        if (frame && frame.parentNode) { frame.parentNode.removeChild(frame); }
        frame = null;
    }

    function update() {
        var busy = count > 0 && pauses === 0;
        if (busy && !timer) { start(); } else if (!busy && timer) { stop(); }
    }

    var startupHeld = true;
    var activity = {
        begin: function () { count++; update(); },
        end: function () { count = Math.max(0, count - 1); update(); },
        pause: function () { pauses++; update(); },
        resume: function () { pauses = Math.max(0, pauses - 1); update(); },
        touch: function (ms) {
            count++; update();
            setTimeout(function () { count = Math.max(0, count - 1); update(); }, ms || 120);
        },
        startupDone: function () {
            if (!startupHeld) { return; }
            startupHeld = false;
            activity.end();
        },
        isBusy: function () { return count > 0 && pauses === 0; }
    };
    window.sterpActivity = activity;

    // every fetch (Blazor HttpClient, start-up resources) holds the signal until its response has arrived
    if (origFetch) {
        window.fetch = function (input, init) {
            activity.begin();
            var p;
            try { p = origFetch(input, init); } catch (e) { activity.end(); throw e; }
            p.then(function () { activity.end(); }, function () { activity.end(); });
            return p;
        };
    }

    // start-up hold, released by the shell once the first screen is built (sterpActivity.startupDone)
    count = 1;
    update();

    function isCommandKey(e) {
        var k = e.key || '';
        if (/^F([1-9]|1[0-2])$/.test(k)) { return true; }
        if (k === 'Escape' || k === 'Enter' || k === 'PageUp' || k === 'PageDown') { return true; }
        return e.ctrlKey || e.altKey;
    }

    document.addEventListener('click', function () { activity.touch(); }, true);
    document.addEventListener('dblclick', function () { activity.touch(); }, true);
    document.addEventListener('change', function () { activity.touch(); }, true);
    document.addEventListener('keydown', function (e) { if (isCommandKey(e)) { activity.touch(); } }, true);

    // TextBox.MaxLength: typing stops at the limit; text set by a program (or pasted) is not cut, and the form's own width
    // check refuses it as the legacy dialogs did (data-maxlen on the input)
    document.addEventListener('keydown', function (e) {
        var t = e.target;
        if (!t || !t.getAttribute || e.ctrlKey || e.altKey || e.metaKey) { return; }
        var max = parseInt(t.getAttribute('data-maxlen') || '', 10);
        if (!max || (e.key || '').length !== 1) { return; }
        var selected = Math.max(0, (t.selectionEnd || 0) - (t.selectionStart || 0));
        if ((t.value || '').length - selected >= max) { e.preventDefault(); }
    }, true);

    // ------------------------------------------------------------------------------------------------- shell helpers
    var keyTarget = null;

    function describeTarget(t) {
        if (!t || !t.tagName) { return { tag: '', id: '', field: '', type: '', windowId: '' }; }
        var win = t.closest ? t.closest('[data-sterp-window]') : null;
        var fieldEl = t.closest ? t.closest('[data-sterp-field]') : null;
        return {
            tag: t.tagName.toLowerCase(),
            id: t.id || '',
            field: fieldEl ? fieldEl.getAttribute('data-sterp-field') : '',
            type: (t.getAttribute && t.getAttribute('type')) || '',
            windowId: win ? win.getAttribute('data-sterp-window') : ''
        };
    }

    function keyName(e) {
        var k = e.key || '';
        if (k.length === 1) { return k.toUpperCase(); }
        if (k === 'Esc') { return 'Escape'; }
        return k;
    }

    var sterp = {
        initKeyboard: function (dotnetRef) {
            keyTarget = dotnetRef;
            var boot = document.getElementById('st-boot');
            if (boot) { boot.parentNode.removeChild(boot); }
            document.addEventListener('keydown', function (e) {
                if (!keyTarget) { return; }
                var name = keyName(e);
                if (name === 'Control' || name === 'Alt' || name === 'Shift' || name === 'Meta' || name === 'AltGraph') { return; }
                var isFn = /^F([1-9]|10)$/.test(name);
                var interesting = isFn || name === 'Escape' || name === 'Enter' || name === 'PageUp' || name === 'PageDown' ||
                    name === 'ArrowUp' || name === 'ArrowDown' || name === 'ArrowLeft' || name === 'ArrowRight' ||
                    name === 'F11' || name === 'F12' || e.ctrlKey || e.altKey;
                if (!interesting) { return; }
                var t = describeTarget(e.target);
                var handled = false;
                try {
                    handled = keyTarget.invokeMethod('OnKeyDown', name, !!e.ctrlKey, !!e.altKey, !!e.shiftKey,
                        t.tag, t.id, t.field, t.type, t.windowId,
                        !!(e.getModifierState && e.getModifierState('CapsLock')), !!(e.getModifierState && e.getModifierState('NumLock')));
                } catch (err) {
                    if (window.console) { console.error(err); }
                }
                // the browser defaults of the function keys (help, find, reload, caret browsing, menu) never apply in the shell
                if (handled || isFn || (e.ctrlKey && (name === 'R' || name === 'O' || name === 'K' || name === 'S' || name === 'P'))) {
                    e.preventDefault();
                    e.stopPropagation();
                }
            }, true);
            document.addEventListener('keyup', function (e) {
                if (!keyTarget || !e.getModifierState) { return; }
                if (e.key === 'CapsLock' || e.key === 'NumLock') {
                    try { keyTarget.invokeMethod('OnLockKeys', e.getModifierState('CapsLock'), e.getModifierState('NumLock')); } catch (err) { }
                }
            }, true);
        },
        focusFirst: function (containerId) {
            var c = document.getElementById(containerId);
            if (!c) { return false; }
            var el = c.querySelector('[data-sterp-autofocus]') ||
                c.querySelector('input:not([type=hidden]):not([type=checkbox]):not([disabled]):not([readonly]), select:not([disabled]), textarea:not([disabled]):not([readonly])');
            if (el) { el.focus(); return true; }
            var w = c.closest ? (c.closest('[data-sterp-window]') || c) : c;
            if (w.focus) { w.focus(); }
            return false;
        },
        focus: function (id) {
            var el = document.getElementById(id);
            if (el && el.focus) { el.focus(); return true; }
            return false;
        },
        selectAll: function (id) {
            var el = document.getElementById(id);
            if (el && el.focus) { el.focus(); if (el.select) { el.select(); } return true; }
            return false;
        },
        containsFocus: function (id) {
            var el = document.getElementById(id);
            return !!(el && document.activeElement && el.contains(document.activeElement));
        },
        elementSize: function (id) {
            var el = document.getElementById(id);
            if (!el) { return [0, 0]; }
            return [el.clientWidth, el.clientHeight];
        },
        scrollIntoView: function (id) {
            var el = document.getElementById(id);
            if (el && el.scrollIntoView) { el.scrollIntoView({ block: 'nearest' }); }
        },
        highlight: function (id, start, length) {
            var el = document.getElementById(id);
            if (!el || !window.getSelection) { return; }
            var walker = document.createTreeWalker(el, NodeFilter.SHOW_TEXT);
            var pos = 0, node;
            while ((node = walker.nextNode())) {
                var len = node.nodeValue.length;
                if (start < pos + len) {
                    var r = document.createRange();
                    r.setStart(node, start - pos);
                    r.setEnd(node, Math.min(len, start - pos + length));
                    var sel = window.getSelection();
                    sel.removeAllRanges();
                    sel.addRange(r);
                    var rect = r.getBoundingClientRect ? r.getBoundingClientRect() : null;
                    if (rect && el.getBoundingClientRect) {
                        var box = el.getBoundingClientRect();
                        el.scrollTop += rect.top - box.top - el.clientHeight / 3;
                    }
                    return;
                }
                pos += len;
            }
        },
        setTitle: function (text) { document.title = text; },
        print: function () { window.print(); },
        download: function (fileName, text) {
            var blob = new Blob([text], { type: 'text/plain' });
            var a = document.createElement('a');
            a.href = URL.createObjectURL(blob);
            a.download = fileName;
            document.body.appendChild(a);
            a.click();
            setTimeout(function () { URL.revokeObjectURL(a.href); a.remove(); }, 1000);
        },
        logClientError: function (message, detail) {
            try {
                if (origFetch) {
                    origFetch('/_sterp/client-errors', {
                        method: 'POST', credentials: 'same-origin', headers: { 'Content-Type': 'application/json' },
                        body: JSON.stringify({ message: String(message || '').substring(0, 2000), detail: String(detail || '').substring(0, 8000), page: location.pathname })
                    }).catch(function () { });
                }
            } catch (e) { }
        },
        replaceUrl: function (path) {
            try { if (location.pathname + location.search !== path) { history.replaceState(history.state, '', path); } } catch (e) { }
        }
    };
    window.sterp = sterp;

    window.addEventListener('error', function (e) {
        sterp.logClientError(e.message, e.error && e.error.stack);
    });
})();
