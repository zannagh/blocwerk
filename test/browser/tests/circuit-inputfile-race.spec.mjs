// An enhanced navigation away from a page that is still loading must not kill the circuit (prod 2026-10-22, circuit
// m2y_Yk2L). The page's late render puts <InputFile>s into DOM the navigation has already swapped out; InputFile's
// first-render init then resolves its element with document.querySelector, gets null, throws "Cannot set properties
// of null (setting '_blazorInputFileNextFileId')", and the unhandled after-render exception terminated the circuit.
//
// The window is forced through the Blazor WebSocket: server→client frames are held while the page renders, the user
// navigates away, the held frames are then delivered into the detached DOM, and the client's "drop that root
// component" update is held back until the server has run the inputs' after-render init. Without SafeInputFile this
// shows the error banner and leaves /walls without a circuit.
import { test, expect, signIn } from '../harness/fixtures.mjs';

/** Forwards the Blazor socket and can hold either direction; `up` holds only root-component updates. */
async function controllableCircuit(page) {
    const state = { holdDown: false, holdRootUpdates: false, down: [], up: [], toPage: null, toServer: null };
    await page.routeWebSocket(/\/_blazor\?id=/, ws => {
        const server = ws.connectToServer();
        state.toPage = m => ws.send(m);
        state.toServer = m => server.send(m);
        server.onMessage(m => (state.holdDown ? state.down.push(m) : ws.send(m)));
        ws.onMessage(m => {
            const text = typeof m === 'string' ? m : m.toString('latin1');
            if (state.holdRootUpdates && text.includes('UpdateRootComponents')) {
                state.up.push(m);
            } else {
                server.send(m);
            }
        });
    });
    return {
        holdDown: () => { state.holdDown = true; },
        releaseDown: () => { state.holdDown = false; state.down.splice(0).forEach(state.toPage); },
        holdRootUpdates: () => { state.holdRootUpdates = true; },
        releaseRootUpdates: () => { state.holdRootUpdates = false; state.up.splice(0).forEach(state.toServer); }
    };
}

const errorBannerShown = page => page.evaluate(() => document.getElementById('blazor-error-ui')?.style.display === 'block');

test('leaving a page whose file inputs render after the DOM swap keeps the circuit alive', async ({ page, world, browserName }) => {
    test.skip(browserName !== 'chromium', 'the frame timing is engine-independent; one engine is enough');
    const circuit = await controllableCircuit(page);
    await signIn(page, world.users.alice, '/walls');
    await expect(page.getByRole('heading', { name: 'My Walls' })).toBeVisible();
    await page.waitForTimeout(1500);

    // The profile renders its avatar <InputFile> once its data has loaded; that batch is held.
    circuit.holdDown();
    await page.evaluate(() => window.Blazor.navigateTo('/profile'));
    await expect(page.locator('.profile-page')).toBeVisible();
    await page.waitForTimeout(1500);

    circuit.holdRootUpdates();
    await page.evaluate(() => window.Blazor.navigateTo('/walls'));
    await expect(page.getByRole('heading', { name: 'My Walls' })).toBeVisible();
    await expect(page.locator('#avatar-file')).toHaveCount(0);

    circuit.releaseDown();
    await page.waitForTimeout(2000);
    circuit.releaseRootUpdates();
    await page.waitForTimeout(1000);

    expect(await errorBannerShown(page)).toBe(false);
    expect(await page.evaluate(() => window.blocwerkCircuitFailed())).toBe(false);

    // Still interactive: an enhanced navigation back to the profile gets its avatar input from the same circuit.
    await page.evaluate(() => window.Blazor.navigateTo('/profile'));
    await expect(page.locator('#avatar-file')).toHaveCount(1);
    await page.waitForTimeout(1000);
    expect(await errorBannerShown(page)).toBe(false);
});

test('after the circuit has failed, the next link is a full page load with a new circuit', async ({ page, world, browserName }) => {
    test.skip(browserName !== 'chromium', 'exercises circuit-failed.js, which has no engine-specific path');
    await signIn(page, world.users.alice, '/walls');
    await expect(page.getByRole('heading', { name: 'My Walls' })).toBeVisible();
    await page.waitForTimeout(1500);

    // What Blazor does on the server's JS.Error: show the banner; the connection stays 'connected' in our books.
    await page.evaluate(() => { document.getElementById('blazor-error-ui').style.display = 'block'; });
    await expect.poll(() => page.evaluate(() => window.blocwerkCircuitFailed())).toBe(true);

    // Back between two hash fragments of the same page is not a page change and must not reload. (Back to an entry
    // Blazor stamped with state is different: Blazor itself re-fetches that page, and the enhancedload reload follows.)
    await page.evaluate(() => { window.__bwSamePage = true; location.hash = 'one'; location.hash = 'two'; });
    await page.evaluate(() => new Promise(resolve => {
        window.addEventListener('popstate', () => setTimeout(resolve, 500), { once: true });
        history.back();
    }));
    expect(await page.evaluate(() => window.__bwSamePage === true)).toBe(true);

    // A wall card, not the tab bar: the banner sits over the bottom bar on narrow layouts.
    await page.locator('a.wall-card').first().click();
    await expect(page).toHaveURL(new RegExp(`/walls/[0-9a-f-]{36}$`));
    await expect.poll(() => page.evaluate(() => window.__bwSamePage === undefined)).toBe(true);
    expect(await errorBannerShown(page)).toBe(false);
});
