// Editor state kept across a deploy's reload (deploy-drafts.js): run by `node --test test/js/`.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createRequire } from 'node:module';

const require = createRequire(import.meta.url);
const drafts = require('../../src/Blocwerk.Web/wwwroot/js/deploy-drafts.js');

function memoryStorage() {
    const map = new Map();
    return {
        getItem: (k) => (map.has(k) ? map.get(k) : null),
        setItem: (k, v) => map.set(k, String(v)),
        removeItem: (k) => map.delete(k),
        map,
    };
}

function harness() {
    const storage = memoryStorage();
    const shown = [];
    const clock = { now: 1000 };
    const store = drafts.createStore(storage, () => clock.now, (text, href, key) => shown.push(key ? [text, href, key] : [text, href]));
    return { storage, shown, clock, store };
}

test('a kept editor state is taken back exactly once, with its notice queued for the reload', () => {
    const { store, shown } = harness();
    store.keep('boulder-revise:1', '{"name":"Crimp"}', 'Your changes were kept.', '/walls/w/boulders/1/revise');
    assert.deepEqual(store.takeNotices().map((n) => [n.text, n.href, n.key]), [['Your changes were kept.', '/walls/w/boulders/1/revise', 'boulder-revise:1']]);
    assert.deepEqual(store.takeNotices(), [], 'a notice is shown once');
    assert.equal(store.take('boulder-revise:1'), '{"name":"Crimp"}');
    assert.equal(store.take('boulder-revise:1'), null);
    assert.deepEqual(shown, [], 'a deploy keep is announced by its queued notice, not again on take');
});

test('a copy made when the page was hidden says it was restored', () => {
    const { store, shown } = harness();
    store.keepHidden('wall-create', '{"name":"Attic"}');
    assert.equal(store.take('wall-create'), '{"name":"Attic"}');
    assert.deepEqual(shown, [[drafts.RESTORED, null, 'wall-create']]);
});

test('a hidden copy after a deploy keep keeps the deploy reason, so no second notice', () => {
    const { store, shown } = harness();
    store.keep('k', 'a', 'kept');
    store.keepHidden('k', 'b');
    assert.equal(store.take('k'), 'b');
    assert.deepEqual(shown, []);
});

test('drop forgets, and old entries expire', () => {
    const { store, clock } = harness();
    store.keep('a', 'x');
    store.drop('a');
    assert.equal(store.take('a'), null);
    store.keep('b', 'y', 'n');
    clock.now += drafts.MAX_AGE_MS + 1;
    assert.equal(store.take('b'), null);
    assert.deepEqual(store.takeNotices(), []);
});

test('blocked storage degrades to nothing kept, never a throw', () => {
    const broken = { getItem: () => { throw new Error('blocked'); }, setItem: () => { throw new Error('blocked'); }, removeItem: () => { throw new Error('blocked'); } };
    const store = drafts.createStore(broken, () => 0, () => {});
    store.keep('a', 'x', 'n');
    assert.equal(store.take('a'), null);
    assert.deepEqual(store.takeNotices(), []);
});

test('discard forgets a kept copy, and says when the editor already took it back', () => {
    const { store } = harness();
    store.keep('a', 'x', 'kept');
    assert.equal(store.discard('a'), false, 'not restored yet: just forgotten');
    assert.equal(store.take('a'), null);

    store.keepHidden('b', 'y');
    assert.equal(store.take('b'), 'y');
    assert.equal(store.discard('b'), true, 'already in the editor: the page must reload to drop it');
});
