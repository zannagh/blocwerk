// A vm sandbox of the offline client's page (Phase 3 PR 3.10): ONLY the six shared cache modules the shell loads
// (Blocwerk.Shared.Ui) and client-store.js, so none of Web's globals (bwAssetCache, bwAssetStore, bwAssetReport,
// bwDeployDrafts, bwAssetManifest) exists, as on /v/. The shell's meta says `offline`; `caches` has the top-level
// match(url, { cacheName }) the store reads with; the ledger is the plain memory one unless `degraded`.
import { readFileSync } from 'node:fs';
import vm from 'node:vm';
import { moduleUrl } from './page-modules.mjs';
import { fakeCaches, ORIGIN } from './page-sandbox.mjs';

const SHELL_MODULES = ['asset-ledger-idb.js', 'asset-ledger.js', 'asset-wipe.js', 'asset-lock.js', 'asset-session.js', 'asset-clear.js'];
const STORE = ['client-store-rows.js', 'client-store.js'].map(name => new URL(`../../../src/Blocwerk.Client/wwwroot/js/${name}`, import.meta.url));
// The sync loop's network side (PR 3.12), as the shell loads it after the store: Web's refreshers, then client-sync.js.
const SYNC_MODULES = ['asset-manifest.js', 'asset-data.js', 'asset-mine.js'];
const SYNC = new URL('../../../src/Blocwerk.Client/wwwroot/js/client-sync.js', import.meta.url);

/** Fake caches whose entries carry real bodies, with the top-level match the store uses. */
export function bodyCaches() {
    const caches = fakeCaches();
    caches.match = async (url, opts) => {
        const store = caches.stores.get(opts && opts.cacheName);
        const hit = store && store.get(String(url));
        return hit ? hit.clone() : undefined;
    };
    caches.putText = async (name, key, text) => (await caches.open(name)).put(key, new Response(text));
    return caches;
}

/**
 * opts: { path, caches, storage (Map: localStorage), ledger (carried over), degraded, pending (upgradePending), bus (a
 * shared Set: BroadcastChannel), modules (the shared modules to load; default all six), worker (a controller),
 * noCrypto (no WebCrypto: no share hash), sync (also load the sync modules and client-sync.js), fetch, EventSource,
 * bwStatus (the status bubbles) }
 */
export function loadClient(opts = {}) {
    const listeners = {};
    const docListeners = {};
    const storage = opts.storage || new Map();
    const caches = opts.caches || bodyCaches();
    const posted = [];
    const swListeners = {};
    const path = opts.path || '/v/';
    const meta = { getAttribute: name => (name === 'content' ? 'offline' : null) };
    const sandbox = {
        console, URL, URLSearchParams, Response, Promise, Map, Set, JSON, Math, Date, Number, String, Object, Array, Error,
        crypto: opts.noCrypto ? undefined : globalThis.crypto, TextEncoder,
        setTimeout: (fn, ms) => setTimeout(fn, Math.min(ms, 5)),
        clearTimeout,
        BroadcastChannel: opts.bus ? class {
            constructor(name) { this.name = name; this.onmessage = null; opts.bus.add(this); }
            postMessage(data) { opts.bus.forEach(c => { if (c !== this && c.name === this.name) { setImmediate(() => c.onmessage && c.onmessage({ data: JSON.parse(JSON.stringify(data)) })); } }); }
        } : undefined,
        CustomEvent: class { constructor(type) { this.type = type; } },
        caches,
        location: { pathname: path.split('?')[0], search: path.includes('?') ? '?' + path.split('?')[1] : '', origin: ORIGIN, href: ORIGIN + path },
        navigator: { serviceWorker: {
            controller: opts.worker === null ? null : { postMessage: m => posted.push(m) },
            addEventListener: (type, fn) => { (swListeners[type] = swListeners[type] || []).push(fn); },
        } },
        fetch: opts.fetch, EventSource: opts.EventSource, bwStatus: opts.bwStatus,
        localStorage: { getItem: k => (storage.has(k) ? storage.get(k) : null), setItem: (k, v) => storage.set(k, String(v)), removeItem: k => storage.delete(k) },
        document: {
            visibilityState: 'visible',
            querySelector: selector => (selector === 'meta[name="bw-partition"]' ? meta : null),
            getElementById: () => null,
            body: { getAttribute: () => null, dataset: {} },
            addEventListener: (type, fn) => { (docListeners[type] = docListeners[type] || []).push(fn); },
        },
        addEventListener: (type, fn) => { (listeners[type] = listeners[type] || []).push(fn); },
        dispatchEvent: () => true,
    };
    sandbox.window = sandbox;
    const context = vm.createContext(sandbox);
    for (const name of opts.modules || SHELL_MODULES) {
        vm.runInContext(readFileSync(moduleUrl(name), 'utf8'), context, { filename: name });
    }
    const sync = opts.sync ? SYNC_MODULES.map(moduleUrl).concat([SYNC]) : [];
    for (const url of STORE.concat(sync)) {
        vm.runInContext(readFileSync(url, 'utf8'), context, { filename: url.pathname.split('/').pop() });
    }
    if (sandbox.bwAssetLedger) {
        if (opts.ledger) {
            context.__ledger = opts.ledger;
            vm.runInContext('window.bwAssetLedger.open = () => Promise.resolve(window.__ledger);', context);
        } else if (!opts.degraded) {
            vm.runInContext('(() => { const l = window.bwAssetLedger.memory(); window.bwAssetLedger.open = () => Promise.resolve(l); })();', context);
        }
        if (opts.pending) {
            sandbox.bwAssetLedger.upgradePending = () => true;
        }
    }
    return {
        win: sandbox,
        store: sandbox.bwClientStore,
        sync: sandbox.bwClientSync,
        caches,
        storage,
        posted,
        ledger: () => sandbox.bwAssetLedger.open(),
        winEvent: (type, event) => (listeners[type] || []).forEach(fn => fn(event || {})),
        docEvent: (type, event) => (docListeners[type] || []).forEach(fn => fn(event || {})),
        swEvent: (type, event) => Promise.all((swListeners[type] || []).map(fn => fn(event || {}))),
    };
}
