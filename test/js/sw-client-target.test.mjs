// The worker's half of the hand-off between the Server pages and the offline client (service-worker.js, Phase 3 PR 3.14):
// `client-target` answers where "Continue offline" goes for a page's path, and a navigation from a /v/ page (the
// client's hand-back) gets no 4 s timer, so a slow Server page is waited for instead of bouncing back into the client.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { dispatchFetch, response, ORIGIN } from './fixtures/sw-sandbox.mjs';
import { V1, ALICE, KIOSK, deploy, workerWith, precache, tick, completeWorker, restart } from './fixtures/sw-client.mjs';

async function target(worker, path) {
    const sent = [];
    let pending;
    worker.listeners.message({ data: { proto: 1, type: 'client-target', path }, ports: [{ postMessage: m => sent.push(m) }], waitUntil: p => { pending = p; } });
    await pending;
    assert.equal(sent.length, 1);
    assert.equal(sent[0].type, 'client-target');
    return sent[0].target;
}

test('client-target names the /v/ twin of a covered path, query kept; anything the client cannot take is null', async () => {
    const worker = await completeWorker();
    assert.equal(await target(worker, '/walls/W1/boulders/B2?view=x'), '/v/walls/W1/boulders/B2?view=x');
    for (const uncovered of ['/', '/walls', '/walls/W1/edit', '/settings', '/v/walls/W1', '/account/login', '//evil.example/walls/W1', 'walls/W1', null]) {
        assert.equal(await target(worker, uncovered), null, String(uncovered));
    }
});

test('client-target is null for a locked partition, a stale kiosk or an incomplete client, and ignores an announced deploy', async () => {
    const locked = restart(await completeWorker(), state => state.set('/__bw/locked', response(200, ALICE)));
    assert.equal(await target(locked, '/walls/W1'), null, 'locked');
    const kiosk = restart(await completeWorker(), state => state.set('/__bw/partition', response(200, KIOSK)));
    assert.equal(await target(kiosk, '/walls/W1'), null, 'a kiosk without a fresh stamp');
    // A device turned kiosk (PR 3.15): the same rule as the automatic hand-off.
    const tablet = restart(await completeWorker(), state => {
        state.set('/__bw/partition', response(200, KIOSK));
        state.set('/__bw/kiosk-fresh', response(200, JSON.stringify({ label: KIOSK, at: Date.now() })));
    });
    assert.equal(await target(tablet, '/walls/W1'), '/v/walls/W1', 'a fresh kiosk');
    assert.equal(await target(workerWith(), '/walls/W1'), null, 'incomplete');

    // The user asked for it: the deploy hold stops only the automatic hand-off (offline-status.js hides the button then).
    const held = await completeWorker();
    held.listeners.message({ data: { proto: 1, type: 'deploy-pending' }, waitUntil: () => undefined });
    await tick();
    assert.equal(await target(held, '/walls/W1'), '/v/walls/W1');
});

test('a navigation from a /v/ page gets no 4 s timer, but a network error or a 502 still goes to the client', async () => {
    const timers = [];
    const worker = workerWith({ timers });
    deploy(worker.routes, V1);
    assert.equal((await precache(worker)).complete, true);
    const fromClient = { mode: 'navigate', referrer: `${ORIGIN}/v/walls/W1` };

    let release;
    worker.routes['/walls/W1'] = () => new Promise(resolve => { release = () => resolve(response(200, 'slow server page')); });
    const before = timers.filter(t => t.ms === 4000).length;
    const slow = dispatchFetch(worker, '/walls/W1', fromClient);
    await tick();
    assert.equal(timers.filter(t => t.ms === 4000).length, before, 'no hand-off timer');
    release();
    assert.equal(await (await slow.settle()).text(), 'slow server page');

    worker.routes['/walls/W1'] = () => response(502, 'proxy');
    assert.equal((await dispatchFetch(worker, '/walls/W1', fromClient).settle()).headers.get('location'), `${ORIGIN}/v/walls/W1`);
    worker.routes['/walls/W1'] = () => Promise.reject(new TypeError('offline'));
    assert.equal((await dispatchFetch(worker, '/walls/W1', fromClient).settle()).headers.get('location'), `${ORIGIN}/v/walls/W1`);

    // Any other referrer keeps the timer (PR 3.9).
    worker.routes['/walls/W1'] = () => new Promise(() => undefined);
    dispatchFetch(worker, '/walls/W1', { mode: 'navigate', referrer: `${ORIGIN}/walls/W1` });
    await tick();
    assert.equal(timers.filter(t => t.ms === 4000).length, before + 1);
    dispatchFetch(worker, '/walls/W1', { mode: 'navigate', referrer: `${ORIGIN}/vx/walls` });
    await tick();
    assert.equal(timers.filter(t => t.ms === 4000).length, before + 2, 'only the /v/ path counts');
});
