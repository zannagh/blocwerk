// Shared pieces of the asset-cache end-to-end tests (asset-cache*.test.mjs): two walls, cache listings, and a member's
// page whose manifests list core, lazy and 3D assets.
import { loadPage, fakeCaches, json, manifest, sha, USER_A, USER_B, ORIGIN } from './page-sandbox.mjs';

export const W1 = '11111111-1111-1111-1111-111111111111';
export const W2 = '22222222-2222-2222-2222-222222222222';
export const keys = (caches, name) => [...(caches.stores.get(name) || new Map()).keys()].map(k => k.split('/__bw/')[1]).sort();
export const names = caches => [...caches.stores.keys()].sort();
export const plain = value => JSON.parse(JSON.stringify(value));
// A route answering one wall's manifest that lists `shas` as core.
export const listing = (wallId, ...shas) => ({ [`/api/walls/${wallId}/manifest`]: () => json(manifest(wallId, shas.map(s => [s, 'core']))) });

export function standardCaches() {
    return fakeCaches({
        [`bw-a1-${USER_A}-img`]: [`${ORIGIN}/__bw/${sha('a')}`],
        [`bw-a1-${USER_B}-img`]: [`${ORIGIN}/__bw/${sha('b')}`],
        'bw-a1-anon-img': [`${ORIGIN}/__bw/${sha('c')}`],
        'bw-sw-state': ['/__bw/partition'],
        'blocwerk-shell-v4': ['/offline.html'],
        'blocwerk-offline': ['x'],
    });
}

export function memberPage(more = {}) {
    const routes = Object.assign({
        [`/api/walls/${W1}/manifest`]: () => json(manifest(W1, [[sha('1'), 'core'], [sha('2'), 'lazy'], [sha('3'), 'view3d', { kind: 'texture' }]]), 200, { etag: 'W/"m1"' }),
        [`/api/walls/${W2}/manifest`]: () => json(manifest(W2, [[sha('4'), 'core', { widths: [640, 1280, 1920, 2560] }]]), 200, { etag: 'W/"m2"' }),
    }, more.routes || {});
    return loadPage(Object.assign({ label: USER_A, summary: { wallsWith3d: 1, walls: [W1, W2] }, path: `/walls/${W2}` }, more, { routes }));
}
