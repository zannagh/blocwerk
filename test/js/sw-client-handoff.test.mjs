// The worker's hand-off of a failed Server-page navigation to the offline client (service-worker.js, Phase 3 PR 3.9):
// a network error, a 502/503/504 or the 4 s timer redirect to /v/ only for a complete, cached, covered version on a
// u-/anon/k- partition with no lock and no announced deploy; everything else keeps today's answer.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { loadWorker, dispatchFetch, response, ORIGIN } from './fixtures/sw-sandbox.mjs';
import { V1, V2, ALICE, KIOSK, ROUTES, STATE, abs, shellHtml, dotnetJs, deploy, workerWith, restart, precache, info, tick, navigate, fetched, completeWorker } from './fixtures/sw-client.mjs';

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

// The routes the client declares (ClientRoutes.Declared, PR 3.11).
const DECLARED = 'walls walls/{WallId:guid} walls/{WallId:guid}/shared/{ShareToken} walls/{WallId:guid}/boulders/{BoulderId:guid} walls/{WallId:guid}/boulders/{BoulderId:guid}/shared/{ShareToken}';
const GUID_A = '3f2a0000-0000-4000-8000-0000000000a1';
const GUID_B = 'B2000000-0000-4000-8000-0000000000b2';

test('a :guid parameter covers a GUID only, so the edit pages beside the declared views are never handed over', async () => {
    const worker = workerWith();
    deploy(worker.routes, V1, { shell: shellHtml(V1, DECLARED) });
    await precache(worker);
    for (const covered of ['/walls', `/walls/${GUID_A}`, `/walls/${GUID_A}/boulders/${GUID_B}`, `/walls/${GUID_A}/shared/tok`, `/walls/${GUID_A}/boulders/${GUID_B}/shared/tok`]) {
        assert.equal((await navigate(worker, covered)).status, 302, covered);
    }
    for (const edit of ['/walls/create', `/walls/${GUID_A}/boulders/create`, `/walls/${GUID_A}/boulders/create/shared/tok`, '/walls/not-a-guid', `/walls/${GUID_A}/update`, `/walls/${GUID_A}/boulders/${GUID_B}/revise`]) {
        assert.equal(await (await navigate(worker, edit)).text(), 'offline page', edit);
    }
});

test('a locked partition, no partition (a kiosk that may keep nothing too) or no complete version gets offline.html, never the client', async () => {
    const cases = {
        locked: state => state.set('/__bw/locked', response(200, ALICE)),
        none: state => state.set('/__bw/partition', response(200, 'none')),
    };
    for (const [name, change] of Object.entries(cases)) {
        const fresh = restart(await completeWorker(), change);
        assert.equal((await info(fresh)).client, V1, `${name}: a cold worker reads its record before it answers`);
        assert.equal(await (await navigate(fresh, '/walls/W1')).text(), 'offline page', name);
        assert.equal(fetched(fresh, '/v/'), 0, name);
    }
    assert.equal(await (await navigate(workerWith(), '/walls/W1')).text(), 'offline page', 'incomplete');
    const anon = await completeWorker({ partition: 'anon' });
    assert.equal((await navigate(anon, '/shared/t/x')).status, 302, 'a share viewer is handed over');
});

const fresh = (label, at) => response(200, JSON.stringify({ label, at }));

test('a kiosk tablet is handed over within 7 days of its last online fill (PR 3.15), never past a lock marker', async () => {
    const kiosk = restart(await completeWorker(), state => {
        state.set('/__bw/partition', response(200, KIOSK));
        state.set('/__bw/kiosk-fresh', fresh(KIOSK, Date.now() - 7 * 24 * 3600 * 1000 + 60000));
    });
    const served = await navigate(kiosk, '/walls/W1');
    assert.equal(served.status, 302);
    assert.equal(served.headers.get('location'), `${ORIGIN}/v/walls/W1`);

    const locked = restart(await completeWorker(), state => {
        state.set('/__bw/partition', response(200, KIOSK));
        state.set('/__bw/kiosk-fresh', fresh(KIOSK, Date.now()));
        state.set('/__bw/locked', response(200, ALICE));
    });
    assert.equal(await (await navigate(locked, '/walls/W1')).text(), 'offline page', 'any lock marker refuses');
});

test('a kiosk copy past 7 days, stamped for another label, from the future, or never stamped gets offline.html, like none', async () => {
    const cases = {
        stale: fresh(KIOSK, Date.now() - 7 * 24 * 3600 * 1000 - 60000),
        'another label': fresh('k-' + 'z'.repeat(20), Date.now()),
        future: fresh(KIOSK, Date.now() + 3600 * 1000),
        unreadable: response(200, 'not json'),
        none: null,
    };
    for (const [name, stamp] of Object.entries(cases)) {
        const worker = restart(await completeWorker(), state => {
            state.set('/__bw/partition', response(200, KIOSK));
            if (stamp) {
                state.set('/__bw/kiosk-fresh', stamp);
            }
        });
        assert.equal(await (await navigate(worker, '/walls/W1')).text(), 'offline page', name);
    }
});

test('only a 502, 503 or 504 is handed over; a 500 or any other status is the server answer', async () => {
    const worker = await completeWorker();
    for (const status of [502, 503, 504]) {
        worker.routes['/walls/W1'] = () => response(status, 'proxy');
        assert.equal((await navigate(worker, '/walls/W1')).status, 302, String(status));
    }
    for (const status of [500, 501, 505, 507, 404]) {
        worker.routes['/walls/W1'] = () => response(status, 'the app said so');
        const served = await navigate(worker, '/walls/W1');
        assert.equal(served.status, status, String(status));
        assert.equal(await served.text(), 'the app said so', String(status));
    }
    worker.routes['/settings'] = () => response(503, 'Unavailable');
    assert.equal((await navigate(worker, '/settings')).status, 503, 'an uncovered route keeps the 503');
});

const post = (worker, type) => {
    let pending;
    worker.listeners.message({ data: { proto: 1, type }, ports: [], waitUntil: p => { pending = p; } });
    return pending;
};

test('an announced deploy holds the hand-off, a restarted worker keeps holding it, and deploy-done ends it', async () => {
    const worker = await completeWorker();
    await post(worker, 'deploy-pending');
    assert.equal(await (await navigate(worker, '/walls/W1')).text(), 'offline page', 'server down during a deploy');
    worker.routes['/walls/W1'] = () => response(502, 'Bad Gateway');
    assert.equal((await navigate(worker, '/walls/W1')).status, 502, "a deploy's 502 is today's answer");
    delete worker.routes['/walls/W1'];

    const restarted = restart(worker, () => undefined);
    assert.equal(await (await navigate(restarted, '/walls/W1')).text(), 'offline page', 'persisted across a worker restart');

    await post(restarted, 'deploy-done');
    assert.equal((await navigate(restarted, '/walls/W1')).status, 302, 'the new instance ends the hold');
    assert.equal(restarted.stored.get(STATE).has('/__bw/deploy'), false);
});

test('an unannounced outage hands over, and a deploy hold ends by itself: expired, unreadable or too far away', async () => {
    assert.equal((await navigate(await completeWorker(), '/walls/W1')).status, 302, 'no announcement');
    const now = Date.now();
    for (const [name, until] of Object.entries({
        expired: String(now - 1), 'too far (a clock that jumped back)': String(now + 10 * 60 * 1000), unreadable: 'soon',
    })) {
        const worker = await completeWorker();
        worker.stored.get(STATE).set('/__bw/deploy', response(200, until));
        assert.equal((await navigate(worker, '/walls/W1')).status, 302, name);
        assert.equal(worker.stored.get(STATE).has('/__bw/deploy'), false, `${name}: the stale hold is removed`);
    }
    const held = await completeWorker();
    held.stored.get(STATE).set('/__bw/deploy', response(200, String(now + 2 * 60 * 1000)));
    assert.equal(await (await navigate(held, '/walls/W1')).text(), 'offline page', 'still inside the 3 min window');
});

test('a deploy hold set now ends 3 minutes later', async () => {
    const worker = await completeWorker();
    await post(worker, 'deploy-pending');
    const until = Number(await worker.stored.get(STATE).get('/__bw/deploy').clone().text());
    assert.ok(Math.abs(until - (Date.now() + 3 * 60 * 1000)) < 1000);
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
    // A /v/ page's shell-cached file (not precached) still answers when the network fails.
    worker.stored.get('blocwerk-shell-v5').set(abs('/js/theme.t1.js'), response(200, 'theme'));
    assert.equal(await (await dispatchFetch(worker, '/js/theme.t1.js', client).settle()).text(), 'theme');
    worker.routes['/v/_framework/missing.wasm'] = () => response(404, 'no');
    assert.equal((await dispatchFetch(worker, '/v/_framework/missing.wasm').settle()).status, 404, 'a miss goes to the network');
});
