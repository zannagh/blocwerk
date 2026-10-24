/*
 * Shared fixtures for the browser tests. Every test gets a fresh browser context (its own Cache Storage, IndexedDB,
 * service worker and cookies); these helpers sign in, wait for the worker, and read the device's caches and the
 * proxy's request log.
 *
 * WebKit runs in a PERSISTENT context (a fresh profile directory per test), as a Safari tab does. In Playwright's
 * ephemeral WebKit contexts, Cache Storage entries written while the origin has no service worker registration are
 * dropped on the next navigation (cache names survive, entries do not), which a real profile does not do.
 */
import { test as base, expect, devices } from '@playwright/test';
import fs from 'node:fs';
import path from 'node:path';
import { outDir, stateFile } from './environment.mjs';

export { expect };

function readState() {
    return JSON.parse(fs.readFileSync(stateFile, 'utf8'));
}

/** The test proxy's control API (proxy.mjs) on its own port, called from the test process. */
function proxyClient(controlURL) {
    const call = async (path, init) => {
        const response = await fetch(controlURL + path, init);
        return response.status === 204 ? null : response.json();
    };
    return {
        mark: async () => (await call('/__pw/mark')).seq,
        log: since => call(`/__pw/log?since=${since || 0}`),
        faults: rules => call('/__pw/faults', { method: 'POST', body: JSON.stringify(rules || []) }),
        /** The server down: open connections closed, connects refused, until the next `faults`. */
        unreachable: () => call('/__pw/unreachable', { method: 'POST' }),
        /** How many requests for `path` (no query) reached the app since `since`. */
        hits: async (since, path) => (await call(`/__pw/log?since=${since || 0}`))
            .filter(e => !e.fault && e.url.split('?')[0] === path).length
    };
}

export const test = base.extend({
    world: async ({}, use) => { await use(readState().world); },
    baseURL: async ({}, use) => { await use(readState().baseURL); },
    server: async ({}, use) => {
        const proxy = proxyClient(readState().controlURL);
        await proxy.faults([]);
        await use(proxy);
        await proxy.faults([]);
    },
    // A device descriptor name (`devices['iPhone 15']`) for the phone layout tests; null is the desktop browser.
    phone: [null, { option: true }],
    context: async ({ browser, browserName, playwright, baseURL, serviceWorkers, phone }, use, testInfo) => {
        const options = { baseURL, serviceWorkers };
        const device = name => {
            const { defaultBrowserType, ...descriptor } = devices[phone || name];
            return descriptor;
        };
        let context;
        let profile = null;
        if (browserName === 'webkit') {
            const safari = device('Desktop Safari');
            fs.mkdirSync(path.join(outDir, 'profiles'), { recursive: true });
            profile = fs.mkdtempSync(path.join(outDir, 'profiles', 'webkit-'));
            context = await playwright.webkit.launchPersistentContext(profile, { ...safari, ...options });
        } else {
            const chrome = device('Desktop Chrome');
            context = await browser.newContext({ ...chrome, ...options });
        }
        await context.tracing.start({ screenshots: true, snapshots: true });
        await use(context);
        const failed = testInfo.status !== testInfo.expectedStatus;
        await context.tracing.stop(failed ? { path: testInfo.outputPath('trace.zip') } : undefined);
        await context.close();
        if (profile) {
            fs.rmSync(profile, { recursive: true, force: true });
        }
    },
    // `true` keeps the 2.7 page modules from booting (window.__bwAssetCacheManual), so a test sees the worker alone.
    workerOnly: [false, { option: true }],
    page: async ({ page, workerOnly }, use) => {
        if (workerOnly) {
            await page.addInitScript(() => { window.__bwAssetCacheManual = true; });
        }
        await use(page);
    }
});

export const assetPath = (wallId, sha, width) => `/api/walls/${wallId}/a/${sha}` + (width ? `/${width}` : '');

export async function signIn(page, user, returnUrl = '/') {
    await page.goto(`/dev/login?userId=${user.id}&returnUrl=${encodeURIComponent(returnUrl)}`);
    if (!returnUrl.startsWith('/__pw/')) {
        await expect(page.locator('meta[name="bw-partition"]')).toHaveAttribute('content', /^u-/);
    }
}

/**
 * Waits until the page is controlled by an activated worker. clients.claim() only takes the documents that exist when
 * the worker activates, so a navigation already in flight at that moment stays uncontrolled for its whole life; such a
 * page is reloaded once.
 */
export async function controlled(page) {
    await page.evaluate(() => navigator.serviceWorker.ready.then(() => undefined));
    const isControlled = () => page.evaluate(() => !!navigator.serviceWorker.controller);
    try {
        await expect.poll(isControlled, { timeout: 3000 }).toBe(true);
    } catch (e) {
        await page.reload();
        await expect.poll(isControlled, { timeout: 10000 }).toBe(true);
    }
}

/** Installs the worker and leaves the page controlled from its first request. */
export async function installWorker(page, url = '/') {
    await page.goto(url);
    await controlled(page);
    await page.reload();
    await controlled(page);
}

export function partitionOf(page) {
    return page.evaluate(async () => (await (await fetch('/api/me/cache-partition', { cache: 'no-store' })).json()).partition);
}

export function cacheNames(page) {
    return page.evaluate(() => caches.keys());
}

/** The `/__bw/` keys (`{sha}[/{width}]`) of every bw-a1 cache, by cache name. */
export function assetKeys(page) {
    return page.evaluate(async () => {
        const out = {};
        for (const name of (await caches.keys()).filter(n => n.startsWith('bw-a1-'))) {
            const requests = await (await caches.open(name)).keys();
            out[name] = requests.map(r => new URL(r.url).pathname.replace(/^\/__bw\//, '')).sort();
        }
        return out;
    });
}

export const allKeys = byCache => Object.values(byCache).flat();

/** A fetch from the page (so through its worker); never throws, a network failure is status 0. */
export function pageFetch(page, url, init) {
    return page.evaluate(async ([u, i]) => {
        try {
            const r = await fetch(u, i || {});
            const body = await r.arrayBuffer();
            return {
                status: r.status, bytes: body.byteLength, type: r.headers.get('content-type'),
                cacheControl: r.headers.get('cache-control'), etag: r.headers.get('etag')
            };
        } catch (e) {
            return { status: 0, error: String(e) };
        }
    }, [url, init || null]);
}

/** True when this build ships the 2.7 page modules (asset-cache.js). Tests of 2.7 skip without them. */
export function hasPageModules(page) {
    return page.evaluate(() => !!(window.bwAssetCache && window.bwAssetLedger));
}

/** Waits for the 2.7 start (wipe decision, manifests) and the background fill it started. */
export async function settled(page) {
    await page.evaluate(async () => {
        await window.bwAssetCache.ready;
        await window.bwAssetCache.filled();
    });
}

export function ledger(page, partition) {
    return page.evaluate(async p => {
        const l = await window.bwAssetLedger.open();
        return { kind: l.kind, meta: await l.getMeta(), walls: await l.wallsOf(p), assets: await l.assetsOf(p) };
    }, partition);
}
