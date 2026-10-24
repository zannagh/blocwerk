// The offline queue (offline-queue.js): run by `node --test test/js/`.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';

const source = readFileSync(new URL('../../src/Blocwerk.Web/wwwroot/js/offline-queue.js', import.meta.url), 'utf8');

/** Loads the queue against an in-memory store and a transport whose first reads can be held open. */
function load() {
    const rows = new Map();
    let nextId = 1;
    const sent = [];
    const gates = [];
    const db = {
        available: () => true,
        count: () => Promise.resolve(rows.size),
        // A read can be held open, the way a slow IndexedDB is while the page is still loading.
        all: () => new Promise(resolve => {
            // The snapshot is taken when the read starts, as IndexedDB's is: an entry added meanwhile is not in it.
            const snapshot = [...rows.values()].map(r => ({ ...r }));
            const answer = () => resolve(snapshot);
            gates.length > 0 ? gates.shift().push(answer) : answer();
        }),
        add: entry => { const id = nextId++; rows.set(id, { ...entry, id }); return Promise.resolve(id); },
        put: entry => { rows.set(entry.id, { ...entry }); return Promise.resolve(); },
        remove: id => { rows.delete(id); return Promise.resolve(); },
    };
    const transport = {
        supports: () => true,
        send: entry => { sent.push(entry.payload.text); return Promise.resolve({ ok: true }); },
        classify: () => 'sent',
        readError: () => Promise.resolve(''),
        backoffFor: () => 1000,
        checkSession: () => Promise.resolve(true),
    };
    const window = { blocwerkOfflineDb: db, blocwerkOfflineTransport: transport, addEventListener: () => {} };
    const document = { body: { dataset: { bwUser: 'u1' } }, addEventListener: () => {}, dispatchEvent: () => {}, visibilityState: 'visible' };
    vm.runInNewContext(source, {
        window, document, navigator: { onLine: true }, crypto: { randomUUID: () => `id-${nextId}` },
        CustomEvent: class { constructor(type, init) { this.type = type; this.detail = init && init.detail; } },
        setInterval: () => 0, setTimeout, clearTimeout, console, Date, Promise,
    });

    /** Holds the next db.all() until the returned function is called. */
    function holdNextRead() {
        const waiting = [];
        gates.push(waiting);
        return () => waiting.forEach(answer => answer());
    }

    return { queue: window.blocwerkOfflineQueue, sent, rows, holdNextRead };
}

const settle = () => new Promise(resolve => setTimeout(resolve, 20));

test('an action queued while a flush is running is sent by a second pass, not left for the next trigger', async () => {
    const { queue, sent, rows, holdNextRead } = load();
    await settle();
    const release = holdNextRead();
    const running = queue.flush();

    await queue.enqueue('comment', { boulderId: 'b', text: 'right after load' });
    release();
    await running;
    await settle();

    assert.deepEqual(sent, ['right after load']);
    assert.equal(rows.size, 0);
});

test('without anything new, a flush runs once', async () => {
    const { queue, sent } = load();
    await queue.enqueue('comment', { boulderId: 'b', text: 'one' });
    await settle();
    await queue.flush();
    await settle();

    assert.deepEqual(sent, ['one']);
});
