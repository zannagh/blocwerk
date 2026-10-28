// The phone-class profiles of the client budget gate (plan 2026-10-22, PR 3.8) and the gate's thresholds.
//
// No real phone is in reach of a test run, so each profile is an emulation, and says which:
//   - android-mid:  Chromium, 4× CPU slowdown (Lighthouse's mobile preset), a Pixel 7 viewport and user agent.
//   - iphone-old:   Chromium, 6× CPU slowdown, an iPhone SE viewport and iOS user agent. Playwright's WebKit can throttle
//                   neither the CPU nor the network, so an "older iPhone" is a slower Chromium plus the engine check below.
//   - webkit-iphone: WebKit with the iPhone 11 descriptor and no throttling: the engine's own memory and start cost.
// The mobile user agents also make wall3d.js pick the phone splat path (prefersLightSplat), as on a device.

/** DevTools' network presets, in bytes per second. */
export const NETWORKS = {
    'fast-4g': { offline: false, latency: 60, downloadThroughput: (9 * 1024 * 1024) / 8, uploadThroughput: (1.5 * 1024 * 1024) / 8 },
    'slow-4g': { offline: false, latency: 150, downloadThroughput: (1.6 * 1024 * 1024) / 8, uploadThroughput: (750 * 1024) / 8 },
};

const PIXEL_UA = 'Mozilla/5.0 (Linux; Android 14; Pixel 7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/141.0.0.0 Mobile Safari/537.36';
const IPHONE_UA = 'Mozilla/5.0 (iPhone; CPU iPhone OS 17_7 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.7 Mobile/15E148 Safari/604.1';

/** How the gate launches each engine (the memory spec starts one browser per measured page). */
export const LAUNCH = { chromium: { args: ['--enable-unsafe-swiftshader'] }, webkit: {} };

export const PROFILES = {
    'android-mid': {
        engine: 'chromium', cpu: 4,
        context: { viewport: { width: 412, height: 915 }, deviceScaleFactor: 2.625, isMobile: true, hasTouch: true, userAgent: PIXEL_UA },
    },
    'iphone-old': {
        engine: 'chromium', cpu: 6,
        context: { viewport: { width: 375, height: 667 }, deviceScaleFactor: 2, isMobile: true, hasTouch: true, userAgent: IPHONE_UA },
    },
    'webkit-iphone': { engine: 'webkit', cpu: null, device: 'iPhone 11' },
};

/**
 * The thresholds. Plan rev 2 names the three measures but no numbers; these are this PR's proposal (to be confirmed):
 *   - bundle: what a cold /v/ boot transfers, Brotli, everything (framework, assemblies, shell, CSS, shared modules).
 *   - warm start: navigation to the first render with the bundle in the browser cache, network throttled to Fast 4G.
 *     This is the user-facing number: the client opens when a gym navigation fails, after PR 3.9 has precached it
 *     in the background, so nobody waits for the cold download.
 *   - cold start: the same from an empty cache on Fast 4G (the precache's own cost, and the worst case).
 *   - memory: what the client adds while wall3d.js shows the phone splat level (250 000 splats), against a page with
 *     no WebAssembly showing the same splat, mounted once the runtime's start has settled (client-memory.spec.mjs);
 *     and the .NET linear memory after boot. The at-boot case is recorded, not gated.
 */
export const THRESHOLDS = {
    bundleBrotliKiB: 2560,
    warmStartMs: { 'android-mid': 3000, 'iphone-old': 4000, 'webkit-iphone': 3000 },
    coldStartFast4gMs: { 'android-mid': 6000, 'iphone-old': 8000 },
    clientOverheadMiB: 64,
    wasmHeapMiB: 48,
};
