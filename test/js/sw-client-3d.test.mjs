// The 3D view's modules in the offline client (service-worker.js, Phase 3 PR 3.13): the shell names wall3d.js, its
// imports and three.js / Spark in its import map, so the precache stores them; a /v/ page's import of /js/wall3d.js and
// every module that fingerprinted module imports in turn (their referrer is the importing module, not the page) are
// answered from the client cache with no network, /lib/ included; a Server page, which imports the plain paths, stays
// network-first as before. The 3D code is a group of its own: a desktop precaches it, a phone or tablet only once a 3D
// view was opened online there (`client-store-3d`, then carried into later versions), the 3D opt-out never.
// Run by `node --test test/js/`.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { dispatchFetch, response, ORIGIN } from './fixtures/sw-sandbox.mjs';
import { V1, V2, ROUTES, STATE, shellHtml, deploy, workerWith, precache } from './fixtures/sw-client.mjs';

const WALL3D = '/js/wall3d.abcdefghij.js';
const STAGE = '/js/wall3d-stage.bcdefghijk.js';
const THREE = '/lib/three/three.module.min.cdefghijkl.js';
const SPARK = '/lib/spark/spark.module.min.defghijklm.js';
const MAPPED = { '/js/wall3d.js': WALL3D, '/js/wall3d-stage.js': STAGE, '/lib/three/three.module.min.js': THREE, '/lib/spark/spark.module.min.js': SPARK };

function deploy3d(worker, version, mapped = MAPPED) {
    const shell = shellHtml(version, ROUTES).replace('"imports":{', `"imports":{${Object.entries(mapped).map(([k, v]) => `"${k}":"${v}",`).join('')}`);
    deploy(worker.routes, version, { shell });
    for (const url of Object.values(mapped)) {
        worker.routes[url] = () => response(200, 'module ' + url, { 'content-type': 'text/javascript' });
    }
}

async function worker3d(group3d) {
    const worker = workerWith();
    deploy3d(worker, V1);
    const done = await precache(worker, { group3d });
    assert.equal(done.complete, true);
    return worker;
}

const cacheOf = (worker, version) => [...worker.stored].find(([name]) => name === `blocwerk-client-${version}`)[1];
const held3d = (worker, version) => Object.values(MAPPED).filter(url => cacheOf(worker, version).has(ORIGIN + url));
const fetched3d = worker => worker.fetches.filter(f => Object.values(MAPPED).includes(f.path)).length;

function store3d(worker) {
    let pending;
    const sent = [];
    worker.listeners.message({ data: { proto: 1, type: 'client-store-3d' }, ports: [{ postMessage: m => sent.push(m) }], waitUntil: p => { pending = p; } });
    return pending.then(() => sent[0]);
}

const served = async (worker, path, referrer) => {
    const before = worker.fetches.length;
    const answer = await dispatchFetch(worker, path, { referrer: ORIGIN + referrer }).settle();
    if (!answer) {
        return { status: null, text: null, network: 'not intercepted' };
    }
    return { status: answer.status, text: await answer.text(), network: worker.fetches.length - before };
};

test('a desktop precache stores every 3D module the import map names', async () => {
    const worker = await worker3d('precache');
    assert.deepEqual(held3d(worker, V1), Object.values(MAPPED));
});

test('a phone or tablet precaches no 3D code until 3D was opened, the opt-out none, and an unknown choice none', async () => {
    for (const group3d of ['opened', 'none', undefined, 'everything']) {
        const worker = await worker3d(group3d);
        assert.deepEqual(held3d(worker, V1), [], String(group3d));
        assert.equal(fetched3d(worker), 0, String(group3d));
        assert.ok(cacheOf(worker, V1).has(ORIGIN + '/v/'), 'the rest of the client is complete');
    }
});

test('a 3D view opened online stores the 3D code into the complete version, and later versions carry it', async () => {
    const worker = await worker3d('opened');
    assert.deepEqual({ ...(await store3d(worker)) }, { proto: 1, type: 'client-stored-3d', complete: true });
    assert.deepEqual(held3d(worker, V1), Object.values(MAPPED));
    assert.ok(worker.stored.get(STATE).has('/__bw/client-3d-opened'));

    // V2 changes wall3d.js only: the phone fills it, and copies the unchanged modules from V1 with no network.
    const changed = { ...MAPPED, '/js/wall3d.js': '/js/wall3d.zyxwvutsrq.js' };
    deploy3d(worker, V2, changed);
    const before = worker.fetches.length;
    assert.equal((await precache(worker, { group3d: 'opened' })).complete, true);
    const cache = cacheOf(worker, V2);
    assert.ok(Object.values(changed).every(url => cache.has(ORIGIN + url)));
    assert.deepEqual(worker.fetches.slice(before).map(f => f.path).filter(p => /^\/(js|lib)\//.test(p) && p !== '/js/sw-guard.g1.js'), ['/js/wall3d.zyxwvutsrq.js']);
});

test('a 3D view opened before the client is precached: the next precache of a phone takes the 3D code', async () => {
    const worker = workerWith();
    deploy3d(worker, V1);
    assert.equal((await store3d(worker)).complete, false, 'no complete version to store into');
    assert.equal((await precache(worker, { group3d: 'opened' })).complete, true);
    assert.deepEqual(held3d(worker, V1), Object.values(MAPPED));
});

test('a desktop whose complete version went without the 3D code gets it on its next precache; the opt-out keeps none', async () => {
    const worker = await worker3d('none');
    await precache(worker, { group3d: 'none' });
    assert.deepEqual(held3d(worker, V1), []);
    await precache(worker, { group3d: 'precache' });
    assert.deepEqual(held3d(worker, V1), Object.values(MAPPED));
});

test("a /v/ page's 3D import and every import of a fingerprinted module come from the client cache, /lib/ too", async () => {
    const worker = await worker3d('precache');
    worker.routes[WALL3D] = worker.routes[STAGE] = worker.routes[THREE] = worker.routes[SPARK] = () => Promise.reject(new TypeError('offline'));
    assert.deepEqual(await served(worker, WALL3D, `/v/walls/w/3d`), { status: 200, text: 'module ' + WALL3D, network: 0 });
    assert.deepEqual(await served(worker, STAGE, WALL3D), { status: 200, text: 'module ' + STAGE, network: 0 });
    assert.deepEqual(await served(worker, THREE, WALL3D), { status: 200, text: 'module ' + THREE, network: 0 });
    assert.deepEqual(await served(worker, SPARK, '/js/wall3d-splat-levels.efghijklmn.js'), { status: 200, text: 'module ' + SPARK, network: 0 });
});

test("a Server page's plain-path 3D imports stay network-first", async () => {
    const worker = await worker3d('precache');
    worker.routes['/js/wall3d-stage.js'] = () => response(200, 'plain stage', { 'content-type': 'text/javascript' });
    assert.equal((await served(worker, '/js/wall3d-stage.js', '/js/wall3d.js')).network, 1);
    assert.equal((await served(worker, STAGE, '/walls/w/3d')).network, 1, 'a Server page asking for a fingerprinted URL too');
    assert.equal((await served(worker, '/lib/three/three.module.min.js', '/js/wall3d.js')).network, 'not intercepted', 'the browser fetches it, as before');
});
