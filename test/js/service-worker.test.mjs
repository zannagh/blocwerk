// The service worker's activate purge, message protocol and wall-asset routing (service-worker.js and the
// sw-asset-cache.js it imports): run by `node --test test/js/`. The real worker files run in a vm sandbox with fake
// `self`, `caches`, `fetch` and `clients` (fixtures/sw-sandbox.mjs), and the tests dispatch events to the listeners
// the worker registers.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { loadWorker, dispatchFetch, response, ORIGIN } from './fixtures/sw-sandbox.mjs';

const SHA = 'ab'.repeat(32);
const OTHER_SHA = 'cd'.repeat(32);
const ALICE = 'u-' + 'a'.repeat(20);
const BOB = 'u-' + 'b'.repeat(20);
const PARTITION = '/api/me/cache-partition';
const asset = (sha = SHA, width = 1280, query = '') => `/api/walls/w1/a/${sha}${width ? '/' + width : ''}${query}`;

async function activate(worker) {
    let pending;
    worker.listeners.activate({ waitUntil: p => { pending = p; } });
    await pending;
}

function send(worker, data, port) {
    const sent = [];
    const target = { postMessage: m => sent.push(m) };
    worker.listeners.message({ data, ports: port ? [target] : [], source: port ? undefined : target });
    return sent;
}

// A worker whose partition lookup answers `label`, and whose asset routes answer `status` with an image body.
function workerFor(label, { status = 200, type = 'image/jpeg', body = 'jpeg-bytes', caches } = {}) {
    const routes = {
        [PARTITION]: () => response(200, JSON.stringify({ partition: label }), { 'content-type': 'application/json' }),
    };
    for (const path of [asset(), asset(SHA, 1280, '?token=t1'), asset(SHA, 0), asset(OTHER_SHA), asset(OTHER_SHA, null)]) {
        const [, sha, width] = /\/a\/([0-9a-f]{64})(?:\/(\d+))?/.exec(path);
        const etag = `"${sha}${width ? '-' + width : ''}"`;
        routes[path] = () => response(status, body,
            { 'content-type': type, 'content-length': String(body.length), etag });
    }
    return loadWorker({ routes, caches });
}

const assetFetches = worker => worker.fetches.filter(f => f.path.includes('/a/')).length;

test('activate keeps the asset caches and the worker state, deleting only old shells, the retired schema and probe scratch', async () => {
    const worker = loadWorker({ cacheNames: [
        'blocwerk-shell-v3', 'blocwerk-shell-v4',
        'bw-a0-user1-img', 'bw-a1-user1-img', 'bw-a1-user1-3d', 'bw-a1-anon-img', 'bw-sw-state',
        'bw-storage-probe', 'bw-storage-probe-ladder', 'something-else',
    ] });
    await activate(worker);
    assert.deepEqual([...worker.stored.keys()].sort(), [
        'blocwerk-shell-v4', 'bw-a1-anon-img', 'bw-a1-user1-3d', 'bw-a1-user1-img', 'bw-storage-probe', 'bw-sw-state',
        'something-else',
    ]);
    assert.equal(worker.self.claimed, true);
});

test('sw-info under proto 1 is answered on the passed port, or else to the sending client', () => {
    const worker = loadWorker();
    const expected = { proto: 1, type: 'sw-info', shell: 'blocwerk-shell-v4', partition: null };
    assert.deepEqual(send(worker, { proto: 1, type: 'sw-info' }, true).map(m => ({ ...m })), [expected]);
    assert.deepEqual(send(worker, { proto: 1, type: 'sw-info' }, false).map(m => ({ ...m })), [expected]);
});

test('sw-info reports the label the asset cache serves, once a lookup answered', async () => {
    const worker = workerFor(ALICE);
    await dispatchFetch(worker, asset()).settle();
    assert.equal(send(worker, { proto: 1, type: 'sw-info' }, true)[0].partition, ALICE);
});

test('messages with an unknown proto or type, or no object at all, are ignored without throwing', () => {
    const worker = loadWorker();
    for (const data of [{ proto: 2, type: 'sw-info' }, { type: 'sw-info' }, { proto: 1, type: 'asset-cached' },
        { proto: 1, type: 'partition', partition: ALICE }, { proto: '1', type: 'sw-info' }, null, undefined,
        'sw-info', 42]) {
        assert.deepEqual(send(worker, data, true), [], JSON.stringify(data));
    }
});

test('a miss goes to the network with the original request, then is stored under the bare sha key and announced', async () => {
    const worker = workerFor(ALICE);
    const event = dispatchFetch(worker, asset(SHA, 1280, '?token=t1'));
    assert.equal(event.handled, true);
    const served = await event.settle();
    assert.equal(served.status, 200);
    assert.equal(await served.text(), 'jpeg-bytes');
    assert.ok(worker.fetches.some(f => f.url.endsWith('?token=t1')), 'the network leg keeps the token');
    assert.deepEqual([...worker.stored.get(`bw-a1-${ALICE}-img`).keys()], [`${ORIGIN}/__bw/${SHA}/1280`]);
    assert.deepEqual(worker.posted.map(p => ({ to: p.to, ...p.message })), [
        { to: 'tab-1', proto: 1, type: 'asset-cached', sha: SHA, width: 1280, bytes: 10 },
    ]);
});

test('a hit is served from the cache for any wall, token or retry query, without touching the network', async () => {
    const worker = workerFor(ALICE);
    await dispatchFetch(worker, asset()).settle();
    const before = assetFetches(worker);
    const hit = await dispatchFetch(worker, `/api/walls/w2/a/${SHA}/1280?token=x&rev=3&bwretry=2`).settle();
    assert.equal(await hit.text(), 'jpeg-bytes');
    assert.equal(assetFetches(worker), before);
    // The original and each width are separate keys.
    await dispatchFetch(worker, asset(SHA, 0)).settle();
    assert.equal(assetFetches(worker), before + 1);
});

test('non-image bytes go to the 3D cache and hit from there', async () => {
    const worker = workerFor(ALICE, { type: 'application/octet-stream', body: 'splat' });
    await dispatchFetch(worker, asset(OTHER_SHA, null)).settle();
    assert.deepEqual([...worker.stored.get(`bw-a1-${ALICE}-3d`).keys()], [`${ORIGIN}/__bw/${OTHER_SHA}`]);
    assert.equal(await (await dispatchFetch(worker, asset(OTHER_SHA, null)).settle()).text(), 'splat');
    assert.equal(assetFetches(worker), 1);
});

test('a 404 (or any non-200) is passed through and never stored', async () => {
    for (const status of [404, 500, 304, 206]) {
        const worker = workerFor(ALICE, { status });
        const served = await dispatchFetch(worker, asset()).settle();
        assert.equal(served.status, status);
        assert.equal(worker.stored.has(`bw-a1-${ALICE}-img`), false, String(status));
        assert.deepEqual(worker.posted, []);
    }
});

test('an opaque or no-store response is not stored', async () => {
    const worker = workerFor(ALICE);
    worker.routes[asset()] = () => response(200, 'x', { 'content-type': 'image/jpeg', etag: `"${SHA}-1280"` }, 'opaque');
    worker.routes[asset(OTHER_SHA)] = () => response(200, 'x',
        { 'content-type': 'image/jpeg', 'cache-control': 'no-store', etag: `"${OTHER_SHA}-1280"` });
    await dispatchFetch(worker, asset()).settle();
    await dispatchFetch(worker, asset(OTHER_SHA)).settle();
    assert.equal(worker.stored.get(`bw-a1-${ALICE}-img`)?.size ?? 0, 0);
});

test('a partition switch stops serving the old partition, and a tab still on the old identity goes network-only', async () => {
    const worker = workerFor(ALICE);
    await dispatchFetch(worker, asset(), { clientId: 'alice-tab' }).settle();
    worker.routes[PARTITION] = () => response(200, JSON.stringify({ partition: BOB }));

    const bob = await dispatchFetch(worker, asset(), { clientId: 'bob-tab' }).settle();
    assert.equal(await bob.text(), 'jpeg-bytes');
    assert.equal(assetFetches(worker), 2, "Bob's page never reads Alice's partition");

    const stale = await dispatchFetch(worker, asset(OTHER_SHA), { clientId: 'alice-tab' }).settle();
    assert.equal(stale.status, 200);
    assert.deepEqual([...worker.stored.get(`bw-a1-${ALICE}-img`).keys()], [`${ORIGIN}/__bw/${SHA}/1280`],
        "the stale tab's response is written into neither partition");
    await dispatchFetch(worker, asset(), { clientId: 'alice-tab' }).settle();
    assert.equal(assetFetches(worker), 4, "the stale tab does not read Alice's partition either");
    assert.deepEqual([...worker.stored.get(`bw-a1-${BOB}-img`).keys()], [`${ORIGIN}/__bw/${SHA}/1280`]);
});

test('a switch while the bytes are in flight discards them instead of caching under the captured partition', async () => {
    const worker = workerFor(ALICE);
    let release;
    worker.routes[asset()] = () => new Promise(resolve => {
        release = () => resolve(response(200, 'late', { 'content-type': 'image/jpeg', etag: `"${SHA}-1280"` }));
    });
    const inFlight = dispatchFetch(worker, asset(), { clientId: 'alice-tab' });
    await new Promise(r => setTimeout(r, 0));
    worker.routes[PARTITION] = () => response(200, JSON.stringify({ partition: 'anon' }));
    await dispatchFetch(worker, asset(OTHER_SHA), { clientId: 'anon-tab' }).settle();
    release();
    assert.equal(await (await inFlight.settle()).text(), 'late');
    assert.equal(worker.stored.get(`bw-a1-${ALICE}-img`)?.size ?? 0, 0);
});

test('none, offline, a malformed label, a failed lookup or a missing client are network-only', async () => {
    for (const label of ['none', 'offline', 'u-NOTBASE32', '../x', 42]) {
        const worker = workerFor(label);
        await dispatchFetch(worker, asset()).settle();
        await dispatchFetch(worker, asset()).settle();
        assert.equal(assetFetches(worker), 2, String(label));
        assert.deepEqual([...worker.stored.keys()].filter(n => n.startsWith('bw-a1-')), [], String(label));
    }
    const failing = workerFor(ALICE);
    failing.routes[PARTITION] = () => response(500, 'oops');
    await dispatchFetch(failing, asset()).settle();
    const anonymous = workerFor(ALICE);
    await dispatchFetch(anonymous, asset(), { clientId: '' }).settle();
    for (const worker of [failing, anonymous]) {
        assert.equal(worker.stored.has(`bw-a1-${ALICE}-img`), false);
    }
});

test('a refused put (quota, full device) still hands the page the network response', async () => {
    const worker = workerFor(ALICE);
    worker.caches.failPut = Object.assign(new Error('quota'), { name: 'QuotaExceededError' });
    const served = await dispatchFetch(worker, asset()).settle();
    assert.equal(served.status, 200);
    assert.equal(await served.text(), 'jpeg-bytes');
    assert.equal(worker.stored.get(`bw-a1-${ALICE}-img`)?.size ?? 0, 0);
    assert.deepEqual(worker.posted.map(p => [p.message.type, p.message.sha, p.message.width]), [['asset-refused', SHA, 1280]],
        'the page hears of the refusal, so a slow write is never taken for one');
});

test('`unknown` (the server could not resolve the partition) is network-only and never falls back to the last label', async () => {
    const worker = workerFor(ALICE);
    await dispatchFetch(worker, asset()).settle();
    assert.equal(assetFetches(worker), 1);
    worker.routes[PARTITION] = () => response(200, JSON.stringify({ partition: 'unknown' }), { 'content-type': 'application/json' });

    await dispatchFetch(worker, asset(), { clientId: 'page-2' }).settle();
    assert.equal(assetFetches(worker), 2, 'the cached copy under the last label is not served');
    assert.notEqual(await worker.stored.get('bw-sw-state').get('/__bw/partition').clone().text(), ALICE);
});

test('a failing cache lookup falls back to the network', async () => {
    const worker = workerFor(ALICE);
    worker.caches.failMatch = new Error('storage gone');
    assert.equal(await (await dispatchFetch(worker, asset()).settle()).text(), 'jpeg-bytes');
});

test('offline, the last partition (kept across worker restarts) still serves its cache', async () => {
    const online = workerFor(ALICE);
    await dispatchFetch(online, asset()).settle();
    const kept = Object.fromEntries([...online.stored].map(([name, entries]) => [name, Object.fromEntries(entries)]));
    assert.equal(await kept['bw-sw-state'][`/__bw/partition`].clone().text(), ALICE);

    const restarted = loadWorker({ caches: kept });
    const served = await dispatchFetch(restarted, asset()).settle();
    assert.equal(await served.text(), 'jpeg-bytes');
    assert.equal(assetFetches(restarted), 0);

    const fresh = loadWorker({ caches: { [`bw-a1-${ALICE}-img`]: kept[`bw-a1-${ALICE}-img`] } });
    await assert.rejects(dispatchFetch(fresh, asset()).settle(), /Failed to fetch/,
        'with no remembered partition an offline worker does not guess one');
});

test('a slow older lookup never overwrites a newer answer', async () => {
    const worker = workerFor(ALICE);
    let answerOld;
    worker.routes[PARTITION] = () => new Promise(resolve => {
        answerOld = () => resolve(response(200, JSON.stringify({ partition: ALICE })));
    });
    const old = dispatchFetch(worker, asset(), { clientId: 'old-tab' });
    await new Promise(r => setTimeout(r, 0));
    worker.routes[PARTITION] = () => response(200, JSON.stringify({ partition: BOB }));
    await dispatchFetch(worker, asset(), { clientId: 'new-tab' }).settle();
    answerOld();
    await old.settle();
    await dispatchFetch(worker, asset(), { clientId: 'new-tab' }).settle();
    assert.equal(worker.stored.has(`bw-a1-${ALICE}-img`), false);
    assert.equal(assetFetches(worker), 2, "Bob's second request is a hit; the old tab went to the network");
});

test('a view3d document is stored in the 3D cache under its bare sha, whatever its token or boulder hint, and hits from there', async () => {
    const worker = workerFor(ALICE);
    const doc = query => `/api/walls/w1/d/view3d/${OTHER_SHA}${query}`;
    worker.routes[doc('?token=t1&boulder=b1')] = () => response(200, '{"wallId":"w1"}',
        { 'content-type': 'application/json', 'content-length': '15', etag: `"${OTHER_SHA}"` });
    await dispatchFetch(worker, doc('?token=t1&boulder=b1')).settle();
    assert.deepEqual([...worker.stored.get(`bw-a1-${ALICE}-3d`).keys()], [`${ORIGIN}/__bw/${OTHER_SHA}`]);
    assert.equal(await (await dispatchFetch(worker, doc('')).settle()).text(), '{"wallId":"w1"}');
    assert.equal(worker.fetches.filter(f => f.path.includes('/d/')).length, 1);
    assert.deepEqual(worker.posted.map(({ message: m }) => [m.type, m.sha, m.width]), [['asset-cached', OTHER_SHA, null]]);
});

test('requests other than a same-origin GET of an /a/ sha URL or a view3d document are left as they were', () => {
    const worker = workerFor(ALICE);
    const untouched = [
        ['/api/walls/w1/photo'],
        ['/api/walls/w1/manifest'],
        [PARTITION],
        [`/api/walls/w1/a/${SHA.toUpperCase()}`],
        [`/api/walls/w1/a/${SHA.slice(1)}`],
        [`/api/walls/w1/a/${SHA}/wide`],
        [`/api/walls/w1/d/holds/${SHA}`],
        [`/api/walls/w1/d/view3d/${SHA}/640`],
        [`/api/walls/w1/d/view3d/${SHA.toUpperCase()}`],
        [`/api/users/u1/avatar`],
        [asset(), { method: 'POST' }],
        [asset(), { method: 'HEAD' }],
        [asset(SHA, null), { headers: { range: 'bytes=0-99' } }],
        [`https://elsewhere.test${asset()}`],
    ];
    for (const [path, options] of untouched) {
        assert.equal(dispatchFetch(worker, path, options).handled, false, path + JSON.stringify(options ?? {}));
    }
    assert.deepEqual(worker.fetches, [], 'not even a partition lookup');
    assert.equal(dispatchFetch(worker, '/css/app.css').handled, true, 'the shell routes still apply');
});

test("a 200 whose ETag does not name the sha (a proxy's error page) is served but not stored", async () => {
    const worker = workerFor(ALICE);
    worker.routes[asset()] = () => response(200, '<html>maintenance</html>', { 'content-type': 'image/jpeg' });
    worker.routes[asset(OTHER_SHA)] = () => response(200, 'x', { 'content-type': 'image/jpeg', etag: `"${SHA}-1280"` });
    assert.equal(await (await dispatchFetch(worker, asset()).settle()).text(), '<html>maintenance</html>');
    await dispatchFetch(worker, asset(OTHER_SHA)).settle();
    assert.equal(worker.stored.get(`bw-a1-${ALICE}-img`)?.size ?? 0, 0);
});

test("a kiosk session's no-store answer (a kind outside the tablet's class) is never stored", async () => {
    const KIOSK = 'k-' + 'k'.repeat(20);
    const worker = workerFor(KIOSK);
    worker.routes[asset()] = () => response(200, 'thumb', {
        'content-type': 'image/jpeg', etag: `"${SHA}-1280"`, 'cache-control': 'private, no-store' });
    assert.equal(await (await dispatchFetch(worker, asset()).settle()).text(), 'thumb');
    await dispatchFetch(worker, asset(), { clientId: 'tab-2' }).settle();
    assert.equal(assetFetches(worker), 2, 'the second viewer asks the server again');
    assert.equal(worker.stored.get(`bw-a1-${KIOSK}-img`)?.size ?? 0, 0);
});

test('a lookup the server cannot answer keeps the last label, persists nothing and is asked again', async () => {
    const failures = {
        '502': () => response(502, 'Bad Gateway'),
        'opaque': () => response(200, JSON.stringify({ partition: BOB }), {}, 'opaque'),
        'html 200': () => response(200, '<html>login</html>', { 'content-type': 'text/html' }),
        'no partition': () => response(200, '{}', { 'content-type': 'application/json' }),
    };
    for (const [name, failure] of Object.entries(failures)) {
        const worker = workerFor(ALICE);
        await dispatchFetch(worker, asset(), { clientId: 'first' }).settle();
        worker.routes[PARTITION] = failure;
        const lookups = () => worker.fetches.filter(f => f.path === PARTITION).length;

        const served = await dispatchFetch(worker, asset(), { clientId: 'during-deploy' }).settle();
        assert.equal(await served.text(), 'jpeg-bytes', name);
        assert.equal(assetFetches(worker), 1, `${name}: still served from the last label's cache`);
        await dispatchFetch(worker, asset(OTHER_SHA), { clientId: 'during-deploy' }).settle();
        assert.equal(worker.stored.get(`bw-a1-${ALICE}-img`).size, 2, `${name}: and still stored there`);
        assert.equal(await worker.stored.get('bw-sw-state').get('/__bw/partition').clone().text(), ALICE, name);
        assert.equal(lookups(), 3, `${name}: a failed lookup is not remembered for the page`);

        worker.routes[PARTITION] = () => response(200, JSON.stringify({ partition: BOB }));
        await dispatchFetch(worker, asset(), { clientId: 'during-deploy' }).settle();
        assert.equal(worker.stored.has(`bw-a1-${BOB}-img`), true, `${name}: the next real answer applies`);
    }
});

test('a lookup that hangs is abandoned at the timeout: that request goes to the network and nothing is remembered', async () => {
    const timers = [];
    const worker = loadWorker({ setTimeout: (fn, ms) => { timers.push({ fn, ms }); return timers.length; } });
    Object.assign(worker.routes, workerFor(ALICE).routes);
    await dispatchFetch(worker, asset(), { clientId: 'first' }).settle();
    worker.routes[PARTITION] = () => new Promise(() => {});

    const hung = dispatchFetch(worker, asset(), { clientId: 'stuck' });
    await new Promise(r => setTimeout(r, 0));
    assert.equal(timers.at(-1).ms, 2500);
    timers.at(-1).fn();
    assert.equal(await (await hung.settle()).text(), 'jpeg-bytes');
    assert.equal(assetFetches(worker), 2, 'network-only, not served from the cache');
    assert.equal(worker.stored.get(`bw-a1-${ALICE}-img`).size, 1);

    worker.routes[PARTITION] = () => response(200, JSON.stringify({ partition: ALICE }));
    await dispatchFetch(worker, asset(), { clientId: 'stuck' }).settle();
    assert.equal(assetFetches(worker), 2, 'the page asks again and, answered, reads its cache');
});

test('sw-refresh-partition makes the worker ask the server again, so sw-info follows the cookie without an /a/ request', async () => {
    const worker = workerFor(ALICE);
    await dispatchFetch(worker, asset()).settle();
    worker.routes[PARTITION] = () => response(200, JSON.stringify({ partition: BOB }), { 'content-type': 'application/json' });
    assert.equal(send(worker, { proto: 1, type: 'sw-info' }, true)[0].partition, ALICE);

    let pending;
    worker.listeners.message({ data: { proto: 1, type: 'sw-refresh-partition' }, ports: [], source: { id: 'tab-1' }, waitUntil: p => { pending = p; } });
    await pending;
    assert.equal(send(worker, { proto: 1, type: 'sw-info' }, true)[0].partition, BOB);
    assert.equal(worker.fetches.filter(f => f.path === PARTITION).length, 2);
});
