// The offline queue's rejected actions kept over reloads (offline-rejected.js): per owner, never shown to anybody
// else, cleared by Dismiss and by an identity change. Run by `node --test test/js/`.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';

const source = readFileSync(new URL('../../src/Blocwerk.Web/wwwroot/js/offline-rejected.js', import.meta.url), 'utf8');

/** A page of one browser: `storage` is its localStorage (shared between pages given the same Map), `who` its body. */
function page(storage, who, opts = {}) {
    const listeners = {};
    const win = {
        document: { body: { dataset: { bwUser: who.user || undefined, bwKiosk: who.kiosk || undefined } } },
        localStorage: opts.blocked
            ? { getItem() { throw new Error('blocked'); }, setItem() { throw new Error('blocked'); }, removeItem() { throw new Error('blocked'); } }
            : {
                getItem: k => (storage.has(k) ? storage.get(k) : null),
                setItem: (k, v) => { if (opts.quota) { throw new Error('QuotaExceededError'); } storage.set(k, String(v)); },
                removeItem: k => { if (opts.quota) { throw new Error('QuotaExceededError'); } storage.delete(k); }
            },
        addEventListener: (t, fn) => { (listeners[t] = listeners[t] || []).push(fn); },
    };
    win.window = win;
    vm.runInNewContext(source, Object.assign(win, { JSON, Date, Array }));
    return { store: win.blocwerkOfflineRejected, fire: t => (listeners[t] || []).forEach(fn => fn()) };
}

test('a rejected action survives a reload for its owner, and only for its owner', () => {
    const storage = new Map();
    page(storage, { user: 'alice' }).store.add({ kind: 'attempt' }, 'Boulder was deleted');
    assert.deepEqual(page(storage, { user: 'alice' }).store.current().map(i => [i.kind, i.message]), [['attempt', 'Boulder was deleted']]);
    assert.deepEqual(page(storage, { user: 'bob' }).store.current(), [], 'another account on this browser');
    assert.deepEqual(page(storage, { user: '' }).store.current(), [], 'signed out');
});

test('on a kiosk, a member\'s conflict is not the next member\'s, and the entry\'s stamp decides the owner', () => {
    const storage = new Map();
    const tablet = page(storage, { user: 'cora', kiosk: 'k1' });
    tablet.store.add({ kind: 'rating', queuedForUserId: 'cora' }, 'Gone');
    assert.equal(page(storage, { user: 'dave', kiosk: 'k1' }).store.current().length, 0);
    assert.equal(page(storage, { user: 'cora', kiosk: 'k1' }).store.current().length, 1);
    assert.equal(page(storage, { user: 'cora' }).store.current().length, 0, 'the same person off the kiosk is another identity');

    // Dave's page drains an entry Cora queued: it is still Cora's.
    page(storage, { user: 'dave', kiosk: 'k1' }).store.add({ kind: 'comment', queuedForUserId: 'cora' }, 'Too long');
    assert.equal(page(storage, { user: 'cora', kiosk: 'k1' }).store.current().length, 2);
});

test('Dismiss clears the owner\'s items only; an identity change clears them all', () => {
    const storage = new Map();
    page(storage, { user: 'alice' }).store.add({ kind: 'attempt' }, 'a');
    page(storage, { user: 'bob' }).store.add({ kind: 'attempt' }, 'b');
    page(storage, { user: 'alice' }).store.clearCurrent();
    assert.equal(page(storage, { user: 'alice' }).store.current().length, 0);
    assert.equal(page(storage, { user: 'bob' }).store.current().length, 1);

    page(storage, { user: 'bob' }).fire('bw:identity-changed');
    assert.equal(page(storage, { user: 'bob' }).store.current().length, 0);
    assert.equal(storage.size, 0);
});

test('without storage the items live as long as the page', () => {
    const p = page(new Map(), { user: 'alice' }, { blocked: true });
    p.store.add({ kind: 'attempt' }, 'x');
    assert.equal(p.store.current().length, 1);
    p.store.clearCurrent();
    assert.equal(p.store.current().length, 0);
});

test('a write that fails (quota) while reads work loses nothing: the page keeps its own list until a write succeeds', () => {
    const storage = new Map();
    page(storage, { user: 'alice' }).store.add({ kind: 'attempt' }, 'kept from before');
    const p = page(storage, { user: 'alice' }, { quota: true });
    p.store.add({ kind: 'comment' }, 'just rejected');
    assert.deepEqual(p.store.current().map(i => i.message), ['kept from before', 'just rejected']);
    p.store.clearCurrent();
    assert.deepEqual(p.store.current(), []);
});
