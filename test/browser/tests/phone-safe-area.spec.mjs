// The iOS safe-area setup of the shell: the markup, the manifest, and the rules that read the insets. Real insets and an
// installed app's display mode cannot be produced in Playwright, so the insets are checked through the tokens and rules
// that carry them, with one injected through the top bar's token.
import { test, expect, signIn } from '../harness/fixtures.mjs';

test.use({ phone: 'iPhone 13' });

function tabbar(page) {
    return page.evaluate(() => {
        const bar = document.querySelector('.bw-tabbar');
        const r = bar.getBoundingClientRect();
        return {
            top: r.top, bottom: r.bottom, left: r.left, width: r.width, innerHeight, innerWidth,
            visible: getComputedStyle(bar).visibility === 'visible' && getComputedStyle(bar).display !== 'none'
        };
    });
}

async function expectPinned(page) {
    const bar = await tabbar(page);
    expect(bar.visible).toBe(true);
    expect(Math.abs(bar.bottom - bar.innerHeight)).toBeLessThanOrEqual(1);
    expect(bar.left).toBe(0);
    expect(bar.width).toBe(bar.innerWidth);
}

test('the shell carries the iOS safe-area setup', async ({ page, world, request }) => {
    await signIn(page, world.users.alice, '/walls');
    await expect(page.locator('meta[name="viewport"]')).toHaveAttribute('content', /viewport-fit=cover/);
    await expect(page.locator('meta[name="apple-mobile-web-app-capable"]')).toHaveAttribute('content', 'yes');
    await expect(page.locator('meta[name="apple-mobile-web-app-status-bar-style"]')).toHaveAttribute('content', 'black-translucent');
    const manifest = await (await request.get('/manifest.webmanifest')).json();
    expect(manifest.display).toBe('standalone');

    // The rules that read the insets, and the installed-app height token, as the browser parsed them.
    const rules = await page.evaluate(() => {
        const out = { tabbarPadding: '', safeTop: '', topbarPadding: [], standaloneHeight: '' };
        const walk = (list, media) => {
            for (const rule of list) {
                if (rule.cssRules && rule.conditionText !== undefined) {
                    walk(rule.cssRules, rule.conditionText);
                    continue;
                }
                if (!rule.style) {
                    continue;
                }
                if (rule.selectorText === '.bw-tabbar' && rule.style.paddingBottom) {
                    out.tabbarPadding = rule.style.paddingBottom;
                }
                if (rule.selectorText === ':root' && rule.style.getPropertyValue('--bw-safe-top')) {
                    out.safeTop = rule.style.getPropertyValue('--bw-safe-top');
                }
                if (rule.selectorText === ':root' && media && media.includes('standalone')) {
                    out.standaloneHeight = rule.style.getPropertyValue('--bw-app-h').trim();
                }
            }
        };
        for (const sheet of document.styleSheets) {
            try { walk(sheet.cssRules, null); } catch (e) { /* cross-origin font sheet */ }
        }
        return out;
    });
    expect(rules.tabbarPadding).toContain('safe-area-inset-bottom');
    expect(rules.safeTop).toContain('safe-area-inset-top');
    expect(rules.standaloneHeight).toBe('100vh');

    // The top bar's ground runs up under the status bar by exactly the inset: inject one through its token.
    const padding = await page.evaluate(() => {
        const bar = document.querySelector('.bw-topbar');
        const before = parseFloat(getComputedStyle(bar).paddingTop);
        document.documentElement.style.setProperty('--bw-safe-top', '47px');
        const after = parseFloat(getComputedStyle(bar).paddingTop);
        document.documentElement.style.removeProperty('--bw-safe-top');
        return after - before;
    });
    expect(padding).toBe(47);

    // Flush with the bottom edge, full width, and its ground continues just below it: the inset plus a few px, never
    // a slab that would cover a form if the bar were displaced.
    await expectPinned(page);
    const under = await page.evaluate(() => {
        const after = getComputedStyle(document.querySelector('.bw-tabbar'), '::after');
        return { bg: after.backgroundColor, h: parseFloat(after.height) };
    });
    expect(under.bg).toBe(await page.evaluate(() => getComputedStyle(document.querySelector('.bw-tabbar')).backgroundColor));
    expect(under.h).toBeLessThanOrEqual(8 + 34);
});

test('an installed app re-applies viewport-fit=cover at start and again at 100, 500 and 1000 ms', async ({ page, world }) => {
    await signIn(page, world.users.alice, '/walls');
    const flips = async standalone => {
        await page.addInitScript(s => {
            Object.defineProperty(navigator, 'standalone', { configurable: true, get: () => s });
            window.__fits = [];
            new MutationObserver(() => {
                const meta = document.querySelector('meta[name="viewport"]');
                window.__fits.push(/viewport-fit=(\w+)/.exec(meta.content)[1]);
            }).observe(document, { subtree: true, attributes: true, attributeFilter: ['content'] });
        }, standalone);
        await page.reload();
        await page.waitForTimeout(1600);
        return page.evaluate(() => ({ fits: window.__fits, now: document.querySelector('meta[name="viewport"]').content }));
    };

    const installed = await flips(true);
    // At least the start and the three re-checks, each an auto -> cover pair, ending on cover.
    expect(installed.fits.filter(f => f === 'auto').length).toBeGreaterThanOrEqual(4);
    expect(installed.fits.filter(f => f === 'auto').length).toBe(installed.fits.filter(f => f === 'cover').length);
    expect(installed.now).toContain('viewport-fit=cover');

    // A rotation re-applies it too.
    const before = await page.evaluate(() => window.__fits.length);
    await page.evaluate(() => window.dispatchEvent(new Event('orientationchange')));
    await page.waitForTimeout(1300);
    expect(await page.evaluate(() => window.__fits.length)).toBeGreaterThan(before);
});

test('a browser tab never touches viewport-fit', async ({ page, world }) => {
    await page.addInitScript(() => {
        window.__fits = [];
        new MutationObserver(() => window.__fits.push(1))
            .observe(document, { subtree: true, attributes: true, attributeFilter: ['content'] });
    });
    await signIn(page, world.users.alice, '/walls');
    await page.waitForTimeout(1300);
    expect(await page.evaluate(() => window.__fits.length)).toBe(0);
    expect(await page.evaluate(() => navigator.standalone)).not.toBe(true);
});
