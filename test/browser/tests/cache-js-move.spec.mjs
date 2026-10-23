// PR 3.7 moved the device cache's ledger, wipe, lock, session and clear modules from /js/ to
// /_content/Blocwerk.Shared.Ui/js/. Installed apps keep the worker they have until it updates, and the released worker
// (main 7140dad, frozen byte for byte in ../released/) predates /_content/ in its shell routes, so a page of this build
// runs under it first. And a tab opened before the deploy keeps its document while its enhanced navigations bring the
// new script URLs. Both pairings must keep the modules loaded, once, with no request for a /js/ path that is gone.
import fs from 'node:fs';
import { test, expect, signIn, installWorker, controlled, cacheNames, hasPageModules, settled, pageFetch } from '../harness/fixtures.mjs';

const released = name => fs.readFileSync(new URL(`../released/${name}`, import.meta.url), 'utf8');
const LIBRARY = '/_content/Blocwerk.Shared.Ui/js/';
const MOVED = ['asset-ledger-idb', 'asset-ledger', 'asset-wipe', 'asset-lock', 'asset-session', 'asset-clear'];
const GLOBALS = ['bwAssetLedgerIdb', 'bwAssetLedger', 'bwAssetWipe', 'bwAssetLock', 'bwAssetSession', 'bwAssetClear'];
const OLD_PATH = new RegExp(`^/js/(${MOVED.join('|')})(\\.[a-z0-9]+)?\\.js$`);

// The released worker and the file it imports, served in place of this build's.
const releasedWorker = () => [
    { path: '^/js/service-worker\\.js$', body: released('service-worker.js'), headers: { 'content-type': 'text/javascript', 'service-worker-allowed': '/' } },
    { path: '^/js/sw-asset-cache\\.js$', body: released('sw-asset-cache.js'), headers: { 'content-type': 'text/javascript' } }
];

// The pathnames of the page's moved-module scripts, and whether each global they define is there.
function movedScripts(page) {
    return page.evaluate(([moved, globals]) => ({
        srcs: [...document.scripts].map(s => s.src && new URL(s.src).pathname)
            .filter(p => p && moved.some(m => p.split('/').pop().startsWith(m + '.'))),
        defined: globals.filter(g => !!window[g])
    }), [MOVED, GLOBALS]);
}

async function expectModulesFromTheLibrary(page) {
    const { srcs, defined } = await movedScripts(page);
    expect(defined).toEqual(GLOBALS);
    // One script per module (asset-ledger-idb's name also starts with asset-ledger, hence the exact stem).
    for (const name of MOVED) {
        const own = srcs.filter(p => p.split('/').pop().replace(/\.[a-z0-9]+\.js$/, '') === name);
        expect(own, name).toHaveLength(1);
        expect(own[0].startsWith(LIBRARY), own[0]).toBe(true);
    }
}

// The shell caches' keys under the library path (the released worker's shell-v4, this build's later one).
function shellLibraryKeys(page) {
    return page.evaluate(async library => {
        const paths = [];
        for (const name of (await caches.keys()).filter(n => n.startsWith('blocwerk-shell-'))) {
            paths.push(...(await (await caches.open(name)).keys()).map(r => new URL(r.url).pathname));
        }
        return paths.filter(p => p.startsWith(library)).sort();
    }, LIBRARY);
}

// Asks the registration for an update and waits until the new worker controls the page.
function updateWorker(page) {
    return page.evaluate(() => new Promise((resolve, reject) => {
        const timer = setTimeout(() => reject(new Error('no controllerchange')), 20000);
        navigator.serviceWorker.addEventListener('controllerchange', () => { clearTimeout(timer); resolve(); }, { once: true });
        navigator.serviceWorker.getRegistration('/').then(reg => reg.update()).catch(reject);
    }));
}

test('under the released worker the moved modules load from the library; after its update the shell keeps them for offline', async ({ page, world, server }) => {
    await server.faults(releasedWorker());
    await signIn(page, world.users.alice);
    await installWorker(page);
    test.skip(!(await hasPageModules(page)), 'needs the 2.7 page modules');
    const start = await server.mark();
    await page.reload();
    await controlled(page);
    await settled(page);

    // The released worker: it routes nothing under /_content/, so its shell holds none of the library's files.
    await expectModulesFromTheLibrary(page);
    expect(await shellLibraryKeys(page)).toEqual([]);

    // The deploy: this build's worker replaces the released one in the open tab.
    await server.faults([]);
    await updateWorker(page);
    await page.reload();
    await controlled(page);
    await settled(page);
    await expectModulesFromTheLibrary(page);

    // This build's worker routes the library's files into its shell. Each script is fetched through it here, past the
    // HTTP cache (no-store): WebKit answers a reload's immutable scripts from its memory cache, never asking the worker.
    const { srcs } = await movedScripts(page);
    for (const src of srcs) {
        expect((await pageFetch(page, src, { cache: 'no-store' })).status, src).toBe(200);
    }
    await expect.poll(async () => (await shellLibraryKeys(page)).filter(p => MOVED.some(m => p.includes(`/${m}.`))).length).toBe(MOVED.length);

    // With the server unreachable, the worker answers each moved script from its shell (no-store skips the HTTP cache,
    // so the answer cannot come from there). Not a `drop` rule for the library's path: Linux WebKit's network process
    // crashes when requests die on open connections (harness/proxy.mjs), and the page then loses its worker.
    await server.unreachable();
    for (const src of srcs) {
        const answer = await pageFetch(page, src, { cache: 'no-store' });
        expect(answer.status, src).toBe(200);
        expect(answer.type, src).toContain('javascript');
    }

    // Nothing ever asked for a moved module at its old /js/ path.
    const old = (await server.log(start)).filter(e => OLD_PATH.test(e.url.split('?')[0]));
    expect(old).toEqual([]);
});

test('a tab opened on the old script URLs keeps its modules across an enhanced navigation that brings the new ones', async ({ page, world, server }) => {
    const wall = world.walls.alpha;
    await signIn(page, world.users.alice);
    await installWorker(page);
    test.skip(!(await hasPageModules(page)), 'needs the 2.7 page modules');

    // The first document's script URLs all differ from the build's (a query on each), as a document rendered by the
    // previous deploy does; the next page comes from the build unchanged.
    await server.faults([{ path: '^/walls$', rewrite: ['.js"></script>', '.js?released=1"></script>'] }]);
    const start = await server.mark();
    await page.goto('/walls');
    await controlled(page);
    await settled(page);
    const before = await movedScripts(page);
    expect(before.defined).toEqual(GLOBALS);
    expect(before.srcs.length).toBe(MOVED.length);
    const queries = () => page.evaluate(library => [...document.scripts].filter(s => s.src.includes(library)).map(s => new URL(s.src).search), LIBRARY);
    expect(await queries()).toEqual(MOVED.map(() => '?released=1'));
    await page.evaluate(globals => { window.__bwFirst = globals.map(g => window[g]); }, GLOBALS);
    await server.faults([]);

    await page.evaluate(path => window.Blazor.navigateTo(path), `/walls/${wall.id}`);
    await page.waitForURL(`**/walls/${wall.id}`);
    await expect(page.locator('.wall-carousel')).toBeVisible();

    // The navigation brought the build's own URLs into the document (no query left) ...
    await expect.poll(queries).toEqual(MOVED.map(() => ''));
    // ... in the same document, with the same module objects: nothing ran a second time.
    expect(await page.evaluate(globals => globals.every((g, i) => window[g] === window.__bwFirst[i]), GLOBALS)).toBe(true);
    const old = (await server.log(start)).filter(e => OLD_PATH.test(e.url.split('?')[0]));
    expect(old).toEqual([]);
    expect((await cacheNames(page)).some(n => n.startsWith('bw-a1-'))).toBe(true);
});
