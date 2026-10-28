// The offline client's 3D mount (Phase 3 PR 3.13, client-3d-stage.js): the 3D code loads only on a mount of a ready
// view, never for a wall the device holds no 3D of; the 20 s rule (no splat in the client's first ~20 s after its first
// render: the hold covers what is left of HOLD_MS, none after it); a mount unmounted or replaced meanwhile drops its
// viewer; the failures map to their answers. A fake clock and fake modules: no real 20 s wait. Run by `node --test test/js/`.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { USER_A } from './fixtures/page-sandbox.mjs';
import { W1, W2 } from './fixtures/asset-pages.mjs';
import { device3d, DOC } from './fixtures/client-3d-device.mjs';

const el = {};
const el2 = {};
const request = (more = {}) => JSON.stringify(Object.assign({ partition: USER_A, wallId: W1, sha: DOC, token: null, boulder: null, mode: 'photoreal', roleColors: { Start: '#0f0' } }, more));

/** Fake wall3d.js and wall3d-splat-hold.js, a clock at `now` ms with the first render at `readyAt`. */
async function stageAt(now, readyAt, more = {}) {
    const made = await device3d(USER_A, more);
    const win = made.client.win;
    const log = { imports: 0, holds: [], mounts: [], disposed: 0, timers: [] };
    win.performance = { now: () => now, getEntriesByName: name => (name === 'bw-client-ready' && readyAt !== null ? [{ startTime: readyAt }] : []) };
    win.setTimeout = (fn, ms) => { log.timers.push(ms); return 0; };
    const wall3d = {
        mount: async (element, view, options) => {
            log.mounts.push({ element, view, options });
            if (more.fail) throw new Error(more.fail);
            return { dispose: () => { log.disposed++; } };
        },
    };
    const hold = { holdPhotoReal: until => log.holds.push(until) };
    win.__bwClient3dModules = async () => {
        log.imports++;
        if (more.noModules) throw new TypeError('Failed to fetch dynamically imported module');
        return [wall3d, hold];
    };
    return Object.assign(made, { log, win });
}

test('the hold is what is left of 20 s after the first render, and none after it', async () => {
    const { stage } = await device3d(USER_A);
    assert.equal(stage.HOLD_MS, 20000);
    assert.equal(stage.holdLeft(1500, { startTime: 1000 }, 20000), 19500);
    assert.equal(stage.holdLeft(21000, { startTime: 1000 }, 20000), 0);
    assert.equal(stage.holdLeft(30000, { startTime: 1000 }, 20000), 0);
    assert.equal(stage.holdLeft(5000, undefined, 20000), 15000, 'without the mark, from the page start');
});

test('a mount 1.5 s after the first render holds photo-real for the 18.5 s left; one 25 s after holds nothing', async () => {
    const early = await stageAt(2500, 1000);
    assert.equal(await early.stage.mount(el, request()), 'ok');
    assert.equal(early.log.holds.length, 1);
    assert.ok(early.log.holds[0] instanceof early.win.Promise || typeof early.log.holds[0].then === 'function', 'a hold');
    assert.deepEqual(early.log.timers, [18500]);
    assert.equal(early.log.mounts[0].options.initialMode, 'photoreal', 'photo-real is asked for, and held');

    const late = await stageAt(26000, 1000);
    assert.equal(await late.stage.mount(el, request()), 'ok');
    assert.deepEqual(late.log.holds, [null]);
    assert.deepEqual(late.log.timers, []);
});

test('a test may shorten the hold; the default mode is schematic', async () => {
    const { stage, log, win } = await stageAt(2000, 1000);
    win.__bwClient3dHoldMs = 3000;
    assert.equal(await stage.mount(el, request({ mode: null })), 'ok');
    assert.deepEqual(log.timers, [2000]);
    assert.equal(log.mounts[0].options.initialMode, 'schematic');
    assert.equal(log.mounts[0].options.boulderFocus, false);
    assert.equal(log.mounts[0].options.roleColors.Start, '#0f0');
});

test('no 3D code loads for a view the device does not hold in full', async () => {
    const gone = await stageAt(30000, 1000, { missing: [DOC] });
    assert.equal(await gone.stage.mount(el, request()), 'missing');
    assert.equal(gone.log.imports, 0);

    const other = await stageAt(30000, 1000);
    assert.equal(await other.stage.mount(el, request({ wallId: W2 })), 'missing');
    assert.equal(other.log.imports, 0);
});

test('the mounted view is the cached one, the boulder lit', async () => {
    const { stage, log } = await stageAt(30000, 1000);
    assert.equal(await stage.mount(el, request({ boulder: { id: 'b1', name: 'Crimp', roles: { h1: 'Top' }, colorFoot: [] } })), 'ok');
    const { view, options } = log.mounts[0];
    assert.equal(view.wallId, W1);
    assert.equal(view.boulderName, 'Crimp');
    assert.equal(view.holds[0].role, 'Top');
    assert.equal(options.boulderFocus, true);
});

test('an unmount frees the viewer; a mount replaced or unmounted meanwhile drops what it got', async () => {
    const { stage, log } = await stageAt(30000, 1000);
    assert.equal(await stage.mount(el, request()), 'ok');
    stage.unmount(el);
    assert.equal(log.disposed, 1);
    stage.unmount(el);
    assert.equal(log.disposed, 1, 'once');

    const first = stage.mount(el, request());
    const second = stage.mount(el, request());
    assert.deepEqual([await first, await second], ['stale', 'ok']);
    assert.equal(log.disposed, 1, 'the first never mounted a viewer that stays');

    const pending = stage.mount(el2, request());
    stage.unmount(el2);
    assert.equal(await pending, 'stale');
});

test('failures: modules that do not load, a model that cannot be drawn, no WebGL', async () => {
    assert.equal(await (await stageAt(30000, 1000, { noModules: true })).stage.mount(el, request()), 'unavailable');
    assert.equal(await (await stageAt(30000, 1000, { fail: 'wall3d-model: bad facet' })).stage.mount(el, request()), 'model');
    assert.equal(await (await stageAt(30000, 1000, { fail: 'Error creating WebGL context.' })).stage.mount(el, request()), 'webgl');
});
