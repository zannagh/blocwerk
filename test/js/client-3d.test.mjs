// The offline client's 3D reads (Phase 3 PR 3.13, client-3d.js): 3D shows only when the device holds the view3d
// document AND every texture and mask it draws (cache.match, never a fetch); the 3D opt-out hides it unless the wall
// is saved for offline 3D; photo-real only with a cached splat level, and the view's ladder keeps only cached levels;
// the share token rides on every URL; a boulder lights its holds by role; a lock, another label or another share
// link's wall reads nothing. Run by `node --test test/js/`.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { USER_A, USER_B, ORIGIN } from './fixtures/page-sandbox.mjs';
import { W1, W2 } from './fixtures/asset-pages.mjs';
import { TOKEN, plain } from './fixtures/client-device.mjs';
import { device3d, DOC, OLD_DOC, TEXTURE, MASK, LEVELS } from './fixtures/client-3d-device.mjs';

const ready = photoReal => ({ state: 'ready', sha: DOC, photoReal });

test('a cached document with its texture and mask is ready, with photo-real when a splat level is cached', async () => {
    const { three } = await device3d(USER_A);
    assert.deepEqual(plain(await three.status(USER_A, W1)), ready(true));
    assert.deepEqual(JSON.parse(await three.statusJson(USER_A, W1)), ready(true));
});

test('a wall whose row lists no view3d document has no 3D on this device', async () => {
    const { three } = await device3d(USER_A);
    assert.deepEqual(plain(await three.status(USER_A, W2)), { state: 'none', sha: null, photoReal: false });
});

test('a missing texture, mask or document is missing: nothing is fetched to fill it', async () => {
    for (const gone of [TEXTURE, MASK, DOC]) {
        const { three, client } = await device3d(USER_A, { missing: [gone] });
        let fetched = 0;
        client.win.fetch = () => { fetched++; return Promise.reject(new Error('offline')); };
        assert.equal((await three.status(USER_A, W1)).state, 'missing', gone);
        assert.equal(await three.view(USER_A, W1, DOC, null, null), null);
        assert.equal(fetched, 0);
    }
});

test('an older document cached in full stands in when the newest is not', async () => {
    const { three } = await device3d(USER_A, { docs: [OLD_DOC, DOC] });
    assert.deepEqual(plain(await three.status(USER_A, W1)), ready(true));
});

test('without a cached splat level (or a splat matrix) photo-real is not offered, and the view has no ladder', async () => {
    const none = await device3d(USER_A, { missing: LEVELS });
    assert.deepEqual(plain(await none.three.status(USER_A, W1)), ready(false));
    const view = await none.three.view(USER_A, W1, DOC, null, null);
    assert.deepEqual(plain(view.splatLevels), []);
    assert.equal(view.splatUrl, null);
    assert.equal(view.splatMobileUrl, null);

    const noMatrix = await device3d(USER_A, { doc: { splatMatrix: null } });
    assert.deepEqual(plain(await noMatrix.three.status(USER_A, W1)), ready(false));
});

test('the view keeps only the cached splat levels, so photo-real never downloads one', async () => {
    const { three } = await device3d(USER_A, { missing: [LEVELS[1]] });
    const view = await three.view(USER_A, W1, DOC, null, null);
    assert.deepEqual(plain(view.splatLevels.map(l => l.url)), [`/api/walls/${W1}/a/${LEVELS[0]}`]);
});

test('the 3D opt-out shows no 3D, whatever is cached; a wall saved for offline 3D still shows', async () => {
    const off = await device3d(USER_A, { cookie: 'theme=dark; blocwerk-cache-3d=off' });
    assert.deepEqual(plain(await off.three.status(USER_A, W1)), { state: 'off', sha: null, photoReal: false });
    assert.equal(await off.three.view(USER_A, W1, DOC, null, null), null);

    const saved = await device3d(USER_A, { cookie: 'blocwerk-cache-3d=off', save3d: true });
    assert.deepEqual(plain(await saved.three.status(USER_A, W1)), ready(true));
    const on = await device3d(USER_A, { cookie: 'blocwerk-cache-3d=on' });
    assert.deepEqual(plain(await on.three.status(USER_A, W1)), ready(true));
});

test('a lock, another label than the active one, or a refused ledger reads nothing', async () => {
    const { three, ledger } = await device3d(USER_A);
    assert.equal((await three.status(USER_B, W1)).state, 'none');
    await ledger.setMeta({ locked: { label: USER_B, since: Date.now() } });
    assert.equal((await three.status(USER_A, W1)).state, 'none');
    assert.equal(await three.view(USER_A, W1, DOC, null, null), null);
});

test('a share link reads only its own wall, and its token rides on every texture and splat URL', async () => {
    const own = await device3d('anon', { path: `/v/walls/${W1}/shared/${TOKEN}/3d` });
    assert.deepEqual(plain(await own.three.status('anon', W1)), ready(true));
    const view = await own.three.view('anon', W1, DOC, 'a b', null);
    assert.equal(view.textures[0].url, `/api/walls/${W1}/a/${TEXTURE}?token=a%20b`);
    assert.equal(view.textures[0].maskUrl, `/api/walls/${W1}/a/${MASK}?token=a%20b`);
    assert.ok(view.splatLevels.every(l => l.url.endsWith('?token=a%20b')));

    const other = await device3d('anon', { path: `/v/walls/${W2}/shared/${TOKEN}/3d` });
    assert.equal((await other.three.status('anon', W1)).state, 'none', "another share link's wall");
});

test('a document of another wall is never drawn', async () => {
    const { three } = await device3d(USER_A, { doc: { wallId: W2 } });
    assert.equal((await three.status(USER_A, W1)).state, 'missing');
});

test("a boulder lights its holds by role; a folded hold takes its copies' strongest; the colour rule adds footholds", async () => {
    const { three } = await device3d(USER_A);
    const boulder = { id: 'B1', name: 'Crimp', roles: { h1: 'Hand', h2b: 'Start' }, colorFoot: ['H3'] };
    const view = await three.view(USER_A, W1, DOC, null, boulder);
    assert.equal(view.boulderId, 'b1');
    assert.equal(view.boulderName, 'Crimp');
    assert.deepEqual(plain(view.holds.map(h => h.role)), ['Hand', 'Start', 'ColorFoot']);
    assert.deepEqual(plain(three.withBoulder({ holds: [{ id: 'x' }] }, null)), { holds: [{ id: 'x' }] });
});

test('the reads touch the ledger and the caches only', async () => {
    const { three, client, caches } = await device3d(USER_A);
    let fetched = 0;
    client.win.fetch = () => { fetched++; return Promise.reject(new Error('no')); };
    await three.status(USER_A, W1);
    await three.view(USER_A, W1, DOC, null, null);
    assert.equal(fetched, 0);
    assert.ok(caches.stores.get(`bw-a1-${USER_A}-3d`).has(`${ORIGIN}/__bw/${DOC}`));
});
