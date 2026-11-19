// The top bar clears the status bar and the Dynamic Island of an installed iPhone 16 Pro, on the Server pages and in
// the offline client (/v/), in portrait and landscape. Playwright cannot produce a real inset, so one is injected
// through the --bw-safe-top token both bars read, and the first content of each bar must sit at or below it.
import { test, expect, signIn } from '../harness/fixtures.mjs';

test.use({ phone: 'iPhone 13' });

const INSET = 59;
const PORTRAIT = { width: 402, height: 874 };
const LANDSCAPE = { width: 874, height: 402 };

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
    });

    test(`the offline client's bar starts below the inset in ${name}, and loads the re-apply script`, async ({ page }) => {
        await page.setViewportSize(size);
        await page.goto('/v/');
        await expect(page.locator('.bw-client-brand')).toBeVisible({ timeout: 30000 });
        expect(await brandTop(page, '.bw-client-brand')).toBeGreaterThanOrEqual(INSET);
        expect(await page.evaluate(() => typeof window.bwSafeArea)).toBe('object');
    });
}
