// The offline client's network side (Phase 3 PR 3.12, client-sync.js) over the real shared modules and Web's refreshers
// (asset-manifest/data/mine.js), as the shell loads them: the docs index writes the ledger row as a Server page does and
// deletes a replaced comments document at once, but never acts on a refusal itself (the C# loop hands every refusal to
// refused()); the marks reach the ledger for `u-` only; catch-up pages carry the status, the sign-out reason and
// Retry-After; the stream forwards its messages and closes itself on a bye or an error, so the browser never
// reconnects on its own; a refusal's lock or wipe reaches the Server tabs; the page's signals; the worker's
// asset-cached rows; the guarded status bubbles. Run by `node --test test/js/`.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { json, USER_A, KIOSK, ORIGIN } from './fixtures/page-sandbox.mjs';
import { W1, memberPage } from './fixtures/asset-pages.mjs';
import { loadClient } from './fixtures/client-sandbox.mjs';
import { TOKEN, plain, same, sha, device } from './fixtures/client-device.mjs';

const tick = () => new Promise(resolve => setImmediate(resolve));

// A fetch answering by path (query included when the route names one); every call is logged.
function server(routes) {
    const calls = [];
    const fetch = async (url, init) => {
        calls.push(String(url));
        const path = String(url).split('?')[0];
        const route = routes[String(url)] || routes[path];
        if (!route) {
            throw new TypeError('Failed to fetch');
        }
        return route(init, String(url));
    };
    return { fetch, calls };
}

// An EventSource the test drives: open ones are listed, each with its URL and whether it was closed.
function eventSources() {
    const opened = [];
    class FakeSource {
        constructor(url) { this.url = url; this.closed = false; this.listeners = {}; opened.push(this); }
        addEventListener(type, fn) { this.listeners[type] = fn; }
        close() { this.closed = true; }
        emit(type, data) { (type === 'message' ? this.onmessage : this.listeners[type])({ data }); }
        opened() { this.onopen({}); }
        fail() { this.onerror({}); }
    }
    return { FakeSource, opened };
}

// A DotNetObjectReference stand-in: records every invokeMethodAsync.
function dotnet() {
    const calls = [];
    return { calls, ref: { invokeMethodAsync: (name, ...args) => { calls.push([name, ...args]); return Promise.resolve(); } } };
}

const commentsAt = shaOf => ({ v: 1, wallId: W1, cursor: '1.3.0', docs: [
    { kind: 'wall', sha: sha('1a'), bytes: 10, cursor: '1.2.3' },
    { kind: 'comments', sha: shaOf, bytes: 10, cursor: '1.3.0' }] });

test('index(): a new index writes the row as the Server pages do, and the replaced comments document leaves the device at once', async () => {
    const { fetch, calls } = server({ [`/api/walls/${W1}/docs`]: () => json(commentsAt(sha('9c')), 200, { etag: '"d2"' }) });
    const { client, ledger, caches } = await device(USER_A, { sync: true, fetch });
    await ledger.putAsset({ partition: USER_A, key: sha('1c'), sha: sha('1c'), width: null, cache: null, bytes: 1, cachedAt: 1, verifiedAt: 1 });

    const answer = plain(await client.sync.index(USER_A, W1, null));

    same(answer, { status: 200, outcome: 'updated', reason: null, docs: { wall: { sha: sha('1a'), cursor: '1.2.3' }, comments: { sha: sha('9c'), cursor: '1.3.0' } } });
    assert.equal((await ledger.getWall(USER_A, W1)).dataEtag, '"d2"');
    assert.equal(caches.stores.get(`bw-a1-${USER_A}-3d`).has(`${ORIGIN}/__bw/${sha('1c')}`), false, 'the old comments (a deleted comment, an erased name) are gone');
    assert.deepEqual(calls, [`/api/walls/${W1}/docs`]);
});

test('index(): 304 answers the stored row, a share link sends its token, and no network is status 0', async () => {
    const { fetch, calls } = server({ [`/api/walls/${W1}/docs`]: () => new Response(null, { status: 304 }) });
    const { client, ledger } = await device('anon', { sync: true, fetch, path: `/v/walls/${W1}/shared/${TOKEN}` });
    await ledger.updateWall('anon', W1, row => Object.assign(row, { dataEtag: '"d1"' }));

    const answer = plain(await client.sync.index('anon', W1, TOKEN));
    assert.equal(answer.outcome, 'unchanged');
    assert.equal(answer.docs.comments.sha, sha('1c'));
    assert.deepEqual(calls, [`/api/walls/${W1}/docs?token=${TOKEN}`]);

    const offline = await device(USER_A, { sync: true, fetch: server({}).fetch });
    same(await offline.client.sync.index(USER_A, W1, null), { status: 0, outcome: 'offline', reason: null, docs: null });
});

test('index(): a refusal is only reported, never acted on here: the loop hands it to refused()', async () => {
    for (const [status, header] of [[403, null], [401, 'forced'], [410, null]]) {
        const headers = header ? { 'X-Blocwerk-SignOut': header } : {};
        const { fetch } = server({ [`/api/walls/${W1}/docs`]: () => json({ error: 'x' }, status, headers) });
        const { client, ledger } = await device(USER_A, { sync: true, fetch });

        const answer = plain(await client.sync.index(USER_A, W1, null));

        assert.equal(answer.status, status);
        assert.equal(answer.reason, header);
        assert.equal((await ledger.wallsOf(USER_A)).length, 2, `${status}: nothing dropped by the index itself`);
        assert.equal((await ledger.getMeta()).activePartition, USER_A);
    }
});

test('index(): another active label answers switched and fetches nothing', async () => {
    const { fetch, calls } = server({});
    const { client, ledger } = await device(USER_A, { sync: true, fetch });
    await ledger.setMeta({ activePartition: 'anon' });

    assert.equal((await client.sync.index(USER_A, W1, null)).outcome, 'switched');
    assert.deepEqual(calls, []);
});

test('doc() and mine(): the document text with the share token; the marks into the ledger for u- only', async () => {
    const marks = { v: 1, wallId: W1, partition: USER_A, cursor: '1.4.0', ticks: [], favourites: [W1] };
    const { fetch, calls } = server({
        [`/api/walls/${W1}/d/comments/${sha('9c')}`]: () => new Response('{"v":1}', { status: 200 }),
        [`/api/me/walls/${W1}/mine`]: () => json(marks),
    });
    const { client, ledger } = await device(USER_A, { sync: true, fetch });

    assert.equal(await client.sync.doc(W1, 'comments', sha('9c'), 'a b'), '{"v":1}');
    assert.equal(calls[0], `/api/walls/${W1}/d/comments/${sha('9c')}?token=a+b`);
    const mine = plain(await client.sync.mine(USER_A, W1));
    assert.equal(mine.status, 200);
    same(JSON.parse(mine.body), marks);
    assert.equal((await ledger.mineOf(USER_A)).find(m => m.wallId === W1).cursor, '1.4.0');

    const anon = await device('anon', { sync: true, fetch, path: `/v/walls/${W1}/shared/${TOKEN}` });
    same(await anon.client.sync.mine('anon', W1), { status: 0, reason: null, body: null });
    assert.equal(calls.filter(c => c.includes('/mine')).length, 1, 'a share link never fetches marks');
});

test('changes() / meChanges(): the cursor and token in the query, Retry-After on a 429, the reason from the header or the feed body', async () => {
    const { fetch, calls } = server({
        [`/api/walls/${W1}/changes`]: () => new Response(null, { status: 429, headers: { 'Retry-After': '30' } }),
        '/api/me/changes': () => json({ error: 'unauthenticated', reason: 'expired' }, 401),
    });
    const { client } = await device(USER_A, { sync: true, fetch });

    same(await client.sync.changes(W1, '1.2.3', TOKEN), { status: 429, reason: null, retryAfter: 30, body: null });
    same(await client.sync.meChanges(null), { status: 401, reason: 'expired', retryAfter: null, body: null, partition: null });
    assert.deepEqual(calls, [`/api/walls/${W1}/changes?after=1.2.3&token=${TOKEN}`, '/api/me/changes']);
    const offline = (await device(USER_A, { sync: true, fetch: server({}).fetch })).client;
    same(await offline.sync.changes(W1, null, null), { status: 0, reason: null, retryAfter: null, body: null });
});

test('open(): forwards messages and resyncs; a bye or an error closes the source first, and a closed stream says nothing more', async () => {
    const { FakeSource, opened } = eventSources();
    const { client } = await device(USER_A, { sync: true, EventSource: FakeSource });
    const { calls, ref } = dotnet();

    client.sync.open(W1, '1.2.3', null, ref);
    const first = opened[0];
    assert.equal(first.url, `/api/walls/${W1}/feed/stream?after=1.2.3`);
    first.opened();
    first.emit('message', '{"kind":"comment.added"}');
    first.emit('resync', '{"next":"1.9.0"}');
    first.emit('bye', '{"reason":"lifetime"}');
    first.emit('message', '{"late":true}');
    first.fail();

    assert.equal(first.closed, true);
    same(calls, [['OnMessage', 'open', null], ['OnMessage', 'message', '{"kind":"comment.added"}'], ['OnMessage', 'resync', '{"next":"1.9.0"}'], ['OnMessage', 'bye', '{"reason":"lifetime"}']]);

    const id = client.sync.open(W1, null, TOKEN, ref);
    assert.equal(opened[1].url, `/api/walls/${W1}/feed/stream?token=${TOKEN}`);
    opened[1].fail();
    opened[1].fail();
    assert.equal(opened[1].closed, true);
    assert.deepEqual(calls.at(-1), ['OnMessage', 'error', null]);
    assert.equal(calls.filter(c => c[1] === 'error').length, 1, 'one error per stream: the loop probes once');

    client.sync.open(W1, null, null, ref);
    client.sync.close(id + 1);
    assert.equal(opened[2].closed, true);
});

test('refused(): an expired 401 locks and a forced one wipes, and either reaches the Server tabs of the partition', async () => {
    for (const [reason, outcome] of [['expired', 'locked'], ['forced', 'wiped']]) {
        const bus = new Set();
        const storage = new Map();
        const serverTab = memberPage({ bus, storage, swReply: 'none' });
        await serverTab.boot();
        serverTab.win.bwAssetClear.listen();
        assert.equal(serverTab.cache.state().active, USER_A);
        const client = loadClient({ sync: true, bus, storage, caches: serverTab.caches, ledger: await serverTab.ledger() });

        assert.equal(await client.sync.refused(USER_A, W1, 401, reason, false), outcome);
        await serverTab.settle();
        await tick();

        assert.equal(serverTab.cache.state().active, 'none', `${reason}: the Server tab heard it and stopped`);
    }
});

test('refused(): a marks refusal drops only the marks', async () => {
    const { client, ledger } = await device(USER_A, { sync: true });

    assert.equal(await client.sync.refused(USER_A, W1, 403, null, true), 'dropped');

    assert.equal((await ledger.wallsOf(USER_A)).length, 2);
    assert.equal((await ledger.mineOf(USER_A)).some(m => m.wallId === W1), false);
});

test('watch(): visibility and online reach the loop; the worker\'s asset-cached gives a synced label its asset row, never a kiosk\'s', async () => {
    const { client, ledger } = await device(USER_A, { sync: true });
    const { calls, ref } = dotnet();

    assert.equal(client.sync.watch(ref), true);
    client.win.document.visibilityState = 'hidden';
    client.docEvent('visibilitychange');
    client.winEvent('online');
    same(calls, [['OnVisibility', false], ['OnOnline']]);

    await client.swEvent('message', { data: { proto: 1, type: 'asset-cached', sha: sha('9c'), width: null, bytes: 42 } });
    const row = (await ledger.assetsOf(USER_A)).find(a => a.sha === sha('9c'));
    assert.equal(row.key, sha('9c'));
    assert.equal(row.bytes, 42);

    const kiosk = await device(KIOSK, { sync: true });
    kiosk.client.sync.watch(dotnet().ref);
    await kiosk.client.swEvent('message', { data: { proto: 1, type: 'asset-cached', sha: sha('9c'), width: null, bytes: 1 } });
    same(await kiosk.ledger.assetsOf(KIOSK), []);
});

test('status(): shows a bubble when the page has them, and is silent when it does not', async () => {
    const shown = [];
    const withBubbles = await device(USER_A, { sync: true, bwStatus: { show: kind => shown.push(kind) } });
    withBubbles.client.sync.status('offline');
    same(shown, ['offline']);

    const without = await device(USER_A, { sync: true });
    assert.doesNotThrow(() => without.client.sync.status('online'));
    const throwing = await device(USER_A, { sync: true, bwStatus: { show: () => { throw new Error('x'); } } });
    assert.doesNotThrow(() => throwing.client.sync.status('online'));
});

test('meChanges(): a 200 carries the label the cookie names right after it, so another user\'s rows can be told apart', async () => {
    const { fetch, calls } = server({
        '/api/me/changes': () => json({ events: [], next: '1.5.0', resync: false, more: false }),
        '/api/me/cache-partition': () => json({ partition: 'u-bbbbbbbbbbbbbbbbbbbb' }),
    });
    const { client } = await device(USER_A, { sync: true, fetch });

    const answer = plain(await client.sync.meChanges('1.4.0'));

    assert.equal(answer.status, 200);
    assert.equal(answer.partition, 'u-bbbbbbbbbbbbbbbbbbbb');
    assert.deepEqual(calls, ['/api/me/changes?after=1.4.0', '/api/me/cache-partition']);
});

test('the resume cursors: a wall\'s on its ledger row under the active label only; the poll\'s for its own label only', async () => {
    const { client, ledger } = await device(USER_A, { sync: true });

    await client.sync.saveCursor(USER_A, W1, '1.7.0');
    assert.equal(await client.sync.savedCursor(USER_A, W1), '1.7.0');
    assert.equal((await ledger.getWall(USER_A, W1)).data.wall.sha, sha('1a'), 'the row keeps its documents');
    await client.sync.saveCursor('anon', W1, '1.8.0');
    assert.equal(await client.sync.savedCursor('anon', W1), null, 'never written under another label');
    await client.sync.saveCursor(USER_A, '33333333-3333-3333-3333-333333333333', '1.9.0');
    assert.equal(await client.sync.savedCursor(USER_A, '33333333-3333-3333-3333-333333333333'), null, 'no row, no cursor');

    client.sync.saveMeCursor(USER_A, '1.6.0');
    assert.equal(client.sync.savedMeCursor(USER_A), '1.6.0');
    assert.equal(client.sync.savedMeCursor('u-bbbbbbbbbbbbbbbbbbbb'), null);
});

test('watch(): a second watch adds no second pair of listeners', async () => {
    const { client } = await device(USER_A, { sync: true });
    const { calls, ref } = dotnet();

    client.sync.watch(ref);
    client.sync.watch(ref);
    client.winEvent('online');

    same(calls, [['OnOnline']]);
});

test('the own-rows cursor goes with its label: a wipe, a lock, Clear and a sweep that keeps another label each remove it', async () => {
    const cases = [
        ['wiped', d => d.client.store.refused(USER_A, W1, 401, 'forced')],
        ['locked', d => d.client.store.refused(USER_A, W1, 401, 'expired')],
        ['cleared', d => d.client.store.clear()],
        ['swept', d => d.client.win.bwAssetWipe.wipe({ caches: d.caches, ledger: d.ledger, origin: ORIGIN }, ['u-bbbbbbbbbbbbbbbbbbbb'])],
    ];
    for (const [name, act] of cases) {
        const d = await device(USER_A, { sync: true });
        d.client.sync.saveMeCursor(USER_A, '1.6.0');
        await act(d);
        assert.equal(d.client.sync.savedMeCursor(USER_A), null, name);
        assert.equal(d.client.storage.has('bw-me-cursor'), false, name);
    }

    const kept = await device(USER_A, { sync: true });
    kept.client.sync.saveMeCursor(USER_A, '1.6.0');
    await kept.client.win.bwAssetWipe.wipe({ caches: kept.caches, ledger: kept.ledger, origin: ORIGIN }, [USER_A]);
    assert.equal(kept.client.sync.savedMeCursor(USER_A), '1.6.0', 'a sweep that keeps the label keeps its cursor');
});
