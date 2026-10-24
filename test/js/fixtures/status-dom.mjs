// A page for the status bubbles (status-bubble.js, status-sources.js, offline-status.js): just enough DOM for them,
// fake timers, and a fake offline queue and circuit whose state a test sets.
import { readFileSync } from 'node:fs';
import vm from 'node:vm';

const js = new URL('../../../src/Blocwerk.Web/wwwroot/js/', import.meta.url);

class FakeElement {
    constructor(tag) {
        this.tagName = tag.toUpperCase();
        this.children = [];
        this.attrs = new Map();
        this.listeners = {};
        this.className = '';
        this.text = '';
        this.isConnected = true;
        this.dataset = {};
        const owner = this;
        this.classList = {
            add: c => { if (!owner.className.split(' ').includes(c)) { owner.className = (owner.className + ' ' + c).trim(); } },
            remove: c => { owner.className = owner.className.split(' ').filter(x => x !== c).join(' '); },
            contains: c => owner.className.split(' ').includes(c),
        };
    }
    get id() { return this.attrs.get('id') || ''; }
    set id(v) { this.attrs.set('id', v); }
    get textContent() { return this.text + this.children.map(c => c.textContent).join(''); }
    set textContent(v) { this.text = String(v); this.children = []; }
    setAttribute(n, v) { this.attrs.set(n, String(v)); }
    getAttribute(n) { return this.attrs.has(n) ? this.attrs.get(n) : null; }
    appendChild(c) { this.children.push(c); return c; }
    addEventListener(t, fn) { (this.listeners[t] = this.listeners[t] || []).push(fn); }
    click() { (this.listeners.click || []).forEach(fn => fn({ stopPropagation() {} })); }
    all() { return this.children.flatMap(c => [c, ...c.all()]); }
}

/** opts: { kiosk, online, modules } */
export function statusPage(opts = {}) {
    const timers = [];
    let now = 1_000_000;
    const body = new FakeElement('body');
    body.dataset = opts.kiosk ? { bwKiosk: 'key' } : {};
    const slot = new FakeElement('div');
    slot.id = 'bw-conn-slot';
    body.appendChild(slot);
    const docListeners = {};
    const find = pred => body.all().find(pred) || null;
    const document = {
        body,
        createElement: tag => new FakeElement(tag),
        getElementById: id => find(e => e.id === id),
        querySelector: sel => find(e => sel.startsWith('.') && e.classList.contains(sel.slice(1))),
        addEventListener: (t, fn) => { (docListeners[t] = docListeners[t] || []).push(fn); },
        dispatchEvent: e => (docListeners[e.type] || []).forEach(fn => fn(e)),
    };

    const queueSubs = [];
    const connSubs = [];
    const queueState = { online: opts.online ?? true, paused: false, flushing: false, count: 0, held: 0, rejected: [], lastError: null };
    let conn = 'connected';
    const win = {
        document,
        Date: { now: () => now },
        setTimeout: (fn, ms) => { timers.push({ fn, at: now + (ms || 0), done: false }); return timers.length; },
        clearTimeout: id => { if (id && timers[id - 1]) { timers[id - 1].done = true; } },
        addEventListener: () => {},
        blocwerkOfflineQueue: {
            state: () => Object.assign({}, queueState, { rejected: queueState.rejected.slice() }),
            subscribe: cb => { queueSubs.push(cb); cb(); return () => {}; },
            dismissRejected: () => { queueState.rejected = []; queueSubs.forEach(cb => cb()); },
            retryNow: () => {},
        },
        blocwerkConnection: { status: () => conn, subscribe: cb => { connSubs.push(cb); cb(conn); return () => {}; } },
    };
    win.window = win;
    const context = vm.createContext(Object.assign(win, { console, Object, Array, Set, Map, JSON, Math, String, Promise }));
    for (const name of opts.modules || ['status-bubble.js', 'status-sources.js', 'offline-status.js']) {
        vm.runInContext(readFileSync(new URL(name, js), 'utf8'), context, { filename: name });
    }

    const page = {
        win, body, slot,
        /** Advances the clock by `ms`, running the timers due on the way. */
        advance: ms => {
            const end = now + ms;
            for (;;) {
                const next = timers.filter(t => !t.done && t.at <= end).sort((a, b) => a.at - b.at)[0];
                if (!next) {
                    break;
                }
                next.done = true;
                now = Math.max(now, next.at);
                next.fn();
            }
            now = end;
        },
        queue: changes => { Object.assign(queueState, changes); queueSubs.forEach(cb => cb()); },
        connection: status => { conn = status; connSubs.forEach(cb => cb(status)); },
        flushed: detail => document.dispatchEvent({ type: 'bw:offline-flushed', detail }),
        pill: () => find(e => e.classList.contains('bw-conn-pill')),
        live: id => document.getElementById(id),
    };
    return page;
}
