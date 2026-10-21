// The service worker's activate purge and message protocol (service-worker.js): run by `node --test test/js/`.
// The real worker file runs in a vm sandbox with a fake `self` and `caches`, and the tests dispatch events to the
// listeners it registers.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import vm from 'node:vm';

const source = readFileSync(
    fileURLToPath(new URL('../../src/Blocwerk.Web/wwwroot/js/service-worker.js', import.meta.url)), 'utf8');

function loadWorker(cacheNames) {
    const stored = new Set(cacheNames);
    const listeners = {};
    const self = {
        location: { origin: 'https://blocwerk.test' },
        addEventListener: (type, fn) => { listeners[type] = fn; },
        clients: { claim: async () => { self.claimed = true; } },
        skipWaiting: async () => {},
        claimed: false,
    };
    const caches = {
        keys: async () => [...stored],
        delete: async name => stored.delete(name),
    };
    vm.runInNewContext(source, { self, caches, URL, Promise, console });
    return { listeners, stored, self };
}

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

test('activate keeps the asset caches and deletes only old shells, the retired schema and probe scratch', async () => {
    const worker = loadWorker([
        'blocwerk-shell-v2', 'blocwerk-shell-v3',
        'bw-a0-user1-img', 'bw-a1-user1-img', 'bw-a1-user1-3d', 'bw-a1-anon-img',
        'bw-storage-probe', 'bw-storage-probe-ladder', 'something-else',
    ]);
    await activate(worker);
    assert.deepEqual([...worker.stored].sort(), [
        'blocwerk-shell-v3', 'bw-a1-anon-img', 'bw-a1-user1-3d', 'bw-a1-user1-img', 'bw-storage-probe', 'something-else',
    ]);
    assert.equal(worker.self.claimed, true);
});

test('sw-info under proto 1 is answered on the passed port, or else to the sending client', () => {
    const worker = loadWorker([]);
    const expected = { proto: 1, type: 'sw-info', shell: 'blocwerk-shell-v3' };
    assert.deepEqual(send(worker, { proto: 1, type: 'sw-info' }, true).map(m => ({ ...m })), [expected]);
    assert.deepEqual(send(worker, { proto: 1, type: 'sw-info' }, false).map(m => ({ ...m })), [expected]);
});

test('messages with an unknown proto or type, or no object at all, are ignored without throwing', () => {
    const worker = loadWorker([]);
    for (const data of [{ proto: 2, type: 'sw-info' }, { type: 'sw-info' }, { proto: 1, type: 'asset-cached' },
        { proto: '1', type: 'sw-info' }, null, undefined, 'sw-info', 42]) {
        assert.deepEqual(send(worker, data, true), [], JSON.stringify(data));
    }
});
