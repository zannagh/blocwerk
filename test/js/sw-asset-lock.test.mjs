// The worker's half of the 401 lock (Phase 3 PR 3.5, sw-asset-lock.js): a partition the page locked (its label in
// `bw-sw-state` at /__bw/locked) is never served and never stored, online or offline, and serving resumes once the page
// removes the marker and posts sw-refresh-partition. Run by `node --test test/js/`.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { loadWorker, dispatchFetch, response } from './fixtures/sw-sandbox.mjs';

const SHA = 'ab'.repeat(32);
const ALICE = 'u-' + 'a'.repeat(20);
const PARTITION = '/api/me/cache-partition';
const LOCK = '/__bw/locked';
const asset = `/api/walls/w1/a/${SHA}/1280`;
const KEY = `https://blocwerk.test/__bw/${SHA}/1280`;

// Alice's cached image, the worker's remembered label, and (when `locked`) the page's lock marker.
function stateWith(locked) {
    const state = { '/__bw/partition': response(200, ALICE) };
    if (locked) {
        state[LOCK] = response(200, locked);
    }
    return { [`bw-a1-${ALICE}-img`]: { [KEY]: response(200, 'cached-bytes') }, 'bw-sw-state': state };
}

function workerFor({ online = true, locked = ALICE } = {}) {
    // Offline, every fetch fails (no route answers).
    const routes = online ? {
        [asset]: () => response(200, 'network-bytes', { 'content-type': 'image/jpeg', etag: `"${SHA}-1280"` }),
        [PARTITION]: () => response(200, JSON.stringify({ partition: ALICE }), { 'content-type': 'application/json' }),
    } : {};
    return loadWorker({ routes, caches: stateWith(locked) });
}

function refresh(worker) {
    let pending;
    worker.listeners.message({ data: { proto: 1, type: 'sw-refresh-partition' }, ports: [{ postMessage: () => undefined }], source: { id: 'tab-1' }, waitUntil: p => { pending = p; } });
    return pending;
}

test('online, a locked label is neither served from its cache nor stored into it', async () => {
    const worker = workerFor();
    const served = await dispatchFetch(worker, asset).settle();
    assert.equal(await served.text(), 'network-bytes', 'the cached copy is not served');
    assert.equal(await (await worker.caches.match(KEY, { cacheName: `bw-a1-${ALICE}-img` })).text(), 'cached-bytes', 'and the network copy is not stored');
});

test('offline, the last known label falls back but a locked one is never served (the marker is read fresh)', async () => {
    const worker = workerFor({ online: false });
    await assert.rejects(dispatchFetch(worker, asset).settle(), /Failed to fetch/, 'network-only: offline, nothing to show');

    const unlocked = workerFor({ online: false, locked: null });
    assert.equal(await (await dispatchFetch(unlocked, asset).settle()).text(), 'cached-bytes', 'without the marker it serves (2.x behaviour)');
});

test('a marker written after the worker read it is honoured offline at once, and online after sw-refresh-partition', async () => {
    const offline = workerFor({ online: false, locked: null });
    assert.equal(await (await dispatchFetch(offline, asset, { clientId: 'a' }).settle()).text(), 'cached-bytes');
    await (await offline.caches.open('bw-sw-state')).put(LOCK, response(200, ALICE));
    await assert.rejects(dispatchFetch(offline, asset, { clientId: 'b' }).settle(), /Failed to fetch/);

    const online = workerFor({ locked: null });
    assert.equal(await (await dispatchFetch(online, asset).settle()).text(), 'cached-bytes');
    await (await online.caches.open('bw-sw-state')).put(LOCK, response(200, ALICE));
    await refresh(online);
    assert.equal(await (await dispatchFetch(online, asset).settle()).text(), 'network-bytes');
});

test('unlocking (the page deletes the marker and posts sw-refresh-partition) serves the partition again', async () => {
    const worker = workerFor();
    assert.equal(await (await dispatchFetch(worker, asset).settle()).text(), 'network-bytes');
    worker.stored.get('bw-sw-state').delete(LOCK);
    await refresh(worker);
    assert.equal(await (await dispatchFetch(worker, asset).settle()).text(), 'cached-bytes');
});

test('a marker naming another label locks nothing of this one', async () => {
    const worker = workerFor({ locked: 'u-' + 'b'.repeat(20) });
    assert.equal(await (await dispatchFetch(worker, asset).settle()).text(), 'cached-bytes');
});
