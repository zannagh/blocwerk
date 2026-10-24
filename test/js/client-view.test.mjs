// The offline client's view facts (Phase 3 PR 3.11, client-view.js): the photo width it asks for is exactly the rung
// the Server pages cached for the same screen (asset-plan.js's fitWidth), so the worker answers it from the device;
// the time offset; and the stored theme applied at load. Run by `node --test test/js/`.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';

const ROOT = new URL('../../src/', import.meta.url);
const read = path => readFileSync(new URL(path, ROOT), 'utf8');

function load(name, path, win) {
    const context = vm.createContext({ window: win, Date, Math });
    vm.runInContext(read(path), context, { filename: name });
    return win;
}

function windowOf({ innerWidth = 390, devicePixelRatio = 3, theme = null, storage = true } = {}) {
    const attributes = new Map();
    return {
        innerWidth,
        devicePixelRatio,
        document: { documentElement: { setAttribute: (k, v) => attributes.set(k, v) } },
        localStorage: storage
            ? { getItem: key => (key === 'blocwerk-theme' ? theme : null) }
            : { getItem: () => { throw new Error('SecurityError'); } },
        attributes
    };
}

test('the width it asks for is the rung the Server pages cached, for every screen', () => {
    const plan = load('asset-plan.js', 'Blocwerk.Web/wwwroot/js/asset-plan.js', windowOf()).bwAssetPlan;
    const view = load('client-view.js', 'Blocwerk.Client/wwwroot/js/client-view.js', windowOf()).bwClientView;

    for (const width of [0, 320, 360, 390, 414, 600, 768, 834, 1024, 1280, 1440, 1920, 2560, 3840]) {
        for (const dpr of [undefined, 1, 1.5, 2, 2.625, 3, 4]) {
            assert.equal(view.fitWidthOf(width, dpr), plan.fitWidth(width, dpr), `${width} @ ${dpr}`);
        }
    }
});

test('fitWidth reads the window, and tzOffset is local time minus UTC in minutes', () => {
    const view = load('client-view.js', 'Blocwerk.Client/wwwroot/js/client-view.js', windowOf({ innerWidth: 390, devicePixelRatio: 3 })).bwClientView;

    assert.equal(view.fitWidth(), 1280);
    assert.equal(view.tzOffset(), -new Date().getTimezoneOffset());
});

test('the Server pages\' stored theme is applied at load, and no storage leaves the system scheme', () => {
    assert.equal(load('client-view.js', 'Blocwerk.Client/wwwroot/js/client-view.js', windowOf({ theme: 'dark' })).attributes.get('data-theme'), 'dark');
    assert.equal(load('client-view.js', 'Blocwerk.Client/wwwroot/js/client-view.js', windowOf({ theme: 'sepia' })).attributes.size, 0);
    assert.equal(load('client-view.js', 'Blocwerk.Client/wwwroot/js/client-view.js', windowOf({ storage: false })).attributes.size, 0);
});
