/*
 * The connection / sync status pill.
 *
 * Renders into the empty <div id="bw-conn-slot"> that Components/Shared/ConnectionStatus.razor
 * puts in MainLayout, falling back to a fixed-position host on <body> if the slot is not on the
 * page (static pages, or a layout that omits the component).
 *
 * The slot is deliberately an element Blazor renders but never fills: Blazor's diff emits no
 * edits for an element whose render-tree children have not changed, so the nodes this file
 * appends survive layout re-renders. Nothing here is driven by the circuit, which is the whole
 * point — the pill has to be at its most informative exactly when the circuit is down.
 */
(function () {
    const SLOT_ID = 'bw-conn-slot';
    let host = null;
    let expanded = false;

    // A transient message shown in the pill for a few seconds (e.g. "TopLogger synced"), then cleared.
    // A real connection/queue status always wins over it, so it never masks an offline/reconnecting state.
    let flashState = null;
    let flashTimer = null;

    function flashMessage(label, tone, ms) {
        flashState = { label: label, tone: tone || 'warn' };
        render();
        if (flashTimer) {
            clearTimeout(flashTimer);
        }
        flashTimer = setTimeout(function () {
            flashState = null;
            render();
        }, ms || 4000);
    }

    function ensureHost() {
        if (host && host.isConnected) {
            return host;
        }

        const slot = document.getElementById(SLOT_ID);
        if (slot) {
            host = slot;
            return host;
        }

        host = document.querySelector('.bw-conn-fallback');
        if (!host) {
            host = document.createElement('div');
            host.className = 'bw-conn-fallback';
            document.body.appendChild(host);
        }
        return host;
    }

    // The default wording when the server announced an update without a message of its own.
    const MAINTENANCE_LABEL = 'Blocwerk is updating\u2026';
    const MAINTENANCE_DETAIL = 'The server is being updated. This page reloads itself as soon as the new version is ready \u2014 nothing you have queued is lost.';
    const MAINTENANCE_READY_LABEL = 'Update ready';
    const MAINTENANCE_READY_DETAIL = 'The new version is live. Reload when you are ready \u2014 the reload is held back because you were typing.';

    /**
     * The maintenance notice, or null. Read lazily off window so this file keeps working with or
     * without maintenance.js, in either load order.
     */
    function maintenance() {
        const api = window.bwMaintenance;
        if (!api || !api.active()) {
            return null;
        }

        const state = api.state();
        if (state.phase === 'ready') {
            return { tone: 'info', label: MAINTENANCE_READY_LABEL, detail: MAINTENANCE_READY_DETAIL };
        }

        return {
            tone: 'warn',
            label: state.message || MAINTENANCE_LABEL,
            detail: state.message ? state.message + ' ' + MAINTENANCE_DETAIL : MAINTENANCE_DETAIL
        };
    }

    function summarise(queueState, connection) {
        const count = queueState.count;

        // Above 'paused' and 'rejected', and below exactly one thing: a device with no network at
        // all. During a deploy the circuit is down and the socket is dead, so every rung below
        // would fire — and every one would be a worse sentence than the true one: 'Session ended'
        // invites a reload straight onto offline.html, and 'Sign in to sync' asks for an action the
        // server cannot serve. The notice is transient and self-clearing, so nothing below is
        // suppressed for long.
        //
        // The queueState.online guard is the exception, and it is about honesty. A tablet with its
        // wifi off is not waiting for an update — it cannot even reach /alive to find out whether
        // one is happening, and the notice it is holding may be minutes stale. Telling that user
        // 'Blocwerk is updating…' explains their stuck queue with the wrong cause and hides the one
        // thing they can actually act on. Offline outranks maintenance; everything else does not.
        const updating = queueState.online ? maintenance() : null;
        if (updating) {
            return updating;
        }

        if (queueState.paused) {
            return { tone: 'warn', label: 'Sign in to sync', detail: 'Your session expired. ' + count + ' action(s) are saved on this device.' };
        }
        if (connection === 'rejected') {
            return { tone: 'warn', label: 'Session ended', detail: 'The server lost this session. Reload to continue; nothing queued is lost.' };
        }
        if (!queueState.online) {
            return { tone: 'warn', label: count > 0 ? 'Offline · ' + count : 'Offline', detail: count > 0 ? count + ' action(s) saved on this device, waiting for a connection.' : 'No connection. Ascents, ratings and comments still work and sync later.' };
        }
        if (connection === 'reconnecting' || connection === 'failed') {
            return { tone: 'warn', label: count > 0 ? 'Reconnecting · ' + count : 'Reconnecting', detail: 'Reconnecting to the server. Queued actions sync automatically.' };
        }
        if (count > 0) {
            return { tone: 'info', label: 'Syncing · ' + count, detail: count + ' action(s) still to send.' };
        }
        return null;
    }

    function buildDetail(queueState, summary) {
        const panel = document.createElement('div');
        panel.className = 'bw-conn-panel';

        const text = document.createElement('p');
        text.className = 'bw-conn-panel-text';
        text.textContent = summary.detail;
        panel.appendChild(text);

        queueState.rejected.forEach(item => {
            const failed = document.createElement('p');
            failed.className = 'bw-conn-panel-error';
            failed.textContent = item.kind + ': ' + item.message;
            panel.appendChild(failed);
        });

        const actions = document.createElement('div');
        actions.className = 'bw-conn-panel-actions';

        if (queueState.paused) {
            const login = document.createElement('a');
            login.className = 'bw-conn-btn';
            login.href = '/account/login';
            login.textContent = 'Sign in';
            actions.appendChild(login);
        }

        // A held-back maintenance reload is offered explicitly; the automatic one waits for the
        // text field to be empty, and the user may well want it sooner.
        if (window.bwMaintenance && window.bwMaintenance.active() && window.bwMaintenance.state().phase === 'ready') {
            actions.appendChild(button('Reload now', () => window.bwMaintenance.reloadNow()));
        } else if (window.blocwerkConnection && window.blocwerkConnection.status() === 'rejected') {
            actions.appendChild(button('Reload', () => window.blocwerkConnection.reload()));
        }

        actions.appendChild(button('Retry now', () => window.blocwerkOfflineQueue.retryNow()));

        if (queueState.rejected.length > 0) {
            actions.appendChild(button('Dismiss', () => window.blocwerkOfflineQueue.dismissRejected()));
        }

        panel.appendChild(actions);
        return panel;
    }

    function button(label, onClick) {
        const el = document.createElement('button');
        el.type = 'button';
        el.className = 'bw-conn-btn';
        el.textContent = label;
        el.addEventListener('click', event => {
            event.stopPropagation();
            onClick();
        });
        return el;
    }

    function render() {
        const queueState = window.blocwerkOfflineQueue
            ? window.blocwerkOfflineQueue.state()
            : { count: 0, online: true, paused: false, rejected: [] };
        const connection = window.blocwerkConnection
            ? window.blocwerkConnection.status()
            : 'connected';

        const container = ensureHost();
        container.textContent = '';

        const summary = summarise(queueState, connection);
        if (!summary && queueState.rejected.length === 0) {
            // Nothing to report from the connection/queue — but a transient flash may be showing.
            if (flashState) {
                container.classList.add('bw-conn-active');
                const flashPill = document.createElement('div');
                flashPill.className = 'bw-conn-pill bw-conn-' + flashState.tone + ' bw-conn-flash';
                const flashDot = document.createElement('span');
                flashDot.className = 'bw-conn-dot';
                flashPill.appendChild(flashDot);
                const flashLabel = document.createElement('span');
                flashLabel.className = 'bw-conn-label';
                flashLabel.textContent = flashState.label;
                flashPill.appendChild(flashLabel);
                container.appendChild(flashPill);
                return;
            }
            expanded = false;
            container.classList.remove('bw-conn-active');
            return;
        }

        const effective = summary || {
            tone: 'warn',
            label: 'Not saved',
            detail: 'Some actions could not be saved.'
        };

        container.classList.add('bw-conn-active');

        const pill = document.createElement('button');
        pill.type = 'button';
        pill.className = 'bw-conn-pill bw-conn-' + effective.tone;
        pill.setAttribute('aria-expanded', expanded ? 'true' : 'false');

        const dot = document.createElement('span');
        dot.className = 'bw-conn-dot';
        pill.appendChild(dot);

        const label = document.createElement('span');
        label.className = 'bw-conn-label';
        label.textContent = effective.label;
        pill.appendChild(label);

        pill.addEventListener('click', () => {
            expanded = !expanded;
            render();
        });

        container.appendChild(pill);

        if (expanded) {
            container.appendChild(buildDetail(queueState, effective));
        }
    }

    if (window.blocwerkOfflineQueue) {
        window.blocwerkOfflineQueue.subscribe(render);
    }
    if (window.blocwerkConnection) {
        window.blocwerkConnection.subscribe(render);
    }

    window.addEventListener('bw:offline-error', render);
    document.addEventListener('DOMContentLoaded', render);

    // The slot lives inside the Blazor layout, so it may not exist yet when this file runs.
    // A few cheap re-renders cover the gap without an always-on MutationObserver.
    [200, 1000, 3000].forEach(delay => setTimeout(render, delay));

    window.blocwerkStatus = { render: render, flash: flashMessage };
})();
