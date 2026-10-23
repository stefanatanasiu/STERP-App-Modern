/*
 * STERP API Console - busy signal for automation that waits for "network idle" after each action (the Agent 07 GUI
 * parity driver; same technique as the STERP-WEB shell, docs/DEVELOPER-GUIDE.md section 10). While the page is busy
 * (start-up, a document download, an operation being executed and its result rendered) a hidden about:blank frame with a
 * pending request to /_sterp/hold stays attached, so the page is not idle until the result has been rendered. The signal
 * never changes what the console does. Loaded synchronously in <head>, before console.js.
 */
(function () {
    'use strict';
    if (window.sterpActivity) { return; }

    var count = 0, frame = null, active = false, busySince = 0, watchdog = null, domHeld = false, startupHeld = true;
    var origFetch = window.fetch ? window.fetch.bind(window) : null;

    function hold(f) {
        var w = f.contentWindow;
        if (!w || !w.fetch) { return; }
        try {
            w.fetch(location.origin + '/_sterp/hold', { cache: 'no-store', credentials: 'omit' }).then(function () {
                if (frame === f && active) { hold(f); }
            }, function () {
                if (frame === f && active) { setTimeout(function () { if (frame === f && active) { renew(); } }, 200); }
            });
        } catch (e) { }
    }

    function renew() {
        var f = document.createElement('iframe');
        f.setAttribute('aria-hidden', 'true');
        f.setAttribute('tabindex', '-1');
        f.setAttribute('title', 'activity');
        f.style.cssText = 'position:fixed;left:-20px;top:-20px;width:1px;height:1px;border:0;visibility:hidden;pointer-events:none';
        (document.body || document.documentElement).appendChild(f);
        if (frame && frame.parentNode) { frame.parentNode.removeChild(frame); }
        frame = f;
        hold(f);
    }

    function update() {
        var busy = count > 0;
        if (busy && !active) {
            active = true;
            busySince = Date.now();
            watchdog = setInterval(function () {
                if (busySince && Date.now() - busySince > 120000) { count = 0; update(); }
            }, 1000);
            renew();
        } else if (!busy && active) {
            active = false;
            busySince = 0;
            clearInterval(watchdog);
            if (origFetch) {
                try { origFetch('/_sterp/settle', { cache: 'no-store', credentials: 'same-origin' }).catch(function () { }); } catch (e) { }
            }
            if (frame && frame.parentNode) { frame.parentNode.removeChild(frame); }
            frame = null;
        }
    }

    var activity = {
        begin: function () { count++; update(); },
        end: function () { count = Math.max(0, count - 1); update(); },
        touch: function (ms) {
            count++; update();
            setTimeout(function () { count = Math.max(0, count - 1); update(); }, ms || 300);
        },
        startupDone: function () {
            if (!startupHeld) { return; }
            startupHeld = false;
            activity.end();
        },
        isBusy: function () { return count > 0; }
    };
    window.sterpActivity = activity;

    // a fetch holds the signal until its response has arrived, plus a short grace for rendering the result
    if (origFetch) {
        window.fetch = function (input, init) {
            activity.begin();
            var p;
            try { p = origFetch(input, init); } catch (e) { activity.end(); throw e; }
            p.then(function () { activity.touch(300); activity.end(); }, function () { activity.touch(300); activity.end(); });
            return p;
        };
    }

    // start-up hold: released by console.js once its first view has been rendered, or on load for a page without it
    count = 1;
    update();
    window.addEventListener('load', function () {
        if (!window.sterpConsole) { activity.startupDone(); }
    });

    // the console marks a region aria-busy while it loads a document or executes an operation
    setInterval(function () {
        var loading = !!document.querySelector('[aria-busy="true"]');
        if (loading && !domHeld) { domHeld = true; activity.begin(); }
        else if (!loading && domHeld) { domHeld = false; activity.touch(150); activity.end(); }
    }, 40);

    function isCommandKey(e) {
        var k = e.key || '';
        return k === 'Enter' || k === 'Escape' || /^F([1-9]|1[0-2])$/.test(k) || e.ctrlKey || e.altKey;
    }

    document.addEventListener('click', function () { activity.touch(300); }, true);
    document.addEventListener('change', function () { activity.touch(300); }, true);
    document.addEventListener('keydown', function (e) { if (isCommandKey(e)) { activity.touch(300); } }, true);
})();
