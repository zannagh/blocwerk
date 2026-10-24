// The offline client steps the PR 3.9 specs share: rewriting the routes the shell declares, precaching the client from a
// Server page, and reading what the worker recorded.
import { expect, signIn, installWorker, settled } from './fixtures.mjs';

// The routes the client declares (ClientRoutes.Declared, PR 3.11), as its shell's meta carries them.
export const ROUTES = 'walls walls/{WallId:guid} walls/{WallId:guid}/shared/{ShareToken} walls/{WallId:guid}/boulders/{BoulderId:guid} walls/{WallId:guid}/boulders/{BoulderId:guid}/shared/{ShareToken}';

// The shell's routes meta rewritten to `routes` (the default keeps the declared ones): the worker reads the routes from
// the shell it precaches. `pairs` adds further rewrites of the shell.
export const declareRoutes = (routes = ROUTES, pairs = []) => ({
    path: '^/v/$',
    rewrite: [[`name="bw-client-routes" content="${ROUTES}"`, `name="bw-client-routes" content="${routes}"`], ...pairs]
});

/** The page's precache outcome (bwClientPrecache.done): complete, skipped, unsupported, waiting, failed or other. */
export function precacheOutcome(page) {
    return page.evaluate(() => (window.bwClientPrecache ? window.bwClientPrecache.done : null));
}

/** Signs Alice in on Alpha's wall page under the installed worker, and waits for the page's precache. */
export function precacheAsAlice(page, world, server, faults = [declareRoutes()]) {
    return precacheAs(page, server, world.users.alice, `/walls/${world.walls.alpha.id}`, faults);
}

/** Signs `user` in on `path` under the installed worker, and waits for the page's fill and precache. */
export async function precacheAs(page, server, user, path, faults = [declareRoutes()]) {
    await server.faults(faults);
    await signIn(page, user);
    await installWorker(page, path);
    await settled(page);
    return precacheOutcome(page);
}

/** The worker's completeness record `{ version, routes }`, or null. */
export function clientRecord(page) {
    return page.evaluate(async () => {
        const hit = await caches.match('/__bw/client', { cacheName: 'bw-sw-state' });
        return hit ? hit.json() : null;
    });
}

export function clientCaches(page) {
    return page.evaluate(async () => (await caches.keys()).filter(n => n.startsWith('blocwerk-client-')).sort());
}

/** The client version the page's meta names. */
export function pageVersion(page) {
    return page.locator('meta[name="bw-client-version"]').getAttribute('content');
}

/** Navigates and waits for the offline client's first render at the matching /v/ route. */
export async function expectClient(page, path) {
    await page.goto(path);
    await expect(page).toHaveURL(new RegExp(`/v${path.replace(/[?]/g, '\\?')}$`));
    await expect.poll(() => page.evaluate(() => performance.getEntriesByName('bw-client-ready').length), { timeout: 30000 }).toBe(1);
}

/** Navigates and expects the worker's offline page. */
export async function expectOfflinePage(page, path) {
    await page.goto(path);
    await expect(page.locator('h1')).toHaveText("You're offline");
    await expect(page).toHaveURL(new RegExp(`${path.replace(/[?]/g, '\\?')}$`));
}

/** The wall's data documents the ledger names that are missing from the partition's -3d cache (the worker stores them as fetched). */
export const missingDocs = (page, partition, wallId) => page.evaluate(async ([p, w]) => {
    const l = await window.bwAssetLedger.open();
    const row = (await l.wallsOf(p)).find(r => r.wallId === w);
    const shas = Object.values((row && row.data) || {}).map(d => d.sha);
    const hits = await Promise.all(shas.map(s => caches.match(`${location.origin}/__bw/${s}`, { cacheName: `bw-a1-${p}-3d` })));
    return shas.length === 0 ? ['no documents named'] : shas.filter((s, i) => !hits[i]);
}, [partition, wallId]);
