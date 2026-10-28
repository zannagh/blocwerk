// Budget gate, part 2 (plan 2026-10-22, PR 3.8): memory with the client plus a splat. The app's own wall3d.js loads
// the phone splat level (250 000 splats, The Attic's first rung) into a page that runs the booted client, and into a
// blank page with no WebAssembly; the difference is what the client costs a phone that shows 3D. The second rung
// (800 000) is recorded too, as the step a phone may climb to.
//
// Two timings, because the runtime's start is a transient: both engines hold 100–200 MiB more for the first seconds
// after boot (compiling the runtime's WebAssembly, tiering up, the first collections) and give most of it back within
// about 20 s. `settled` mounts the splat 20 s after the first render (a 3D view opened from the wall view); `at boot`
// mounts it 1.5 s after (a 3D link opened offline straight into /v/…/3d, PR 3.13), the worst case.
import { test, expect, newGateContext, gateFiles, collectGarbage, browserMemory, pidsNow, withPeakMemory, record, median, mib, gateDir } from './gate-fixtures.mjs';
import { LAUNCH, THRESHOLDS } from './profiles.mjs';
import { splatCount, splatFile } from './spz.mjs';

const READY = 'bw-client-ready';
const ROUNDS = 3;
const settle = page => page.waitForTimeout(1500);
const AT_BOOT_MS = 1500;
const SETTLED_MS = 20000;

// The .NET runtime's linear memory: the client's managed heap and runtime, which no JS heap figure includes.
const wasmHeapBytes = page => page.evaluate(() => {
    const runtime = globalThis.Blazor && globalThis.Blazor.runtime;
    const module = runtime && runtime.Module;
    const view = (module && module.HEAPU8) || (runtime && runtime.localHeapViewU8 && runtime.localHeapViewU8());
    return view ? view.buffer.byteLength : null;
});

async function openPage(browser, baseURL, profile, client) {
    const context = await newGateContext(browser, baseURL, profile);
    const page = await context.newPage();
    if (client) {
        await page.goto('/v/');
        await page.waitForFunction(m => performance.getEntriesByName(m).length === 1, READY, { timeout: 120000 });
    } else {
        await page.goto('/__gate/blank.html');
    }
    return { context, page };
}

// One page in a browser of its own, so its processes are the only ones counted and none is left over from the page
// before: memory once open, the peak while the splat loads and shows, and once it has settled.
async function oneRound(playwright, baseURL, profile, splat, client, waitMs) {
    const before = pidsNow();
    const browser = await playwright[profile.engine].launch(LAUNCH[profile.engine]);
    try {
        const { page } = await openPage(browser, baseURL, profile, client);
        await page.waitForTimeout(waitMs);
        await collectGarbage(page, profile);
        const open = browserMemory(before);
        const wasm = client ? await wasmHeapBytes(page) : null;
        const { result, peak } = await withPeakMemory(before, () => page.evaluate(async count => {
            const { mountSplat } = await import('/__gate/mount-splat.js');
            return mountSplat('/__gate/scene.spz', count);
        }, splatCount(splat)));
        await settle(page);
        await collectGarbage(page, profile);
        const shown = browserMemory(before);
        return { open, peak, shown, wasm, mount: result };
    } finally {
        await browser.close();
    }
}

async function compare(playwright, baseURL, server, profile, count, waitMs) {
    const { file, source } = splatFile(gateDir, count);
    await gateFiles(server, file);
    const rounds = { blank: [], client: [] };
    for (let i = 0; i < ROUNDS; i++) {
        rounds.blank.push(await oneRound(playwright, baseURL, profile, file, false, waitMs));
        rounds.client.push(await oneRound(playwright, baseURL, profile, file, true, waitMs));
    }
    const m = (side, key) => median(rounds[side].map(r => r[key].pageAndGpu));
    return {
        splat: source, splats: splatCount(file), mountedAfterMs: waitMs,
        modes: [...new Set([...rounds.blank, ...rounds.client].map(r => r.mount.mode))],
        tappedAgain: rounds.client.some(r => r.mount.confirmed),
        mountMs: median(rounds.client.map(r => r.mount.ms)),
        blankPeakMiB: mib(m('blank', 'peak')), clientPeakMiB: mib(m('client', 'peak')),
        blankShownMiB: mib(m('blank', 'shown')), clientShownMiB: mib(m('client', 'shown')),
        bootOverheadMiB: mib(m('client', 'open') - m('blank', 'open')),
        peakOverheadMiB: mib(m('client', 'peak') - m('blank', 'peak')),
        shownOverheadMiB: mib(m('client', 'shown') - m('blank', 'shown')),
        wasmHeapMiB: mib(median(rounds.client.map(r => r.wasm || 0)) / 1024)
    };
}

test('memory with the client and a splat', async ({ playwright, baseURL, server, profile }) => {
    test.setTimeout(1800000);
    const settled = await compare(playwright, baseURL, server, profile, 250000, SETTLED_MS);
    const result = { memory250k: settled, memory250kAtBoot: await compare(playwright, baseURL, server, profile, 250000, AT_BOOT_MS) };
    if (!process.env.BW_GATE_SPLAT) {
        result.memory800k = await compare(playwright, baseURL, server, profile, 800000, SETTLED_MS);
    }
    record(profile.name, result);
    console.log(`gate ${profile.name}: ${JSON.stringify(result)}`);

    // A splat that never showed measured nothing: the numbers only count with photo-real on screen.
    expect(settled.modes, 'the splat showed (photo-real) on both pages').toEqual(['photoreal']);
    expect.soft(settled.peakOverheadMiB, 'client overhead at the splat peak, MiB').toBeLessThanOrEqual(THRESHOLDS.clientOverheadMiB);
    expect.soft(settled.shownOverheadMiB, 'client overhead with the splat shown, MiB').toBeLessThanOrEqual(THRESHOLDS.clientOverheadMiB);
    expect.soft(settled.wasmHeapMiB, '.NET linear memory after boot, MiB').toBeLessThanOrEqual(THRESHOLDS.wasmHeapMiB);
});
