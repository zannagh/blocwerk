// A vm sandbox that runs the real service worker (and the scripts it imports) against fake `caches`, `fetch` and
// `clients`, for service-worker.test.mjs. Responses are real `Response`s typed 'basic', as same-origin ones are.
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import vm from 'node:vm';

const wwwroot = new URL('../../../src/Blocwerk.Web/wwwroot', import.meta.url);
export const ORIGIN = 'https://blocwerk.test';

export function response(status, body = '', headers = {}, type = 'basic') {
    const r = new Response(status === 204 || status === 304 ? null : body, { status, headers });
    Object.defineProperty(r, 'type', { value: type });
    return r;
}

function fakeCaches(initial) {
    const stores = new Map();
    for (const [name, entries] of Object.entries(initial)) {
        stores.set(name, new Map(Object.entries(entries)));
    }
    const caches = {
        failPut: null,
        failMatch: null,
        stores,
        keys: async () => [...stores.keys()],
        has: async name => stores.has(name),
        delete: async name => stores.delete(name),
        open: async name => {
            if (!stores.has(name)) {
                stores.set(name, new Map());
            }
            const store = stores.get(name);
            return {
                put: async (key, value) => {
                    if (caches.failPut) {
                        throw caches.failPut;
                    }
                    store.set(String(key), value);
                },
                match: async key => store.get(String(key))?.clone(),
                add: async () => {},
            };
        },
        match: async (key, options = {}) => {
            if (caches.failMatch) {
                throw caches.failMatch;
            }
            const names = options.cacheName ? [options.cacheName] : [...stores.keys()];
            for (const name of names) {
                const hit = stores.get(name)?.get(String(key));
                if (hit) {
                    return hit.clone();
                }
            }
            return undefined;
        },
    };
    return caches;
}

// `routes` maps a path (or a path + search) to a function returning a Response, a promise of one, or throwing for a
// network failure. Every call is recorded in `fetches`.
export function loadWorker({ cacheNames = [], caches: initial = {}, routes = {} } = {}) {
    const listeners = {};
    const posted = [];
    const fetches = [];
    const self = {
        location: { origin: ORIGIN },
        addEventListener: (type, fn) => { listeners[type] = fn; },
        clients: {
            claim: async () => { self.claimed = true; },
            get: async id => ({ id, postMessage: m => posted.push({ to: id, message: m }) }),
        },
        registration: {},
        skipWaiting: async () => {},
        claimed: false,
    };
    const caches = fakeCaches(Object.fromEntries([...cacheNames.map(n => [n, {}]), ...Object.entries(initial)]));
    const fetch = async (input, init) => {
        const url = new URL(typeof input === 'string' ? input : input.url, ORIGIN);
        fetches.push({ path: url.pathname, url: url.href, init });
        const route = routes[url.pathname + url.search] || routes[url.pathname];
        if (!route) {
            throw new TypeError('Failed to fetch');
        }
        return route(input, init);
    };
    const context = vm.createContext({ self, caches, fetch, Response, URL, Promise, Number, console });
    context.importScripts = (...paths) => {
        for (const path of paths) {
            vm.runInContext(readFileSync(fileURLToPath(new URL('.' + path, wwwroot + '/')), 'utf8'), context);
        }
    };
    vm.runInContext(readFileSync(fileURLToPath(new URL('js/service-worker.js', wwwroot + '/')), 'utf8'), context);
    return { listeners, self, caches, stored: caches.stores, posted, fetches, routes };
}

// Dispatches a fetch event. `handled` is false when the worker left the request to the network; `settle()` waits for
// the response and every waitUntil the worker registered while producing it.
export function dispatchFetch(worker, path, { clientId = 'tab-1', method = 'GET', headers = {}, mode = 'no-cors' } = {}) {
    const lifetime = [];
    const event = {
        request: { url: new URL(path, ORIGIN).href, method, mode, headers: new Headers(headers) },
        clientId,
        handled: false,
        respondWith(p) { event.handled = true; event.response = Promise.resolve(p); },
        waitUntil(p) { lifetime.push(p); },
    };
    worker.listeners.fetch(event);
    event.settle = async () => {
        const result = await event.response;
        for (let i = 0; i < lifetime.length; i++) {
            await lifetime[i];
        }
        return result;
    };
    return event;
}
