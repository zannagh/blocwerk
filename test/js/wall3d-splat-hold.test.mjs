// The photo-real hold (Phase 3 PR 3.13, wall3d-splat-hold.js): the offline client's first ~20 s put no splat in memory.
// A held level load (wall3d-splat-levels.js) imports nothing and downloads nothing until the hold ends; a mode switch
// during the hold (abort) drops the load without either; a page that sets no hold (the Server pages) never waits.
// Run by `node --test test/js/`.
import { test } from 'node:test';
import assert from 'node:assert/strict';

globalThis.location ??= { search: '' };
globalThis.document ??= { visibilityState: 'visible', addEventListener() {}, removeEventListener() {} };
const { holdPhotoReal, photoRealHeld, photoRealHold } = await import('../../src/Blocwerk.Web/wwwroot/js/wall3d-splat-hold.js');
const { createLevelLoader } = await import('../../src/Blocwerk.Web/wwwroot/js/wall3d-splat-levels.js');

const settled = async promise => {
    let done = false;
    promise.then(() => { done = true; }, () => { done = true; });
    for (let i = 0; i < 20; i++) {
        await new Promise(resolve => setImmediate(resolve));
    }
    return done;
};

function released() {
    let release;
    const until = new Promise(resolve => { release = resolve; });
    return { until, release };
}

function loader(fetched) {
    globalThis.fetch = url => {
        fetched.push(url);
        return Promise.reject(new Error('offline'));
    };
    return createLevelLoader({
        renderer: {}, scene: { add() {}, remove() {} }, view: { splatMatrix: [] },
        levels: [{ url: '/api/walls/w/a/' + '9'.repeat(64), splats: 1000 }], light: true, clip: null, cull: null,
        retry: { meshOptions: {}, rendererOptions: {}, loaded() {} }, request() {}, visible: () => true, closed: () => false,
        onShown() {}, onStage() {},
    });
}

test('without a hold (the Server pages) nothing waits', async () => {
    holdPhotoReal(null);
    assert.equal(photoRealHeld(), false);
    assert.equal(await settled(photoRealHold()), true);
});

test('a hold waits for its end; an abort ends the wait early', async () => {
    const { until, release } = released();
    holdPhotoReal(until);
    assert.equal(photoRealHeld(), true);
    assert.equal(await settled(photoRealHold()), false);
    const abort = new AbortController();
    const aborted = photoRealHold(abort.signal);
    abort.abort();
    assert.equal(await settled(aborted), true);
    release();
    assert.equal(await settled(photoRealHold()), true);
    holdPhotoReal(null);
});

test('a held level load downloads nothing until the hold ends, then goes on', async () => {
    const { until, release } = released();
    holdPhotoReal(until);
    const fetched = [];
    const levels = loader(fetched);
    const load = levels.load(0, null);
    assert.equal(await settled(load), false, 'still held');
    assert.deepEqual(fetched, []);
    assert.equal(levels.downloading, true, 'a mode switch now aborts it (wall3d-splat.js cancel)');

    release();
    await assert.rejects(load, err => err.stage === 'download', 'past the hold: Spark loaded, then the (offline) download');
    assert.equal(fetched.length, 1);
    holdPhotoReal(null);
});

test('an abort during the hold drops the load: no Spark, no download', async () => {
    const { until } = released();
    holdPhotoReal(until);
    const fetched = [];
    const levels = loader(fetched);
    const load = levels.load(0, null);
    await settled(load);
    levels.abort();
    assert.equal(await load, false);
    assert.deepEqual(fetched, []);
    holdPhotoReal(null);
});
