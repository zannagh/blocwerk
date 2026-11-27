// The tab bar marks the tab of the page on screen (.active + aria-current), on a phone and on a desktop window, in both
// colour schemes, after a full load and after in-app navigation, and always offers Tools: the profile has no switch
// that moves it away. BW_SHOTS=<dir> also saves a screenshot per page, size and scheme.
import { test, expect, signIn } from '../harness/fixtures.mjs';

const active = page => page.locator('.bw-tabbar .nav-item.active');

async function expectActive(page, label) {
    await expect(active(page)).toHaveCount(1);
    await expect(active(page)).toHaveText(label);
    await expect(active(page)).toHaveAttribute('aria-current', 'page');
}

async function shoot(page, name) {
    const dir = process.env.BW_SHOTS;
    if (dir) {
        const consent = page.locator('.bw-consent');
        if (await consent.isVisible()) {
            await consent.getByRole('button', { name: 'OK' }).click();
        }
        await page.screenshot({ path: `${dir}/${test.info().project.name}-${name}.png` });
    }
}

const sizes = [
    ['phone', { phone: 'iPhone 13' }, { width: 402, height: 874 }],
    ['desktop', {}, { width: 1440, height: 900 }]
];

for (const [size, options, viewport] of sizes) {
    for (const scheme of ['light', 'dark']) {
        test.describe(`${size} ${scheme}`, () => {
            test.use(options);

            test('the current page\'s tab is active, and Tools is always in the bar', async ({ page, world }) => {
                await page.setViewportSize(viewport);
                await page.emulateMedia({ colorScheme: scheme });
                await signIn(page, world.users.pia, '/profile');
                await expectActive(page, 'Profile');
                await expect(page.locator('.bw-tabbar .nav-item', { hasText: 'Tools' })).toBeVisible();
                await expect(page.getByText('Show Tools in navigation bar')).toHaveCount(0);
                await shoot(page, `${size}-${scheme}-profile`);

                for (const [url, label] of [['/tools', 'Tools'], ['/walls', 'Walls'], ['/profile', 'Profile'], ['/tools', 'Tools']]) {
                    await page.evaluate(u => window.Blazor.navigateTo(u), url);
                    await expect(page).toHaveURL(new RegExp(`${url}$`));
                    await expectActive(page, label);
                }
                await shoot(page, `${size}-${scheme}-tools`);
            });
        });
    }
}
