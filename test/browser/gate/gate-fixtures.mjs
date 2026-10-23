// Fixtures and probes of the client budget gate: a context per phone profile, CPU and network throttling over CDP,
// the process tree's memory, and the results file the report reads.
import { execFileSync } from 'node:child_process';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { devices } from '@playwright/test';
import { test as base, expect } from '../harness/fixtures.mjs';
import { outDir } from '../harness/environment.mjs';
import { NETWORKS, PROFILES } from './profiles.mjs';

export { expect };

export const gateDir = path.join(outDir, 'gate');
export const resultsFile = path.join(gateDir, 'results.json');
const here = path.dirname(fileURLToPath(import.meta.url));

export const test = base.extend({
    gateProfile: ['android-mid', { option: true }],
    profile: async ({ gateProfile }, use) => { await use({ name: gateProfile, ...PROFILES[gateProfile] }); }
});

/**
 * A context shaped like the profile's phone, with an empty cache, so every cold measure starts from nothing. No
 * service worker: PR 3.8 measures the client itself, and the worker's precache (3.9) would blur the cold number.
 * Nothing is routed: Playwright's routing turns the HTTP cache off (the warm start would be a cold one), and WebKit
 * fails large routed bodies.
 */
export async function newGateContext(browser, baseURL, profile) {
    const shape = profile.device ? (({ defaultBrowserType, ...d }) => d)(devices[profile.device]) : profile.context;
    return browser.newContext({ ...shape, baseURL, serviceWorkers: 'block' });
}

/**
 * The /__gate/ files, answered by the test proxy (harness/proxy.mjs fault rules), so they come from the page's own
 * origin and never reach the app: a blank page with no WebAssembly, the wall3d.js mount and the splat under test.
 */
// The client shell's viewport, so both pages lay out (and size the 3D canvas) alike on a mobile profile.
const BLANK = '<!DOCTYPE html><html><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1, viewport-fit=cover">'
    + '<title>gate</title></head><body style="margin:0"></body></html>';

export function gateFiles(server, splat) {
    return server.faults([
        { path: '^/__gate/blank\\.html$', body: BLANK, headers: { 'content-type': 'text/html' } },
        { path: '^/__gate/mount-splat\\.js$', file: path.join(here, 'mount-splat.js'), headers: { 'content-type': 'text/javascript' } },
        { path: '^/__gate/scene\\.spz$', file: splat, headers: { 'content-type': 'application/octet-stream' } }
    ]);
}

/** CPU slowdown and a network preset for the page (Chromium only; WebKit has neither and runs as it is). */
export async function throttle(page, profile, network) {
    if (profile.engine !== 'chromium') {
        return null;
    }
    const cdp = await page.context().newCDPSession(page);
    await cdp.send('Emulation.setCPUThrottlingRate', { rate: profile.cpu });
    await cdp.send('Network.enable');
    await cdp.send('Network.emulateNetworkConditions', network ? NETWORKS[network] : { offline: false, latency: 0, downloadThroughput: -1, uploadThroughput: -1 });
    return cdp;
}

/** A full garbage collection where the engine lets a test ask for one. */
export async function collectGarbage(page, profile) {
    if (profile.engine === 'chromium') {
        const cdp = await page.context().newCDPSession(page);
        await cdp.send('HeapProfiler.collectGarbage');
        await cdp.detach();
    }
}

/** The pids alive now: taken before a browser starts, so browserMemory() can tell that browser's processes apart. */
export const pidsNow = () => new Set(processTable().map(r => r.pid));

function processTable() {
    return execFileSync('ps', ['-axo', 'pid=,ppid=,rss=,command='], { encoding: 'utf8' }).split('\n')
        .map(l => l.trim().match(/^(\d+)\s+(\d+)\s+(\d+)\s+(.*)$/)).filter(Boolean)
        .map(m => ({ pid: +m[1], ppid: +m[2], rss: +m[3], command: m[4] }));
}

/**
 * Resident memory (KiB) of the one browser this test launched, by kind: its page (renderer, WebContent), its GPU
 * process and the rest. Chromium's processes are this worker's descendants. WebKit's are XPC services that launchd
 * starts, so they count when they are Playwright WebKit services that did not exist in `before` (pidsNow()).
 */
export function browserMemory(before) {
    const rows = processTable();
    const mine = new Set([process.pid]);
    let grew = true;
    while (grew) {
        grew = false;
        for (const r of rows) {
            if (!mine.has(r.pid) && mine.has(r.ppid)) {
                mine.add(r.pid);
                grew = true;
            }
        }
    }
    const webkitService = r => !before.has(r.pid) && /ms-playwright\/webkit-.*com\.apple\.WebKit\.(WebContent|GPU|Networking)/.test(r.command);
    const kinds = { page: 0, gpu: 0, other: 0 };
    for (const r of rows.filter(x => x.pid !== process.pid && (mine.has(x.pid) || webkitService(x)))) {
        const kind = /--type=renderer|WebKit\.WebContent/.test(r.command) ? 'page' : /--type=gpu-process|WebKit\.GPU/.test(r.command) ? 'gpu' : 'other';
        kinds[kind] += r.rss;
    }
    return { ...kinds, pageAndGpu: kinds.page + kinds.gpu, total: kinds.page + kinds.gpu + kinds.other };
}

/** Runs `work` while sampling browserMemory(before) every 200 ms; resolves to its result and the peak sample. */
export async function withPeakMemory(before, work) {
    let peak = browserMemory(before);
    const timer = setInterval(() => {
        const now = browserMemory(before);
        if (now.pageAndGpu > peak.pageAndGpu) {
            peak = now;
        }
    }, 200);
    try {
        const result = await work();
        return { result, peak };
    } finally {
        clearInterval(timer);
    }
}

/** Merges `values` into the results file under `profile` (one file for the whole gate run). */
export function record(profile, values) {
    fs.mkdirSync(gateDir, { recursive: true });
    const all = fs.existsSync(resultsFile) ? JSON.parse(fs.readFileSync(resultsFile, 'utf8')) : {};
    all[profile] = { ...(all[profile] || {}), ...values };
    fs.writeFileSync(resultsFile, JSON.stringify(all, null, 2));
}

export const median = xs => [...xs].sort((a, b) => a - b)[Math.floor(xs.length / 2)];
export const mib = kib => Math.round((kib / 1024) * 10) / 10;
