// Budget gate, part 1 (plan 2026-10-22, PR 3.8): what a cold /v/ boot downloads, Brotli as the app ships it, and how
// long the client takes to its first render, cold and warm, on each phone profile (profiles.mjs).
import fs from 'node:fs';
import path from 'node:path';
import { outDir } from '../harness/environment.mjs';
import { test, expect, newGateContext, throttle, record, median } from './gate-fixtures.mjs';
import { THRESHOLDS } from './profiles.mjs';

const RUNS = 3;
const READY = 'bw-client-ready';
const webRoot = path.join(outDir, 'web', 'wwwroot');

async function bootOnce(page) {
    await page.goto('/v/');
    await page.waitForFunction(m => performance.getEntriesByName(m).length === 1, READY, { timeout: 120000 });
    return page.evaluate(m => {
        const resources = performance.getEntriesByType('resource').map(r => ({ url: new URL(r.name).pathname, transfer: r.transferSize, encoded: r.encodedBodySize }));
        const nav = performance.getEntriesByType('navigation')[0];
        return { readyMs: Math.round(performance.getEntriesByName(m)[0].startTime), resources: [{ url: location.pathname, transfer: nav.transferSize, encoded: nav.encodedBodySize }, ...resources] };
    }, READY);
}

// Brotli bytes of every file the boot fetched, from the published app's .br files: what HTTPS browsers get. The wire
// here is plain http, over which WebKit asks for gzip only, so the transferred bytes are recorded but not gated.
function brotliBytes(urls) {
    let total = 0;
    const missing = [];
    for (const url of new Set(urls)) {
        const file = path.join(webRoot, url.endsWith('/') ? `${url}index.html` : url);
        const br = fs.existsSync(`${file}.br`) ? `${file}.br` : null;
        if (br) {
            total += fs.statSync(br).size;
        } else if (fs.existsSync(file)) {
            total += fs.statSync(file).size;
            missing.push(url);
        }
    }
    return { total, uncompressed: missing };
}

async function measure(browser, baseURL, profile, network) {
    const cold = [];
    const warm = [];
    let bundle = null;
    for (let run = 0; run < RUNS; run++) {
        const context = await newGateContext(browser, baseURL, profile);
        const page = await context.newPage();
        await throttle(page, profile, network);
        const first = await bootOnce(page);
        cold.push(first.readyMs);
        bundle = bundle || {
            files: first.resources.length,
            wireKiB: Math.round(first.resources.reduce((t, r) => t + (r.transfer || 0), 0) / 1024),
            brotli: brotliBytes(first.resources.map(r => r.url))
        };
        warm.push((await bootOnce(page)).readyMs);
        await context.close();
    }
    return { cold, warm, bundle };
}

test('bundle and start time', async ({ browser, baseURL, profile }) => {
    test.setTimeout(600000);
    const network = profile.engine === 'chromium' ? 'fast-4g' : null;
    const { cold, warm, bundle } = await measure(browser, baseURL, profile, network);
    const result = {
        engine: profile.engine, cpu: profile.cpu, network: network || 'unthrottled',
        bundleBrotliKiB: Math.round(bundle.brotli.total / 1024), bundleWireKiB: bundle.wireKiB, bundleFiles: bundle.files,
        bundleUncompressed: bundle.brotli.uncompressed,
        coldStartMs: median(cold), coldRunsMs: cold, warmStartMs: median(warm), warmRunsMs: warm
    };
    if (profile.name === 'android-mid') {
        const slow = await measure(browser, baseURL, profile, 'slow-4g');
        result.coldStartSlow4gMs = median(slow.cold);
        result.warmStartSlow4gMs = median(slow.warm);
    }
    record(profile.name, result);
    console.log(`gate ${profile.name}: ${JSON.stringify(result)}`);

    expect.soft(result.bundleBrotliKiB, 'bundle, Brotli KiB').toBeLessThanOrEqual(THRESHOLDS.bundleBrotliKiB);
    expect.soft(result.warmStartMs, 'warm start ms').toBeLessThanOrEqual(THRESHOLDS.warmStartMs[profile.name]);
    if (THRESHOLDS.coldStartFast4gMs[profile.name]) {
        expect.soft(result.coldStartMs, 'cold start on Fast 4G, ms').toBeLessThanOrEqual(THRESHOLDS.coldStartFast4gMs[profile.name]);
    }
});
