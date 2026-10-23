// The worker's offline client precache (sw-client-fill.js, Phase 3 PR 3.9): one client version per cache with its
// completeness record, resumable fills, and old versions deleted only by a completed fill, never by activate. The real
// worker runs in the vm sandbox (fixtures/sw-sandbox.mjs, fixtures/sw-client.mjs).
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { loadWorker, dispatchFetch, response, ORIGIN } from './fixtures/sw-sandbox.mjs';
import { V1, V2, ALICE, KIOSK, ROUTES, STATE, abs, shellHtml, dotnetJs, deploy, workerWith, restart, precache, info, tick, navigate, fetched, completeWorker } from './fixtures/sw-client.mjs';

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
    assert.equal((await info(worker)).client, V1);
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
    assert.equal((await info(worker)).client, null);

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
        assert.equal((await info(worker)).client, null, name);
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
    assert.equal((await info(worker)).client, V1, 'the old version stays the complete one');

    deploy(worker.routes, V2);
    assert.equal((await precache(worker)).version, V2);
    assert.deepEqual([...worker.stored.keys()].filter(n => n.startsWith('blocwerk-client-')), [`blocwerk-client-${V2}`]);
    assert.equal((await info(worker)).client, V2);
});

async function activate(worker) {
    let pending;
    worker.listeners.activate({ waitUntil: p => { pending = p; } });
    await pending;
    return [...worker.stored.keys()].filter(n => n.startsWith('blocwerk-client-')).sort();
}

test('activate never deletes a client version: a newer worker cannot drop a fill another worker is running', async () => {
    const caches = { [`blocwerk-client-${V1}`]: {}, [`blocwerk-client-${V2}`]: {} };
    assert.deepEqual(await activate(workerWith({ caches, record: { version: V1, routes: [] } })), [`blocwerk-client-${V1}`, `blocwerk-client-${V2}`]);
});

test('a worker update during a fill: the fill still completes, and a record whose cache is gone is never served', async () => {
    const worker = workerWith();
    deploy(worker.routes, V1);
    let release;
    const wasm = worker.routes['/v/_framework/dotnet.native.w1.wasm'];
    worker.routes['/v/_framework/dotnet.native.w1.wasm'] = () => new Promise(resolve => { release = () => resolve(wasm()); });
    const filling = precache(worker);
    while (!release) {
        await tick();
    }
    // The next worker activates on the same caches while this one is still filling.
    const next = loadWorker({ caches: Object.fromEntries([...worker.stored].map(([name, entries]) => [name, Object.fromEntries(entries)])) });
    next.caches.stores = worker.caches.stores;
    await activate(next);
    release();
    worker.routes['/v/_framework/dotnet.native.w1.wasm'] = wasm;
    assert.equal((await filling).complete, true);
    assert.equal(await (await navigate(worker, '/walls/W1')).status, 302);

    // The record survives but its cache does not (the browser evicted it, or an older worker's activate deleted it).
    await worker.caches.delete(`blocwerk-client-${V1}`);
    assert.equal((await info(worker)).client, null, 'the page is told to precache again');
    assert.equal(await (await navigate(worker, '/walls/W1')).text(), 'offline page', 'nothing is handed to a missing cache');
    assert.equal((await precache(worker)).complete, true, 'the next precache fills it again');
    assert.equal((await info(worker)).client, V1);
});

test('auth, kiosk, dev, admin and client paths are never handed over, even under a catch-all route', async () => {
    const worker = workerWith();
    deploy(worker.routes, V1, { shell: shellHtml(V1, '{*all}') });
    assert.equal((await precache(worker)).complete, true);
    for (const path of ['/account/login', '/Account/callback', '/signin-oidc', '/signout-callback', '/signing-in', '/signed-out',
        '/signup', '/oauth-select', '/oauth-callback', '/forgot-password', '/reset-password', '/join/t', '/kiosk/pair', '/dev/login',
        '/settings/api-keys', '/administration', '/access-denied', '/v', '/V/walls/x']) {
        assert.equal(await (await navigate(worker, path)).text(), 'offline page', path);
    }
    assert.equal((await navigate(worker, '/walls/W1')).status, 302);
    assert.equal((await navigate(worker, '/vertical')).status, 302, 'a segment that only starts with v is no client path');
});
