// The top bar clears the status bar and the Dynamic Island of an installed iPhone 16 Pro, on the Server pages and in
// the offline client (/v/), in portrait and landscape. Playwright cannot produce a real inset, so one is injected
// through the --bw-safe-top token both bars read, and the first content of each bar must sit at or below it.
import path from 'node:path';
import { test, expect, signIn } from '../harness/fixtures.mjs';

test.use({ phone: 'iPhone 13' });

const INSET = 59;
const PORTRAIT = { width: 402, height: 874 };
const LANDSCAPE = { width: 874, height: 402 };

// BW_SHOT_DIR: where to leave a screenshot of each bar, for looking at it.
async function shot(page, name) {
    if (process.env.BW_SHOT_DIR) {
        await page.screenshot({ path: path.join(process.env.BW_SHOT_DIR, `${name}.png`) });
    }
}

async function brandTop(page, selector) {
    return page.evaluate(([sel, inset]) => {
        document.documentElement.style.setProperty('--bw-safe-top', inset + 'px');
        return document.querySelector(sel).getBoundingClientRect().top;
    }, [selector, INSET]);
}

for (const [name, size] of [['portrait', PORTRAIT], ['landscape', LANDSCAPE]]) {
    test(`the Server top bar starts below the inset in ${name}`, async ({ page, world }) => {
        await page.setViewportSize(size);
        await signIn(page, world.users.alice, '/walls');
        expect(await brandTop(page, '.bw-topbar .app-title')).toBeGreaterThanOrEqual(INSET);
        await shot(page, `server-${name}`);
    });

    test(`the offline client's bar starts below the inset in ${name}, and loads the re-apply script`, async ({ page }) => {
        await page.setViewportSize(size);
        await page.goto('/v/');
        await expect(page.locator('.bw-client-brand')).toBeVisible({ timeout: 30000 });
        expect(await brandTop(page, '.bw-client-brand')).toBeGreaterThanOrEqual(INSET);
        expect(await page.evaluate(() => typeof window.bwSafeArea)).toBe('object');
        await shot(page, `offline-${name}`);
    });
}

// The full-screen overlays are positioned against the viewport, not the shell, so each clears the inset itself.
test('the full-screen overlays with their own header clear the top inset', async ({ page, world }) => {
    await signIn(page, world.users.alice, '/walls');
    const missing = await page.evaluate(() => {
        const rules = [];
        for (const sheet of document.styleSheets) {
            try {
                for (const rule of sheet.cssRules) {
                    rules.push(rule);
                }
            } catch { /* a cross-origin sheet */ }
        }
        return ['.wall-lightbox', '.shape-adjust-overlay', '.beta-lightbox', '.reference-overlay']
            .filter(sel => !rules.some(r => r.selectorText === sel && r.cssText.includes('safe-area-inset-top')));
    });
    expect(missing).toEqual([]);
});
