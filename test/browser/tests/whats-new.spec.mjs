// The version in the Info popover links to /whats-new, which lists Blocwerk's GitHub releases. The app reads them from
// the harness's GitHub stub (github-stub.mjs), never the real GitHub; the stub's preview notes carry a <script>.
import { test, expect } from '../harness/fixtures.mjs';

async function openInfo(page) {
    // The popover is interactive: retry the click until the circuit is up and the popover opens. The button toggles,
    // so it is only clicked while the popover is still closed.
    const popover = page.locator('.info-popover');
    await expect(async () => {
        if (!(await popover.isVisible())) {
            await page.getByTitle('Info', { exact: true }).click();
        }
        await expect(popover).toBeVisible({ timeout: 1000 });
    }).toPass();
}

test('the version in the Info popover opens the release notes', async ({ page }) => {
    // Anonymous, from a page with the top bar (the anonymous landing page has none).
    await page.goto('/about');
    await openInfo(page);
    const version = page.locator('.info-popover .info-version');
    const shown = (await version.innerText()).trim();
    expect(shown).toMatch(/^v/);

    await version.click();

    await expect(page).toHaveURL(/\/whats-new$/);
    await expect(page.getByRole('heading', { name: 'What\'s new' })).toBeVisible();
    await expect(page.locator('.whatsnew-running')).toHaveText(shown);
    await expect(page.locator('.whatsnew-release', { hasText: 'Blocwerk 0.1.0' })).toBeVisible();
});

test('the page lists stable releases, the toggle adds prereleases, and notes never run script', async ({ page }) => {
    await page.goto('/whats-new');
    const stable = page.locator('.whatsnew-release[data-tag="v0.1.0"]');
    await expect(stable.getByRole('heading', { name: 'Blocwerk 0.1.0' })).toBeVisible();
    await expect(stable.locator('.whatsnew-notes strong')).toHaveText('Stubbed');
    const docs = stable.locator('.whatsnew-notes a', { hasText: 'the docs' });
    await expect(docs).toHaveAttribute('rel', 'noopener noreferrer');
    await expect(docs).toHaveAttribute('target', '_blank');
    await expect(page.getByRole('link', { name: /All releases on GitHub/ }))
        .toHaveAttribute('href', 'https://github.com/zannagh/blocwerk/releases');

    const running = (await page.locator('.whatsnew-running').innerText()).trim().replace(/^v/, '');
    const preview = page.locator('.whatsnew-release[data-tag="0.1.1-224"]');
    if (running !== '0.1.1-224') {
        await expect(preview).toHaveCount(0);
        await page.getByRole('link', { name: 'Show prereleases (1)' }).click();
        await expect(page).toHaveURL(/\/whats-new\?prereleases=true$/);
    }

    await expect(preview.locator('.whatsnew-badge', { hasText: 'Prerelease' })).toBeVisible();
    await expect(preview.locator('.whatsnew-notes')).toContainText('<script>window.__bwReleaseNotesPwned = true</script>');
    await expect(preview.locator('.whatsnew-notes script')).toHaveCount(0);
    expect(await page.evaluate(() => window.__bwReleaseNotesPwned)).toBeUndefined();
});
