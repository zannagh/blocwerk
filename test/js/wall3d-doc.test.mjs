// wall3d-doc.js: the 3D view's cached view3d document turned back into the boulder's view (PR 2.8).
// Run by `node --test test/js/`.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { DOC_FAILED, applyOverlay, resolveView, tokenized, withToken } from '../../src/Blocwerk.Web/wwwroot/js/wall3d-doc.js';

const WALL = '11111111-1111-1111-1111-111111111111';
const SHA = 'a'.repeat(64);
const doc = () => ({
    wallId: WALL, boulderId: null, boulderName: null,
    holds: [{ id: 'h1', role: null }, { id: 'h2', role: null }],
    textures: [{ facetId: 'F1', url: `/api/walls/${WALL}/a/${SHA}`, maskUrl: null }],
    splatUrl: `/api/walls/${WALL}/a/${SHA}`, splatMobileUrl: null,
    splatLevels: [{ url: `/x/splat?v=2`, splats: 40000, sizeBytes: 1 }],
});
const fetchOf = (status, body) => {
    const calls = [];
    const fn = async (url, init) => { calls.push({ url, init }); return { ok: status === 200, status, json: async () => body }; };
    fn.calls = calls;
    return fn;
};

test('a payload with a document is fetched and given back its boulder and its share token', async () => {
    const fetch = fetchOf(200, doc());
    const overlay = { boulderId: 'b1', boulderName: 'Crimp', roles: { h2: 'Start' }, holds: [{ id: 'h9', role: 'Top' }] };

    const view = await resolveView({ doc: `/api/walls/${WALL}/d/view3d/${SHA}?token=t%2B1`, wallId: WALL, sha: SHA, token: 't+1', overlay }, fetch);

    assert.equal(fetch.calls[0].url, `/api/walls/${WALL}/d/view3d/${SHA}?token=t%2B1`);
    assert.equal(fetch.calls[0].init.credentials, 'same-origin');
    assert.equal(view.boulderId, 'b1');
    assert.equal(view.boulderName, 'Crimp');
    assert.deepEqual(view.holds.map(h => [h.id, h.role]), [['h1', null], ['h2', 'Start'], ['h9', 'Top']]);
    assert.equal(view.textures[0].url, `/api/walls/${WALL}/a/${SHA}?token=t%2B1`);
    assert.equal(view.textures[0].maskUrl, null);
    assert.equal(view.splatLevels[0].url, '/x/splat?v=2&token=t%2B1');
});

test('a document that cannot be fetched, or is another wall\'s, throws DOC_FAILED so the stage sends the view instead', async () => {
    const payload = { doc: '/d', wallId: WALL, sha: SHA, overlay: null };
    for (const fetch of [fetchOf(404, null), fetchOf(200, { ...doc(), wallId: 'other' }), async () => { throw new TypeError('offline'); }]) {
        await assert.rejects(resolveView(payload, fetch), err => err.message.startsWith(DOC_FAILED));
    }
});

test('a whole view (no document) is drawn as it is, and no token or overlay leaves the view unchanged', async () => {
    const view = doc();
    assert.equal(await resolveView(view, () => assert.fail('no fetch')), view);
    assert.equal(withToken(view, null), view);
    assert.equal(applyOverlay(view, null), view);
    assert.equal(tokenized(null, 't'), null);
});
