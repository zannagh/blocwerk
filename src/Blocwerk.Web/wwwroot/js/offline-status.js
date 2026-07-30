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

    function summarise(queueState, connection) {
        const count = queueState.count;

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

        if (window.blocwerkConnection && window.blocwerkConnection.status() === 'rejected') {
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

    window.blocwerkStatus = { render: render };
})();
