// The service worker's routing of the Phase 3 data documents (PR 3.1): /d/{wall|holds|boulders|gallery|comments}/{sha}
// is content-addressed like view3d, so it is cached under the bare sha in the partition's `-3d` cache. Run by
// `node --test test/js/`, over the same vm sandbox as service-worker.test.mjs.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { loadWorker, dispatchFetch, response, ORIGIN } from './fixtures/sw-sandbox.mjs';

const ALICE = 'u-' + 'a'.repeat(20);
const PARTITION = '/api/me/cache-partition';

function workerFor(label) {
    return loadWorker({ routes: {
        [PARTITION]: () => response(200, JSON.stringify({ partition: label }), { 'content-type': 'application/json' }),
    } });
}

test('the Phase 3 data documents are stored in the 3D cache under their bare sha, a share token stripped, and hit from there', async () => {
    const worker = workerFor(ALICE);
    const kinds = ['wall', 'holds', 'boulders', 'gallery', 'comments'];
    const shas = kinds.map((_, i) => String(i + 1).repeat(64));
    kinds.forEach((kind, i) => {
        worker.routes[`/api/walls/w1/d/${kind}/${shas[i]}?token=t1`] = () => response(200, `{"kind":"${kind}"}`,
            { 'content-type': 'application/json', 'content-length': String(kind.length + 10), etag: `"${shas[i]}"` });
    });
    for (const [i, kind] of kinds.entries()) {
        await dispatchFetch(worker, `/api/walls/w1/d/${kind}/${shas[i]}?token=t1`).settle();
    }
    assert.deepEqual([...worker.stored.get(`bw-a1-${ALICE}-3d`).keys()].sort(), shas.map(sha => `${ORIGIN}/__bw/${sha}`).sort());
    assert.equal(worker.stored.get(`bw-a1-${ALICE}-img`)?.size ?? 0, 0, 'JSON never lands in the image cache');
    assert.equal(await (await dispatchFetch(worker, `/api/walls/w1/d/holds/${shas[1]}`).settle()).text(), '{"kind":"holds"}');
    assert.equal(worker.fetches.filter(f => f.path.includes('/d/')).length, kinds.length, 'the second holds read is a hit');
});

test('a data document answered no-store or without its sha as ETag is served but never stored', async () => {
    const worker = workerFor(ALICE);
    const sha = '9'.repeat(64);
    worker.routes[`/api/walls/w1/d/boulders/${sha}`] = () => response(200, '{}',
        { 'content-type': 'application/json', 'cache-control': 'no-store', etag: `"${sha}"` });
    worker.routes[`/api/walls/w1/d/wall/${sha}`] = () => response(200, '{}', { 'content-type': 'application/json' });
    await dispatchFetch(worker, `/api/walls/w1/d/boulders/${sha}`).settle();
    await dispatchFetch(worker, `/api/walls/w1/d/wall/${sha}`).settle();
    assert.equal(worker.stored.get(`bw-a1-${ALICE}-3d`)?.size ?? 0, 0);
});
