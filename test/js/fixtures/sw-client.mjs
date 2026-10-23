// The offline client's worker fixtures (service-worker.js's offline client section; PR 3.9): a deploy's shell and files,
// a worker with a given partition, lock or record, and the messages the page sends. For sw-client-*.test.mjs.
import assert from 'node:assert/strict';
import { loadWorker, dispatchFetch, response, ORIGIN } from './sw-sandbox.mjs';

export const V1 = '1111111111111111';
export const V2 = '2222222222222222';
export const ALICE = 'u-' + 'a'.repeat(20);
export const KIOSK = 'k-' + 'k'.repeat(20);
export const ROUTES = 'walls/{w} walls/{w}/boulders/{b} shared/{*rest}';
export const STATE = 'bw-sw-state';
export const abs = path => ORIGIN + path;

export function shellHtml(version, routes = ROUTES, extra = '') {
    return `<!DOCTYPE html><html><head><meta name="bw-partition" content="offline" />
<meta name="bw-client-version" content="${version}" /><meta name="bw-client-routes" content="${routes}" />
<base href="/v/" /><script type="importmap">{"imports":{"/v/_framework/dotnet.js":"/v/_framework/dotnet.${version.slice(0, 4)}.js",
"/v/_framework/dotnet.native.js":"/v/_framework/dotnet.native.n1.js"},"integrity":{}}</script>
<link rel="stylesheet" href="/v/css/client.c${version.slice(0, 2)}.css" /><link rel="stylesheet" href="/_content/Blocwerk.Shared.Ui/css/hold-tap.h1.css" />
</head><body><script src="/_content/Blocwerk.Shared.Ui/js/asset-ledger.l1.js"></script><script src="/js/sw-guard.g1.js"></script>
<script src="/v/_framework/blazor.webassembly.b1.js" autostart="false"></script>${extra}</body></html>`;
}

export const dotnetJs = assemblies => `export const x=1;/*json-start*/${JSON.stringify({ resources: {
    jsModuleNative: [{ name: 'dotnet.native.n1.js' }], wasmNative: [{ name: 'dotnet.native.w1.wasm' }],
    coreAssembly: [{ name: 'System.Private.CoreLib.c1.wasm' }], assembly: assemblies.map(name => ({ name })),
    lazyAssembly: [{ name: 'Lazy.z9.wasm' }] } })}/*json-end*/`;

// A deploy serving client `version`: its shell and every file the shell and the boot manifest name.
export function deploy(routes, version, { shell = shellHtml(version) } = {}) {
    const file = body => () => response(200, body, { 'content-type': 'application/octet-stream' });
    Object.assign(routes, {
        '/v/': () => response(200, shell, { 'content-type': 'text/html', 'cache-control': 'no-cache' }),
        [`/v/_framework/dotnet.${version.slice(0, 4)}.js`]: file(dotnetJs([`Blocwerk.Client.${version.slice(0, 4)}.wasm`])),
        '/v/_framework/dotnet.native.n1.js': file('native-js'),
        '/v/_framework/dotnet.native.w1.wasm': file('wasm'),
        '/v/_framework/System.Private.CoreLib.c1.wasm': file('corelib'),
        [`/v/_framework/Blocwerk.Client.${version.slice(0, 4)}.wasm`]: file('client-' + version),
        '/v/_framework/blazor.webassembly.b1.js': file('loader'),
        [`/v/css/client.c${version.slice(0, 2)}.css`]: file('css'),
        '/_content/Blocwerk.Shared.Ui/css/hold-tap.h1.css': file('hold-tap'),
        '/_content/Blocwerk.Shared.Ui/js/asset-ledger.l1.js': file('ledger'),
        '/js/sw-guard.g1.js': file('guard'),
    });
}

export function workerWith({ partition = ALICE, locked = null, record = null, caches = {}, timers = null } = {}) {
    const state = { '/__bw/partition': response(200, partition) };
    if (locked) {
        state['/__bw/locked'] = response(200, locked);
    }
    if (record) {
        state['/__bw/client'] = response(200, JSON.stringify(record));
    }
    const options = { caches: { [STATE]: state, 'blocwerk-shell-v5': { '/offline.html': response(200, 'offline page') }, ...caches } };
    if (timers) {
        options.setTimeout = (fn, ms) => { timers.push({ fn, ms }); return timers.length; };
    }
    return loadWorker(options);
}

// The same device after the worker was stopped and started again, with `change` applied to its state cache first.
export function restart(worker, change) {
    change(worker.stored.get(STATE));
    return loadWorker({ caches: Object.fromEntries([...worker.stored].map(([name, entries]) => [name, Object.fromEntries(entries)])) });
}

export function precache(worker) {
    let pending;
    const sent = [];
    worker.listeners.message({ data: { proto: 1, type: 'client-precache' }, ports: [{ postMessage: m => sent.push(m) }], waitUntil: p => { pending = p; } });
    return pending.then(() => sent[0]);
}

export const info = async worker => {
    const sent = [];
    let pending;
    worker.listeners.message({ data: { proto: 1, type: 'sw-info' }, ports: [{ postMessage: m => sent.push(m) }], waitUntil: p => { pending = p; } });
    await pending;
    return sent[0];
};
export const tick = () => new Promise(resolve => setTimeout(resolve, 0));
export const navigate = (worker, path) => dispatchFetch(worker, path, { mode: 'navigate' }).settle();
export const fetched = (worker, path) => worker.fetches.filter(f => f.path === path).length;

export async function completeWorker(options = {}) {
    const worker = workerWith(options);
    deploy(worker.routes, V1);
    assert.deepEqual({ ...(await precache(worker)) }, { proto: 1, type: 'client-precached', version: V1, complete: true });
    return worker;
}
