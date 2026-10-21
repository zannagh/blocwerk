/*
 * Boots the storage probe page (/storage-probe): shows the run id and context, reports the page load (with
 * any crash the last visit left behind and a silent read of the markers), and wires the four buttons. One
 * test at a time; the buttons are disabled while one runs.
 */
(function () {
    'use strict';

    const probe = window.bwProbe;
    let busy = false;

    function setBusy(value, label) {
        busy = value;
        document.querySelectorAll('[data-probe-action]').forEach(b => {
            b.disabled = value;
        });
        const state = document.getElementById('probe-state');
        if (state) {
            state.textContent = value ? 'Running: ' + label + '. Keep this screen open.' : 'Idle.';
        }
    }

    const actions = {
        quick: ['Quick checks', () => probe.checks.quick()],
        'write-markers': ['Write markers', () => probe.checks.writeMarkers()],
        'check-markers': ['Check markers', () => probe.checks.checkMarkers(false)],
        ladder: ['Size ladder', () => probe.ladder.run()],
        'reset-ladder': ['Reset ladder', async () => {
            await probe.ladder.reset();
            await probe.post('ladder', 'reset', {});
            probe.log('Size ladder progress cleared; the next run starts at 25 MB.', 'info');
        }],
    };

    async function perform(name) {
        if (busy || !actions[name]) {
            return;
        }
        const [label, work] = actions[name];
        setBusy(true, label);
        try {
            await work();
        } catch (e) {
            probe.log(label + ' stopped: ' + probe.describe(e), 'fail');
            await probe.post('error', 'fail', { action: name, error: probe.describe(e) });
        } finally {
            setBusy(false);
        }
    }

    async function boot() {
        document.getElementById('probe-run').textContent = probe.runId;
        document.getElementById('probe-ctx').textContent = probe.standalone ? 'Home-screen app' : 'Browser tab';
        document.querySelectorAll('[data-probe-action]').forEach(b => {
            b.addEventListener('click', () => perform(b.dataset.probeAction));
        });

        setBusy(true, 'page load report');
        try {
            const status = await probe.post('load', 'ok', { ua: navigator.userAgent, ladder: probe.ladder.summary() });
            if (status === 401 || status === 403) {
                probe.log('The server refused the report: sign in to Blocwerk in this context first.', 'fail');
            }
            probe.previousVisit = localStorage.getItem('bw-storage-probe-last-visit');
            await probe.ladder.detectCrashes();
            await probe.checks.checkMarkers(true);
            localStorage.setItem('bw-storage-probe-last-visit', new Date().toISOString());
        } catch (e) {
            probe.log('Load report failed: ' + probe.describe(e), 'warn');
        } finally {
            setBusy(false);
        }
        probe.log('Ready. Run id ' + probe.runId + ' (' + probe.ctx + ').', 'info');
    }

    // A rejection no test awaited (WebKit raises one from a failed cache.put's body stream): logged, not lost.
    window.addEventListener('unhandledrejection', event => {
        const text = probe.describe(event.reason);
        probe.log('Unhandled: ' + text, 'warn');
        probe.post('unhandled', 'fail', { error: text });
    });

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', boot);
    } else {
        boot();
    }
})();
