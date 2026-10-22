// A vm sandbox that runs the real asset-cache page modules (asset-*.js) against a fake page: `caches`, `fetch`, a
// service worker that stores `/a/` and `/d/` responses and posts `asset-cached` as sw-asset-cache.js does, `localStorage`, timers
// the test fires by hand, and no IndexedDB (so the ledger runs on its memory backend). For asset-cache.test.mjs.
import { readFileSync } from 'node:fs';
import vm from 'node:vm';

const js = new URL('../../../src/Blocwerk.Web/wwwroot/js/', import.meta.url);
const MODULES = ['asset-ledger.js', 'asset-budget.js', 'asset-wipe.js', 'asset-manifest.js', 'asset-data.js', 'asset-store.js', 'asset-session.js', 'asset-plan.js', 'asset-fill.js', 'asset-cache.js', 'asset-notice.js', 'asset-3d.js', 'asset-clear.js', 'asset-report.js'];
export const ORIGIN = 'https://blocwerk.test';
export const USER_A = 'u-aaaaaaaaaaaaaaaaaaaa';
export const USER_B = 'u-bbbbbbbbbbbbbbbbbbbb';
export const KIOSK = 'k-kkkkkkkkkkkkkkkkkkkk';
export const sha = c => c.repeat(64);

export function json(body, status = 200, headers = {}) {
    return new Response(JSON.stringify(body), { status, headers: Object.assign({ 'content-type': 'application/json' }, headers) });
}

export function fakeCaches(initial = {}) {
    const stores = new Map(Object.entries(initial).map(([name, keys]) => [name, new Map(keys.map(k => [k, new Response('x', { headers: { 'content-length': '1000' } })]))]));
    const api = {
        stores,
        failOpen: false,
        keys: async () => [...stores.keys()],
        has: async name => stores.has(name),
        delete: async name => stores.delete(name),
        open: async name => {
            if (api.failOpen) {
                throw new Error('QuotaExceededError');
            }
            if (!stores.has(name)) {
                stores.set(name, new Map());
            }
            const store = stores.get(name);
            return {
                put: async (key, value) => { store.set(String(key), value); },
                match: async key => store.get(String(key.url || key))?.clone(),
                delete: async key => store.delete(String(key.url || key)),
                keys: async () => [...store.keys()].map(url => ({ url })),
            };
        },
    };
    return api;
}

// A manifest body: `assets` as [sha, tier, extra].
export function manifest(wallId, assets, extra = {}) {
    return Object.assign({ v: 1, wallId, sha: 'w', generation: 1, pending: 0, totals: {}, assets: assets.map(([s, tier, more]) => Object.assign({ sha: s, kind: 'panel', id: s.slice(0, 4), type: 'image/jpeg', bytes: 1000, tier }, more || {})) }, extra);
}

/**
 * opts: { label (meta content, null = no meta), path, search, caches, routes: { path → fn(init, url) }, partitionOfWorker,
 *         swReply ('v4' | 'none' | 'v3' | null = no controller), maxTouchPoints, coarse (a coarse pointer), refuse (worker posts asset-refused), silent (worker
 *         neither stores nor answers: a slow write), prefs3d, ua, quota, ledger, user, connection, persisted (persisted()'s answer),
 *         marks (the elements querySelectorAll answers: offline-ready marks), gate (awaited before an asset fetch),
 *         bus (a shared Set: BroadcastChannel between pages given the same one), degraded (the ledger's refused-storage
 *         fallback instead of the plain memory ledger), storage (a Map shared as localStorage between pages),
 *         storageBlocked (localStorage throws) }
 */
export function loadPage(opts = {}) {
    const timers = [];
    const listeners = {};
    const docListeners = {};
    const swListeners = [];
    const fetches = [];
    const posted = [];
    const storage = opts.storage || new Map();
    const assigned = [];
    const events = [];
    const persisted = [];
    const caches = opts.caches || fakeCaches();
    const label = opts.label === undefined ? USER_A : opts.label;
    const meta = label === null ? null : { getAttribute: () => label };
    const worker = { label: opts.workerLabel || label, refuse: !!opts.refuse, silent: !!opts.silent };

    const timer = (fn, ms) => {
        // Short waits (verify, sw-info, the logout cap) run scaled down; long ones (the pending refetch, retries) wait for fireLong().
        const t = { fn, ms, done: false };
        timers.push(t);
        if (ms < 10000) {
            // Scaled down to event-loop turns, not wall-clock time, so the order is kept however loaded the machine
            // is: a reply already posted through setImmediate (sw-info, asset-cached) is delivered within the first
            // turn, long before a 1.5 s timeout's 30 turns. A wall-clock delay lost that race whenever the loop stalled
            // past it (about 1 full-suite run in 10), and the page then saw no worker and filled nothing.
            const turns = Math.max(5, Math.min(60, Math.round(ms / 50)));
            const hop = left => setImmediate(() => {
                if (t.done) {
                    return;
                }
                if (left > 1) {
                    hop(left - 1);
                    return;
                }
                t.done = true;
                fn();
            });
            hop(turns);
        }
        return t;
    };

    function postFromWorker(message) {
        setImmediate(() => swListeners.forEach(fn => fn({ data: message })));
    }

    async function routedFetch(input, init = {}) {
        const url = new URL(String(input), ORIGIN);
        fetches.push({ path: url.pathname, search: url.search, init });
        const asset = /^\/api\/walls\/[^/]+\/(?:a|d\/([a-z0-9]+))\/([0-9a-f]{64})(?:\/(\d+))?$/.exec(url.pathname);
        const route = sandbox.routes[url.pathname + url.search] || sandbox.routes[url.pathname];
        if (route) {
            return route(init, url);
        }
        if (asset) {
            if (opts.gate) {
                await opts.gate(url.pathname);
            }
            // The worker's part: store in the partition's cache (a /d/ document in -3d), then announce.
            const [, docKind, assetSha, assetWidth] = asset;
            const key = `${ORIGIN}/__bw/${assetSha}` + (assetWidth ? `/${assetWidth}` : '');
            const announced = { proto: 1, sha: assetSha, width: assetWidth ? Number(assetWidth) : null, bytes: 5000 };
            if (worker.refuse) {
                postFromWorker(Object.assign({ type: 'asset-refused' }, announced));
            } else if (!worker.silent) {
                const cache = await caches.open(`bw-a1-${worker.label}-${docKind ? '3d' : 'img'}`);
                await cache.put(key, new Response('x', { headers: { 'content-length': '5000' } }));
                postFromWorker(Object.assign({ type: 'asset-cached' }, announced));
            }
            return new Response('bytes', { status: 200 });
        }
        if (/^\/api\/walls\/[^/]+\/docs$/.test(url.pathname)) {
            return json({ cursor: null, docs: [] }); // a wall with no data documents (asset-data tests route their own)
        }
        if (url.pathname === '/api/me/cache-summary') {
            return json(opts.summary || { wallsWith3d: 0, walls: [] });
        }
        return new Response('', { status: 404 });
    }

    class FakeChannel {
        constructor() {
            this.port1 = { onmessage: null };
            this.port2 = { postMessage: data => setImmediate(() => this.port1.onmessage && this.port1.onmessage({ data })) };
        }
    }

    const swReply = opts.swReply === undefined ? 'v4' : opts.swReply;
    const controller = swReply === null ? null : {
        postMessage: (message, ports) => {
            posted.push(message);
            if (message && message.type === 'sw-refresh-partition' && opts.swRefreshed !== undefined && ports && ports[0]) {
                ports[0].postMessage({ proto: 1, type: 'partition-refreshed', partition: opts.swRefreshed });
                return; // `swRefreshed`: a worker that answers the refresh with this label (else it stays silent)
            }
            if (swReply === 'none' || !message || message.type !== 'sw-info') {
                return; // an old worker ignores sw-info
            }
            const reply = { proto: 1, type: 'sw-info', shell: swReply === 'v3' ? 'blocwerk-shell-v3' : 'blocwerk-shell-v4' };
            if (opts.swPartition !== undefined) {
                reply.partition = opts.swPartition;
            }
            ports[0].postMessage(reply);
        },
    };

    let keep3d = opts.prefs3d === undefined ? true : opts.prefs3d;
    const sandbox = {
        routes: opts.routes || {},
        __bwAssetCacheManual: true,
        console, URL, URLSearchParams, Response, Promise, Map, Set, JSON, Math, Date, Number, String, Object, Array, Error,
        setTimeout: timer,
        clearTimeout: t => { if (t) { t.done = true; } },
        setInterval: () => 0,
        MessageChannel: FakeChannel,
        BroadcastChannel: opts.bus ? class {
            constructor(name) { this.name = name; this.onmessage = null; opts.bus.add(this); }
            postMessage(data) { opts.bus.forEach(c => { if (c !== this && c.name === this.name) { setImmediate(() => c.onmessage && c.onmessage({ data: JSON.parse(JSON.stringify(data)) })); } }); }
        } : undefined,
        CustomEvent: class { constructor(type) { this.type = type; } },
        caches,
        fetch: routedFetch,
        innerWidth: 400,
        devicePixelRatio: 3,
        location: { pathname: opts.path || '/', search: opts.search || '', origin: ORIGIN, href: ORIGIN + (opts.path || '/'), assign: u => assigned.push(String(u)) },
        navigator: {
            userAgent: opts.ua || 'Mozilla/5.0 (X11; Linux x86_64) Chrome/130.0 Safari/537.36',
            maxTouchPoints: opts.maxTouchPoints ?? 0,
            standalone: opts.navStandalone,
            connection: opts.connection,
            onLine: opts.onLine ?? true,
            userAgentData: opts.userAgentData,
            serviceWorker: { controller, addEventListener: (type, fn) => { if (type === 'message') { swListeners.push(fn); } } },
            storage: { estimate: async () => ({ quota: opts.quota ?? 50e9, usage: 41e9 }), persisted: async () => !!opts.persisted, persist: async () => { persisted.push(Date.now()); return true; } },
        },
        matchMedia: q => ({ matches: q === '(pointer: coarse)' ? !!opts.coarse : !!opts.standalone }),
        localStorage: opts.storageBlocked
            ? { getItem: () => { throw new Error('SecurityError'); }, setItem: () => { throw new Error('SecurityError'); }, removeItem: () => undefined }
            : { getItem: k => (storage.has(k) ? storage.get(k) : null), setItem: (k, v) => storage.set(k, String(v)), removeItem: k => storage.delete(k) },
        document: {
            readyState: 'complete',
            visibilityState: 'visible',
            querySelector: selector => (selector === 'meta[name="bw-partition"]' ? meta : null),
            querySelectorAll: () => opts.marks || [],
            body: { getAttribute: name => (name === 'data-bw-user' ? (opts.user ?? '') : null), dataset: opts.kioskKey ? { bwKiosk: opts.kioskKey } : {} },
            addEventListener: (type, fn) => { (docListeners[type] = docListeners[type] || []).push(fn); },
            removeEventListener: () => {},
        },
        addEventListener: (type, fn) => { (listeners[type] = listeners[type] || []).push(fn); },
        dispatchEvent: event => { events.push(event.type); (listeners[event.type] || []).forEach(fn => fn(event)); },
        bwPrefs: { getCache3d: () => keep3d, setCache3d: on => { keep3d = !!on; return keep3d; } },
    };
    if (opts.deployDrafts) {
        sandbox.bwDeployDrafts = opts.deployDrafts;
    }
    sandbox.window = sandbox;
    const context = vm.createContext(sandbox);
    if (opts.ledger) {
        // A ledger carried over from an earlier page load of the same browser.
        context.__ledger = opts.ledger;
    }
    for (const name of MODULES) {
        vm.runInContext(readFileSync(new URL(name, js), 'utf8'), context, { filename: name });
    }
    if (opts.ledger) {
        vm.runInContext('window.bwAssetLedger.open = () => Promise.resolve(window.__ledger);', context);
    } else if (!opts.degraded) {
        // The plain memory ledger, standing in for IndexedDB. `degraded` keeps the real open(), which (no IndexedDB
        // here) gives the degraded fallback a refused storage or a pending upgrade gets.
        vm.runInContext('(() => { const l = window.bwAssetLedger.memory(); window.bwAssetLedger.open = () => Promise.resolve(l); })();', context);
    }

    const settle = async () => {
        for (let round = 0; round < 4; round++) {
            for (let i = 0; i < 30; i++) {
                await new Promise(resolve => setImmediate(resolve));
            }
            await new Promise(resolve => setTimeout(resolve, 10));
        }
    };

    return {
        win: sandbox, caches, fetches, posted, timers, events, assigned, storage, worker, persisted,
        cache: sandbox.bwAssetCache,
        ledger: () => sandbox.bwAssetLedger.open(),
        boot: async () => { await sandbox.bwAssetCache.boot(); await settle(); await sandbox.bwAssetCache.filled(); await settle(); },
        settle,
        // Fires the long timers (the pending refetch) that are due.
        fireLong: async () => { for (const t of timers.filter(t => !t.done && t.ms >= 10000)) { t.done = true; t.fn(); } await settle(); await sandbox.bwAssetCache.filled(); await settle(); },
        docEvent: (type, event) => (docListeners[type] || []).forEach(fn => fn(event)),
        winEvent: (type, event) => (listeners[type] || []).forEach(fn => fn(event || {})),
        assetFetches: () => fetches.filter(f => /\/a\/[0-9a-f]{64}/.test(f.path)),
        dataFetches: () => fetches.filter(f => /\/d\/[a-z0-9]+\/[0-9a-f]{64}$|\/docs$/.test(f.path)),
    };
}
