// The worker's half of the offline client (sw-client-cache.js, Phase 3 PR 3.9): the page-triggered precache of one
// client version with its completeness record, the activate rule for old versions, and the hand-off of a failed
// Server-page navigation to /v/ (network error, 5xx, or the 4 s timer), refused for a locked partition, a kiosk and an
// uncovered route. The real worker runs in the vm sandbox (fixtures/sw-sandbox.mjs).
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { loadWorker, dispatchFetch, response, ORIGIN } from './fixtures/sw-sandbox.mjs';

const V1 = '1111111111111111';
const V2 = '2222222222222222';
const ALICE = 'u-' + 'a'.repeat(20);
const KIOSK = 'k-' + 'k'.repeat(20);
const ROUTES = 'walls/{w} walls/{w}/boulders/{b} shared/{*rest}';
const STATE = 'bw-sw-state';
const abs = path => ORIGIN + path;

function shellHtml(version, routes = ROUTES, extra = '') {
    return `<!DOCTYPE html><html><head><meta name="bw-partition" content="offline" />
<meta name="bw-client-version" content="${version}" /><meta name="bw-client-routes" content="${routes}" />
<base href="/v/" /><script type="importmap">{"imports":{"/v/_framework/dotnet.js":"/v/_framework/dotnet.${version.slice(0, 4)}.js",
"/v/_framework/dotnet.native.js":"/v/_framework/dotnet.native.n1.js"},"integrity":{}}</script>
<link rel="stylesheet" href="/v/css/client.c${version.slice(0, 2)}.css" /><link rel="stylesheet" href="/_content/Blocwerk.Shared.Ui/css/hold-tap.h1.css" />
</head><body><script src="/_content/Blocwerk.Shared.Ui/js/asset-ledger.l1.js"></script><script src="/js/sw-guard.g1.js"></script>
<script src="/v/_framework/blazor.webassembly.b1.js" autostart="false"></script>${extra}</body></html>`;
}

const dotnetJs = assemblies => `export const x=1;/*json-start*/${JSON.stringify({ resources: {
    jsModuleNative: [{ name: 'dotnet.native.n1.js' }], wasmNative: [{ name: 'dotnet.native.w1.wasm' }],
    coreAssembly: [{ name: 'System.Private.CoreLib.c1.wasm' }], assembly: assemblies.map(name => ({ name })),
    lazyAssembly: [{ name: 'Lazy.z9.wasm' }] } })}/*json-end*/`;

// A deploy serving client `version`: its shell and every file the shell and the boot manifest name.
function deploy(routes, version, { shell = shellHtml(version) } = {}) {
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

function workerWith({ partition = ALICE, locked = null, record = null, caches = {}, timers = null } = {}) {
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
function restart(worker, change) {
    change(worker.stored.get(STATE));
    return loadWorker({ caches: Object.fromEntries([...worker.stored].map(([name, entries]) => [name, Object.fromEntries(entries)])) });
}

function precache(worker) {
    let pending;
    const sent = [];
    worker.listeners.message({ data: { proto: 1, type: 'client-precache' }, ports: [{ postMessage: m => sent.push(m) }], waitUntil: p => { pending = p; } });
    return pending.then(() => sent[0]);
}

const info = worker => {
    const sent = [];
    worker.listeners.message({ data: { proto: 1, type: 'sw-info' }, ports: [{ postMessage: m => sent.push(m) }] });
    return sent[0];
};
const tick = () => new Promise(resolve => setTimeout(resolve, 0));
const navigate = (worker, path) => dispatchFetch(worker, path, { mode: 'navigate' }).settle();
const fetched = (worker, path) => worker.fetches.filter(f => f.path === path).length;

async function completeWorker(options = {}) {
    const worker = workerWith(options);
    deploy(worker.routes, V1);
    assert.deepEqual({ ...(await precache(worker)) }, { proto: 1, type: 'client-precached', version: V1, complete: true });
    return worker;
}

test('a precache fills one versioned cache with the shell, every file it names and the eager boot files, then writes the record', async () => {
    const worker = await completeWorker();
    const files = [...worker.stored.get(`blocwerk-client-${V1}`).keys()].sort();
    assert.deepEqual(files, [
        '/_content/Blocwerk.Shared.Ui/css/hold-tap.h1.css', '/_content/Blocwerk.Shared.Ui/js/asset-ledger.l1.js',
        '/js/sw-guard.g1.js', '/v/', `/v/_framework/Blocwerk.Client.${V1.slice(0, 4)}.wasm`,
        '/v/_framework/System.Private.CoreLib.c1.wasm', '/v/_framework/blazor.webassembly.b1.js',
        `/v/_framework/dotnet.${V1.slice(0, 4)}.js`, '/v/_framework/dotnet.native.n1.js', '/v/_framework/dotnet.native.w1.wasm',
        '/v/css/client.c11.css'].map(abs).sort(), 'the shared library stays at /_content/, never under /v/; no lazy assembly');
    const record = JSON.parse(await worker.stored.get(STATE).get('/__bw/client').clone().text());
    assert.deepEqual(record, { version: V1, routes: ROUTES.split(' ') });
    assert.equal(info(worker).client, V1);
    assert.equal(fetched(worker, '/v/'), 1);
    assert.equal(worker.fetches.find(f => f.path === '/v/').init.redirect, 'error', 'a redirected shell (a kiosk sent away) fails');
});

test('a second precache of the complete version fetches only the shell', async () => {
    const worker = await completeWorker();
    const before = worker.fetches.length;
    assert.equal((await precache(worker)).complete, true);
    assert.deepEqual(worker.fetches.slice(before).map(f => f.path), ['/v/']);
});

test('a failed file leaves no record; the next precache resumes and fetches only what is missing', async () => {
    const worker = workerWith();
    deploy(worker.routes, V1);
    worker.routes['/v/_framework/System.Private.CoreLib.c1.wasm'] = () => response(503, 'deploying');
    assert.equal((await precache(worker)).complete, false);
    assert.equal(worker.stored.get(STATE).has('/__bw/client'), false);
    assert.equal(worker.stored.get(`blocwerk-client-${V1}`).has(abs('/v/')), false, 'no shell before every file is in');
    assert.equal(info(worker).client, null);

    deploy(worker.routes, V1);
    const before = worker.fetches.length;
    assert.equal((await precache(worker)).complete, true);
    assert.deepEqual(worker.fetches.slice(before).map(f => f.path).sort(), ['/v/', '/v/_framework/System.Private.CoreLib.c1.wasm']);
});

test('a broken shell or boot manifest never completes, and the worker keeps working', async () => {
    const broken = {
        'no version meta': shellHtml(V1).replace('bw-client-version', 'bw-other'),
        'no routes meta': shellHtml(V1).replace('bw-client-routes', 'bw-other'),
        'no dotnet.js': shellHtml(V1).replace('"/v/_framework/dotnet.js"', '"/v/_framework/other.js"'),
    };
    for (const [name, shell] of Object.entries(broken)) {
        const worker = workerWith();
        deploy(worker.routes, V1, { shell });
        assert.equal((await precache(worker)).complete, false, name);
        assert.equal(worker.stored.get(STATE).has('/__bw/client'), false, name);
    }
    for (const [name, answer] of Object.entries({
        'no boot manifest': () => response(200, 'export const x = 1;'),
        'a redirected shell': () => Object.defineProperty(response(200, shellHtml(V1)), 'redirected', { value: true }),
        'a 403 shell': () => response(403, 'kiosk'),
        'an opaque shell': () => response(200, shellHtml(V1), {}, 'opaque'),
    })) {
        const worker = workerWith();
        deploy(worker.routes, V1);
        worker.routes[name === 'no boot manifest' ? `/v/_framework/dotnet.${V1.slice(0, 4)}.js` : '/v/'] = answer;
        assert.equal((await precache(worker)).complete, false, name);
        assert.equal(info(worker).client, null, name);
    }
});

test('a kiosk partition is never precached', async () => {
    const worker = workerWith({ partition: KIOSK });
    deploy(worker.routes, V1);
    assert.equal((await precache(worker)).complete, false);
    assert.equal(fetched(worker, '/v/'), 0);
});

test('a new version replaces the old one only once it is complete', async () => {
    const worker = await completeWorker();
    deploy(worker.routes, V2);
    worker.routes['/v/_framework/dotnet.native.w1.wasm'] = () => { throw new TypeError('Failed to fetch'); };
    assert.deepEqual({ ...(await precache(worker)) }, { proto: 1, type: 'client-precached', version: V1, complete: false });
    assert.ok(worker.stored.has(`blocwerk-client-${V1}`) && worker.stored.has(`blocwerk-client-${V2}`));
    assert.equal(info(worker).client, V1, 'the old version stays the complete one');

    deploy(worker.routes, V2);
    assert.equal((await precache(worker)).version, V2);
    assert.deepEqual([...worker.stored.keys()].filter(n => n.startsWith('blocwerk-client-')), [`blocwerk-client-${V2}`]);
    assert.equal(info(worker).client, V2);
});

test('activate deletes other client versions only when one is complete', async () => {
    const run = async worker => {
        let pending;
        worker.listeners.activate({ waitUntil: p => { pending = p; } });
        await pending;
        return [...worker.stored.keys()].filter(n => n.startsWith('blocwerk-client-')).sort();
    };
    const caches = { [`blocwerk-client-${V1}`]: {}, [`blocwerk-client-${V2}`]: {} };
    assert.deepEqual(await run(workerWith({ caches })), [`blocwerk-client-${V1}`, `blocwerk-client-${V2}`]);
    assert.deepEqual(await run(workerWith({ caches, record: { version: V2, routes: [] } })), [`blocwerk-client-${V2}`]);
});

test('a failed navigation goes to the client route when the version is complete and covers the path', async () => {
    const worker = await completeWorker();
    const served = await navigate(worker, '/walls/W1/boulders/B2?view=x');
    assert.equal(served.status, 302);
    assert.equal(served.headers.get('location'), `${ORIGIN}/v/walls/W1/boulders/B2?view=x`);
    assert.equal((await navigate(worker, '/Shared/t0k/walls/w')).headers.get('location'), `${ORIGIN}/v/Shared/t0k/walls/w`);
    for (const uncovered of ['/', '/settings', '/walls/W1/edit', '/walls']) {
        assert.equal(await (await navigate(worker, uncovered)).text(), 'offline page', uncovered);
    }
});

test('a locked partition, a kiosk, no partition or no complete version gets offline.html, never the client', async () => {
    const cases = {
        locked: state => state.set('/__bw/locked', response(200, ALICE)),
        kiosk: state => state.set('/__bw/partition', response(200, KIOSK)),
        none: state => state.set('/__bw/partition', response(200, 'none')),
    };
    for (const [name, change] of Object.entries(cases)) {
        const fresh = restart(await completeWorker(), change);
        assert.equal(info(fresh).client, null, `${name}: before the worker read its record`);
        assert.equal(await (await navigate(fresh, '/walls/W1')).text(), 'offline page', name);
        assert.equal(fetched(fresh, '/v/'), 0, name);
    }
    assert.equal(await (await navigate(workerWith(), '/walls/W1')).text(), 'offline page', 'incomplete');
    const anon = await completeWorker({ partition: 'anon' });
    assert.equal((await navigate(anon, '/shared/t/x')).status, 302, 'a share viewer is handed over');
});

test('a 5xx is handed over when the client can take it, and passed through when it cannot', async () => {
    const worker = await completeWorker();
    worker.routes['/walls/W1'] = () => response(502, 'Bad Gateway');
    worker.routes['/settings'] = () => response(503, 'Unavailable');
    worker.routes['/walls/W2'] = () => response(404, 'nope');
    assert.equal((await navigate(worker, '/walls/W1')).status, 302);
    assert.equal((await navigate(worker, '/settings')).status, 503);
    assert.equal((await navigate(worker, '/walls/W2')).status, 404, 'a 4xx is an answer');
});

test('the 4 s timer redirects a hanging navigation only when the client can take it; otherwise it keeps waiting', async () => {
    const timers = [];
    const worker = workerWith({ timers });
    deploy(worker.routes, V1);
    assert.equal((await precache(worker)).complete, true);
    let release;
    worker.routes['/walls/W1'] = () => new Promise(resolve => { release = () => resolve(response(200, 'late page')); });
    worker.routes['/settings'] = () => new Promise(resolve => { release = () => resolve(response(200, 'slow settings')); });

    const hanging = dispatchFetch(worker, '/walls/W1', { mode: 'navigate' });
    await tick();
    const handoff = timers.filter(t => t.ms === 4000);
    assert.equal(handoff.length, 1);
    handoff[0].fn();
    const served = await hanging.settle();
    assert.equal(served.headers.get('location'), `${ORIGIN}/v/walls/W1`);
    release();

    const slow = dispatchFetch(worker, '/settings', { mode: 'navigate' });
    await tick();
    timers.filter(t => t.ms === 4000).at(-1).fn();
    await tick();
    release();
    assert.equal(await (await slow.settle()).text(), 'slow settings', 'an uncovered route waits for the server');
});

test('a healthy navigation is the network answer, whatever the client state', async () => {
    const worker = await completeWorker();
    worker.routes['/walls/W1'] = () => response(200, 'server page');
    assert.equal(await (await navigate(worker, '/walls/W1')).text(), 'server page');
});

test('a /v/ navigation is the cached shell when servable, else the network', async () => {
    const worker = await completeWorker();
    const before = fetched(worker, '/v/');
    const served = await navigate(worker, '/v/walls/W1');
    assert.match(await served.text(), /bw-client-version" content="1111111111111111"/);
    assert.equal(fetched(worker, '/v/walls/W1') + fetched(worker, '/v/') - before, 0);

    worker.stored.get(STATE).set('/__bw/locked', response(200, ALICE));
    worker.routes['/v/walls/W1'] = () => response(200, 'network shell');
    assert.equal(await (await navigate(worker, '/v/walls/W1')).text(), 'network shell', 'locked: the server decides');
});

test("the client's files are cache-first, and a /v/ page's /_content/ and /js/ files too", async () => {
    const worker = await completeWorker();
    const before = worker.fetches.length;
    const client = { referrer: `${ORIGIN}/v/walls/W1` };
    for (const [path, options] of [['/v/_framework/dotnet.native.w1.wasm', {}], ['/_content/Blocwerk.Shared.Ui/js/asset-ledger.l1.js', client], ['/js/sw-guard.g1.js', client]]) {
        const served = await dispatchFetch(worker, path, options).settle();
        assert.equal(served.status, 200, path);
    }
    assert.equal(worker.fetches.length, before, 'no network');
    await dispatchFetch(worker, '/js/sw-guard.g1.js', { referrer: `${ORIGIN}/walls/W1` }).settle();
    assert.equal(worker.fetches.length, before + 1, "a Server page's own asset stays network-first");
    worker.routes['/v/_framework/missing.wasm'] = () => response(404, 'no');
    assert.equal((await dispatchFetch(worker, '/v/_framework/missing.wasm').settle()).status, 404, 'a miss goes to the network');
});
