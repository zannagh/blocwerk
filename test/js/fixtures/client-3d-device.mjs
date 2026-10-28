// A device for the offline client's 3D tests (client-3d*.test.mjs): the client store's device (client-device.mjs) plus
// client-3d.js and client-3d-stage.js, with a view3d document of wall W1 that a Server page's 3D view cached: its row
// lists the document (`docs`, as asset-3d.js's noteDoc writes it), the document sits in -3d, its texture and mask in
// -img, and its two splat levels in -3d unless left out.
import { readFileSync } from 'node:fs';
import vm from 'node:vm';
import { device, sha } from './client-device.mjs';
import { ORIGIN } from './page-sandbox.mjs';
import { W1 } from './asset-pages.mjs';

const SCRIPTS = ['client-3d.js', 'client-3d-stage.js'].map(name => new URL(`../../../src/Blocwerk.Client/wwwroot/js/${name}`, import.meta.url));

export const DOC = sha('d');
export const OLD_DOC = sha('e');
export const TEXTURE = sha('7');
export const MASK = sha('8');
export const LEVELS = [sha('9'), sha('6')];
const path = shaOf => `/api/walls/${W1}/a/${shaOf}`;

/** A view3d document of W1 as View3DDocument serialises it (web casing, no token, no boulder). */
export function view3d(extra = {}) {
    return Object.assign({
        wallId: W1, boulderId: null, boulderName: null,
        facets: [{ id: 'F1' }],
        holds: [{ id: 'h1', role: null }, { id: 'h2', role: null, duplicateIds: ['h2b'] }, { id: 'h3', role: null }],
        textures: [{ facetId: 'F1', url: path(TEXTURE), maskUrl: path(MASK) }],
        splatUrl: null, splatMobileUrl: null,
        splatLevels: [{ url: path(LEVELS[0]), splats: 40000, sizeBytes: 1 }, { url: path(LEVELS[1]), splats: 250000, sizeBytes: 2 }],
        splatMatrix: Array.from({ length: 16 }, (_, i) => (i % 5 === 0 ? 1 : 0)),
    }, extra);
}

// The shell's import map: the client's runtime under /v/, and the 3D code (the worker's 3D group) outside it.
export const CODE = { '/js/wall3d.js': '/js/wall3d.abcdefghij.js', '/lib/spark/spark.module.min.js': '/lib/spark/spark.module.min.bcdefghijk.js' };
const IMPORT_MAP = { textContent: JSON.stringify({ imports: { '/v/_framework/dotnet.js': '/v/_framework/dotnet.x1.js', ...CODE } }) };

/**
 * more: device()'s options, plus docs (the row's view3d documents, newest first; default [DOC]), save3d, cookie
 * (document.cookie), missing (worker keys left out of the caches), doc (the document's fields), code (the 3D code in
 * the client's cache; default true, false for none, or the list of import-map keys held).
 */
export async function device3d(label, more = {}) {
    const made = await device(label, more);
    const { client, ledger, caches } = made;
    for (const script of SCRIPTS) {
        vm.runInContext(readFileSync(script, 'utf8'), client.win, { filename: script.pathname.split('/').pop() });
    }
    client.win.document.cookie = more.cookie || '';
    const query = client.win.document.querySelector;
    client.win.document.querySelector = selector => (selector === 'script[type="importmap"]' ? IMPORT_MAP : query(selector));
    const held = more.code === false ? [] : Array.isArray(more.code) ? more.code : Object.keys(CODE);
    for (const name of held) {
        await caches.putText('blocwerk-client-1111111111111111', ORIGIN + CODE[name], 'module');
    }
    const docs = (more.docs || [DOC]).map(s => ({ sha: s, kind: 'view3d', tier: 'view3d', bytes: 10 }));
    await ledger.updateWall(label, W1, row => Object.assign(row, { docs, save3d: !!more.save3d }));
    const missing = new Set(more.missing || []);
    const put = async (cache, key, text) => (missing.has(key) ? undefined : caches.putText(cache, `${ORIGIN}/__bw/${key}`, text));
    await put(`bw-a1-${label}-3d`, DOC, JSON.stringify(view3d(more.doc)));
    await put(`bw-a1-${label}-img`, TEXTURE, 'jpeg');
    await put(`bw-a1-${label}-img`, MASK, 'png');
    for (const level of LEVELS) {
        await put(`bw-a1-${label}-3d`, level, 'spz');
    }
    return Object.assign(made, { three: client.win.bwClient3d, stage: client.win.bwClient3dStage });
}
