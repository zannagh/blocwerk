// The offline boulder submit (offline-boulder.js): run by `node --test test/js/`.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';

const source = readFileSync(new URL('../../src/Blocwerk.Web/wwwroot/js/offline-boulder.js', import.meta.url), 'utf8');

class FakeElement {
    constructor(attributes) {
        this.attributes = attributes;
    }

    getAttribute(name) {
        return Object.hasOwn(this.attributes, name) ? this.attributes[name] : null;
    }

    hasAttribute(name) {
        return Object.hasOwn(this.attributes, name);
    }

    closest(selector) {
        const name = selector.slice(1, -1);
        return this.hasAttribute(name) ? this : null;
    }
}

/** Loads the script against a fake offline page; returns a click() driver and what it did. */
function load(snapshot) {
    const handlers = [];
    const enqueued = [];
    const flashed = [];
    const navigated = [];
    const form = new FakeElement({ 'data-bw-boulder-snapshot': JSON.stringify(snapshot) });
    const window = {
        blocwerkOfflineQueue: { enqueue: (kind, payload) => { enqueued.push({ kind, payload }); return Promise.resolve(); } },
        blocwerkConnection: { isUp: () => false },
        blocwerkStatus: { flash: message => flashed.push(message) },
        location: { assign: url => navigated.push(url) },
        dispatchEvent: () => {},
    };
    const document = {
        addEventListener: (type, handler) => handlers.push(handler),
        querySelector: selector => (selector === '#bw-boulder-form' ? form : null),
    };
    vm.runInNewContext(source, { window, document, Element: FakeElement, CustomEvent: class {}, console });

    async function click(attributes) {
        const event = { target: new FakeElement(attributes), preventDefault() {}, stopPropagation() {} };
        handlers.forEach(handler => handler(event));
        await new Promise(resolve => setImmediate(resolve));
    }

    return { click, enqueued, flashed, navigated };
}

const revise = {
    'data-bw-boulder-action': 'revise',
    'data-bw-boulder-form': '#bw-boulder-form',
    'data-bw-boulder-nav': '/walls/w/boulders/b',
    'data-bw-mode': 'offline-only',
};

test('offline revise with a selection on an unseen panel queues nothing and says why', async () => {
    const page = load({ id: 'b', holds: [] });

    await page.click({ ...revise, 'data-bw-boulder-blocked': '2 selected hold(s) on Panel 2/2 have not been looked at.' });

    assert.equal(page.enqueued.length, 0);
    assert.equal(page.navigated.length, 0);
    assert.deepEqual(page.flashed, ['2 selected hold(s) on Panel 2/2 have not been looked at.']);
});

test('offline revise without a block queues the snapshot as before', async () => {
    const page = load({ id: 'b', holds: [{ holdId: 'h', type: 0, usage: 0 }] });

    await page.click(revise);

    assert.equal(page.enqueued.length, 1);
    assert.equal(page.enqueued[0].kind, 'boulder-revise');
    assert.equal(page.enqueued[0].payload.boulderId, 'b');
    assert.deepEqual(page.navigated, ['/walls/w/boulders/b']);
    assert.equal(page.flashed.length, 0);
});
