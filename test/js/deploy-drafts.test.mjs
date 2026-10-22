// Editor state kept across a deploy's reload (deploy-drafts.js): run by `node --test test/js/`.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createRequire } from 'node:module';

const require = createRequire(import.meta.url);
const drafts = require('../../src/Blocwerk.Web/wwwroot/js/deploy-drafts.js');

function memoryStorage() {
    const map = new Map();
    return {
        get length() { return map.size; },
        key: (i) => [...map.keys()][i] ?? null,
        getItem: (k) => (map.has(k) ? map.get(k) : null),
        setItem: (k, v) => map.set(k, String(v)),
        removeItem: (k) => map.delete(k),
        map,
    };
}

function harness(who = 'u1', storage = memoryStorage()) {
    const shown = [];
    const clock = { now: 1000 };
    const id = { who };
    const store = drafts.createStore(storage, () => clock.now, (text, href, key) => shown.push(key ? [text, href, key] : [text, href]), () => id.who);
    return { storage, shown, clock, store, id };
}

test('a kept editor state is taken back exactly once, with its notice queued for the reload', () => {
    const { store, shown } = harness();
    assert.equal(store.keep('u1|boulder-revise:1', '{"name":"Crimp"}', 'Your changes were kept.', '/walls/w/boulders/1/revise'), true);
    assert.deepEqual(store.takeNotices().map((n) => [n.text, n.href, n.key]), [['Your changes were kept.', '/walls/w/boulders/1/revise', 'u1|boulder-revise:1']]);
    assert.deepEqual(store.takeNotices(), [], 'a notice is shown once');
    assert.deepEqual(store.take('u1|boulder-revise:1'), { payload: '{"name":"Crimp"}', reason: 'deploy' });
    assert.equal(store.take('u1|boulder-revise:1'), null);
    assert.deepEqual(shown, [], 'a deploy keep is announced by its queued notice, not again on take');
});

test('a copy made when the page was hidden says it was restored', () => {
    const { store, shown } = harness();
    store.keepHidden('u1|wall-create', '{"name":"Attic"}');
    assert.deepEqual(store.take('u1|wall-create'), { payload: '{"name":"Attic"}', reason: 'hidden' });
    assert.deepEqual(shown, [[drafts.RESTORED, null, 'u1|wall-create']]);
});

test('a hidden copy after a deploy keep keeps the deploy reason, so no second notice', () => {
    const { store, shown } = harness();
    store.keep('u1|k', 'a', 'kept');
    store.keepHidden('u1|k', 'b');
    assert.deepEqual(store.take('u1|k'), { payload: 'b', reason: 'deploy' });
    assert.deepEqual(shown, []);
});

test('drop forgets, and old entries expire', () => {
    const { store, clock } = harness();
    store.keep('u1|a', 'x');
    store.drop('u1|a');
    assert.equal(store.take('u1|a'), null);
    store.keep('u1|b', 'y', 'n');
    clock.now += drafts.MAX_AGE_MS + 1;
    assert.equal(store.take('u1|b'), null);
    assert.deepEqual(store.takeNotices(), []);
});

test('blocked storage degrades to nothing kept, never a throw', () => {
    const broken = { length: 0, key: () => null, getItem: () => { throw new Error('blocked'); }, setItem: () => { throw new Error('blocked'); }, removeItem: () => { throw new Error('blocked'); } };
    const store = drafts.createStore(broken, () => 0, () => {}, () => 'u1');
    store.keep('u1|a', 'x', 'n');
    assert.equal(store.take('u1|a'), null);
    assert.deepEqual(store.takeNotices(), []);
    store.checkIdentity();
    store.clearAll();
});

test('discard forgets a kept copy, and says when the editor already took it back', () => {
    const { store } = harness();
    store.keep('u1|a', 'x', 'kept');
    assert.equal(store.discard('u1|a'), false, 'not restored yet: just forgotten');
    assert.equal(store.take('u1|a'), null);

    store.keepHidden('u1|b', 'y');
    assert.equal(store.take('u1|b').payload, 'y');
    assert.equal(store.discard('u1|b'), true, 'already in the editor: the page must reload to drop it');
});

test('a copy never restores into another identity, and is never written under one', () => {
    const storage = memoryStorage();
    const alice = harness('alice', storage);
    alice.store.keep('alice|boulder-create:w', 'alice form', 'kept');
    assert.equal(alice.store.keep('bob|boulder-create:w', 'forged', 'x'), false);

    const bob = harness('bob', storage);
    assert.equal(bob.store.take('alice|boulder-create:w'), null);
    assert.deepEqual(bob.store.takeNotices(), [], "alice's notice is not shown to bob");
    assert.equal(alice.store.take('alice|boulder-create:w').payload, 'alice form');
});

test('a page load under a different identity clears every kept copy (sign-out, act-as end, account switch)', () => {
    const { store, storage, id } = harness('k1-alice');
    store.checkIdentity();
    store.keep('k1-alice|boulder-create:w', 'alice on the tablet', 'kept');
    storage.setItem('unrelated', 'stays');

    id.who = 'k1-anon';
    store.checkIdentity();
    id.who = 'k1-alice';
    assert.equal(store.take('k1-alice|boulder-create:w'), null);
    assert.equal(storage.getItem('unrelated'), 'stays');

    store.keep('k1-alice|x', 'again', 'kept');
    store.clearAll();
    assert.equal(store.take('k1-alice|x'), null);
});

test('one notice per editor however often the flush runs', () => {
    const { store } = harness();
    store.keep('u1|a', 'x', 'kept a');
    store.keep('u1|a', 'x2', 'kept a');
    store.notice('saved', '/walls/w');
    store.notice('saved', '/walls/w');
    assert.deepEqual(store.takeNotices().map((n) => n.text), ['kept a', 'saved']);
});

test('a notice can list links (the boulders a saved wall batch retired)', () => {
    const { store } = harness();
    store.notice('Saved; 2 boulders were retired', '/walls/w', null, [
        { text: 'Crimp Line (retired)', href: '/walls/w/boulders/1' },
        { text: 'Sloper (review)', href: '/walls/w/boulders/2' },
        { text: '', href: '/ignored' },
    ]);
    const [n] = store.takeNotices();
    assert.deepEqual(n.links, [
        { text: 'Crimp Line (retired)', href: '/walls/w/boulders/1' },
        { text: 'Sloper (review)', href: '/walls/w/boulders/2' },
    ]);
});

test('a stale tab of identity A writes nothing once another tab recorded B at this browser', () => {
    const storage = memoryStorage();
    const a = harness('u1', storage);
    a.store.checkIdentity();
    harness('u2', storage).store.checkIdentity();
    assert.equal(a.store.keep('u1|boulder-revise:1', 'x', 'kept'), false);
    a.store.keepHidden('u1|wall-create', 'y');
    assert.deepEqual([...storage.map.keys()].filter(k => k.startsWith('bw-kept:')), []);
});

test('kept copies older than 7 days are swept on the next page load, whoever they were for', () => {
    const storage = memoryStorage();
    const h = harness('u1', storage);
    h.store.keep('u1|old', 'x');
    h.clock.now += drafts.MAX_AGE_MS + 1;
    h.store.keep('u1|new', 'y');
    h.store.checkIdentity();
    assert.deepEqual([...storage.map.keys()].filter(k => k.startsWith('bw-kept:')), ['bw-kept:u1|new']);
});

test('an anonymous kiosk keeps only for the deploy reload: one restore within 2 minutes, never a hidden copy', () => {
    const who = 'kkey1-anon';
    const h = harness(who);
    h.store.keepHidden(`${who}|boulder-create:w`, 'hidden');
    assert.equal(h.storage.getItem(`bw-kept:${who}|boulder-create:w`), null, 'a hidden copy is never even written');
    assert.equal(h.store.take(`${who}|boulder-create:w`), null, 'so it is never kept for the next person');

    h.store.keep(`${who}|boulder-create:w`, 'deploy', 'kept');
    h.clock.now += 60 * 1000;
    assert.deepEqual(h.store.take(`${who}|boulder-create:w`), { payload: 'deploy', reason: 'deploy' });
    assert.equal(h.store.take(`${who}|boulder-create:w`), null, 'one restore');

    h.store.keep(`${who}|boulder-create:w`, 'late', 'kept');
    h.clock.now += drafts.ANON_KIOSK_RESTORE_MS + 1;
    assert.equal(h.store.take(`${who}|boulder-create:w`), null, 'past the reload window it is not restored');

    h.store.keep(`${who}|boulder-create:w`, 'swept', 'kept');
    h.clock.now += drafts.ANON_KIOSK_RESTORE_MS + 1;
    h.store.checkIdentity();
    assert.equal(h.storage.getItem(`bw-kept:${who}|boulder-create:w`), null, 'and the next page load sweeps it');

    const member = harness('kkey1-u7');
    member.store.keepHidden('kkey1-u7|boulder-create:w', 'mine');
    assert.deepEqual(member.store.take('kkey1-u7|boulder-create:w'), { payload: 'mine', reason: 'hidden' }, 'a signed-in kiosk member keeps as before');
});
