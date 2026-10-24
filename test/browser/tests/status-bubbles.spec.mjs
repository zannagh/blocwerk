// The status bubbles (status-bubble.js) in a real browser, in place of the green "offline ready" checkmark that the
// wall list and the wall's title bar used to carry: "Offline mode available" once the walls are on the device (once,
// not on every load), "Switched to offline/online mode" around an outage, and "Uploading …" then a conflict for what
// was queued meanwhile. Every bubble bwStatus agrees to show is recorded in sessionStorage by an init script, so a test
// can count them across reloads however short they stay up: the tests assert on that history, never on a bubble still
// being on screen, and drive the queue with retryNow() instead of waiting out its backoff.
import { test, expect, signIn, installWorker, hasPageModules, settled } from '../harness/fixtures.mjs';

// Wraps bwStatus.show as soon as the page defines it, recording each bubble shown (not the dropped repeats).
async function recordBubbles(page) {
    await page.addInitScript(() => {
        let api;
        Object.defineProperty(window, 'bwStatus', {
            configurable: true,
            get: () => api,
            set: value => {
                const show = value.show;
                value.show = (kind, opts) => {
                    const shown = show(kind, opts);
                    if (shown) {
                        const list = JSON.parse(sessionStorage.getItem('pw-bubbles') || '[]');
                        list.push(value.current().label);
                        sessionStorage.setItem('pw-bubbles', JSON.stringify(list));
                    }
                    return shown;
                };
                api = value;
            }
        });
    });
}

async function signedInWithCircuit(page, user, url) {
    await recordBubbles(page);
    await signIn(page, user, url);
    // Until Blazor.start resolves, a lost connection is not one the framework reconnects, so not one the app can
    // notice: the server goes away only once the circuit runs.
    await expect.poll(() => page.evaluate(() => window.blocwerkConnection.started() && window.blocwerkConnection.status() === 'connected'),
        { timeout: 30000 }).toBe(true);
    // And settled: in a cold WebKit, a connection lost within the first moments after the start raises no reconnect
    // state at all (the framework's, not this app's), so the outage would go unnoticed for a reason the test is not about.
    await page.waitForLoadState('networkidle');
    await page.waitForFunction(() => performance.now() > 3000);
}

const shownBubbles = page => page.evaluate(() => JSON.parse(sessionStorage.getItem('pw-bubbles') || '[]'));
const pill = page => page.locator('.bw-conn-pill');

test('no checkmark on the wall list or the wall; "Offline mode available" once the walls are cached, and only once', async ({ page, world }) => {
    await recordBubbles(page);
    await signIn(page, world.users.alice);
    await installWorker(page, '/walls');
    test.skip(!(await hasPageModules(page)), 'needs the 2.7 page modules');
    await settled(page);

    await expect(page.locator('.wall-card, .wall-list').first()).toBeVisible();
    await expect(page.locator('[data-bw-offline-ready], .bw-offline-ready')).toHaveCount(0);
    await expect.poll(() => shownBubbles(page)).toEqual(['Offline mode available']);

    await page.goto(`/walls/${world.walls.alpha.id}`);
    await settled(page);
    await page.evaluate(() => window.bwAssetReport.readiness.check());
    await expect(page.locator('[data-bw-offline-ready], .bw-offline-ready')).toHaveCount(0);
    expect(await shownBubbles(page)).toEqual(['Offline mode available']);

    await page.reload();
    await settled(page);
    await page.evaluate(() => window.bwAssetReport.readiness.check());
    expect(await shownBubbles(page), 'the same walls ready on the next load say nothing').toEqual(['Offline mode available']);
});

test('an outage: switched to offline mode, then to online mode when the server is back', async ({ page, world, server }) => {
    await signedInWithCircuit(page, world.users.alice, '/walls');

    await server.unreachable();
    await expect.poll(() => shownBubbles(page), { timeout: 30000 }).toEqual(['Switched to offline mode']);

    await server.faults([]);
    await expect.poll(() => shownBubbles(page), { timeout: 30000 }).toEqual(['Switched to offline mode', 'Switched to online mode']);
});

// The framework raises no reconnect state for a connection lost in the circuit's first moments (seen in cold WebKit);
// circuit-watchdog.js reports it. The server goes away the moment the circuit has started, on a fresh page load.
test('a connection lost right after the page load is still told as offline, and leaves offline once the server is back', async ({ page, world, server }) => {
    await recordBubbles(page);
    await page.addInitScript(() => sessionStorage.setItem('pw-loads', String(Number(sessionStorage.getItem('pw-loads') || 0) + 1)));
    await signIn(page, world.users.alice, '/');
    await page.goto('/walls', { waitUntil: 'commit' });
    await page.waitForFunction(() => window.blocwerkConnection && window.blocwerkConnection.started(), null, { polling: 10, timeout: 30000 });
    const loads = await page.evaluate(() => Number(sessionStorage.getItem('pw-loads')));
    await server.unreachable();
    const sinceLoad = await page.evaluate(() => performance.now()).catch(() => null);
    test.info().annotations.push({ type: 'drop', description: `server gone ${Math.round(sinceLoad)} ms after the navigation` });

    // The fill may finish from the device's cache meanwhile, so "Offline mode available" may follow: only "offline" counts.
    await expect.poll(() => shownBubbles(page), { timeout: 30000 }).toContain('Switched to offline mode');
    await server.faults([]);
    // Back: the circuit reconnects ("Switched to online mode"); or the server no longer holds a circuit that never
    // fully started, and the page offers the reload ("Session ended"), which maintenance.js takes by itself. Either
    // way it does not stay offline.
    await expect.poll(async () => {
        const state = await page.evaluate(() => ({
            status: window.blocwerkConnection.status(), loads: Number(sessionStorage.getItem('pw-loads')),
            shown: JSON.parse(sessionStorage.getItem('pw-bubbles') || '[]')
        })).catch(() => null);
        if (!state) {
            return 'navigating';
        }
        if (state.loads > loads || state.status === 'rejected') {
            return 'reloads';
        }
        return state.status === 'connected' && state.shown.includes('Switched to online mode') ? 'online' : state.status;
    }, { timeout: 30000 }).toMatch(/^(online|reloads)$/);
});

// The cookie banner is left up: the pill sits above it, so the conflict is visible and tappable on a fresh device.
test('what was queued during an outage: "Uploading 2 changes", then the refused one as a conflict that survives a reload', async ({ page, world, server }) => {
    await signedInWithCircuit(page, world.users.erin, `/walls/${world.social.wallId}`);

    await server.unreachable();
    await expect.poll(() => shownBubbles(page), { timeout: 30000 }).toEqual(['Switched to offline mode']);
    // A favourite the server takes, and an ascent on a boulder that does not exist (404: given up for good).
    await page.evaluate(([boulder, missing]) => Promise.all([
        window.blocwerkOfflineQueue.enqueue('favorite', { boulderId: boulder, favorite: true }),
        window.blocwerkOfflineQueue.enqueue('attempt', { boulderId: missing, type: 'Send', timestamp: new Date().toISOString() })
    ]), [world.social.boulder, '00000000-0000-4000-8000-00000000beef']);

    // Back, but the offline API still answers 503 (kept, retried later), so nothing is sent before the circuit is up,
    // whichever of the queue's tick and the reconnect comes first.
    await server.faults([{ path: '^/api/offline/', status: 503 }]);
    await expect.poll(() => shownBubbles(page), { timeout: 30000 }).toContain('Switched to online mode');
    // The entries are still backing off from the outage: "Retry now" makes them due, and they meet the 503.
    await page.evaluate(() => window.blocwerkOfflineQueue.retryNow());
    await expect.poll(() => shownBubbles(page), { timeout: 30000 })
        .toEqual(['Switched to offline mode', 'Switched to online mode', 'Uploading 2 changes']);
    await server.faults([]);
    // The failed sends put the entries on a backoff; "Retry now" in the pill's panel skips it.
    await page.evaluate(() => window.blocwerkOfflineQueue.retryNow());

    await expect(pill(page)).toHaveText('1 change could not be uploaded', { timeout: 20000 });
    expect(await shownBubbles(page)).toEqual([
        'Switched to offline mode', 'Switched to online mode', 'Uploading 2 changes', '1 change could not be uploaded'
    ]);
    await expect(page.locator('#bw-status-alert')).toHaveText('1 change could not be uploaded');

    // Kept over a reload (the post-deploy one included) until dismissed, and told again there.
    await page.reload();
    await expect(pill(page)).toHaveText('1 change could not be uploaded');
    await expect(page.locator('.bw-consent')).toBeVisible();
    await pill(page).click();
    const panel = page.locator('.bw-conn-panel');
    await expect(panel.locator('.bw-conn-panel-error')).toHaveCount(1);
    await expect(panel.locator('.bw-conn-panel-error')).toContainText('attempt:');
    await panel.getByRole('button', { name: 'Dismiss' }).click();
    await expect(pill(page)).toHaveCount(0);
});
