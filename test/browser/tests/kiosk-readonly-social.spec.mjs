// A kiosk tablet resting on its wall (nobody picked) reads a boulder's comments and activity, read-only: no comment box,
// no delete, and only the public part of the log (nothing of a draft or an archived boulder). It names members who
// consented to the kiosk (Cora) and setters on their setting entries (Erin); everyone else (Dave) is "A member", with
// an empty avatar circle. The wall-wide activity card stays members-only. New comments and activity reach it live.
// Acting as a member, the tablet shows names. Seeded by seed/SeedSocial.cs, on the social wall and its own kiosk key.
import { test, expect, signIn } from '../harness/fixtures.mjs';

const PLACEHOLDER = 'A member';

async function registerKiosk(page, token) {
    await page.goto('/oauth-select');
    await page.getByText('Register this device as a wall kiosk').click();
    await page.getByLabel('Kiosk key').fill(token);
    await page.getByRole('button', { name: 'Register kiosk' }).click();
    await page.waitForURL(/\/walls\/[0-9a-f-]{36}/);
}

// The act-as form POST, as in sw-kiosk.spec.mjs (a member without a PIN gets a confirm form).
async function actAs(page, user) {
    await page.goto(`/kiosk/users/${user.id}`);
    const form = page.locator('form[action="/kiosk/act-as"]').first();
    await expect(form).toBeAttached();
    const antiforgery = await form.locator('input[name="__RequestVerificationToken"]').inputValue();
    const response = await page.request.post('/kiosk/act-as', {
        form: { userId: user.id, pin: '', __RequestVerificationToken: antiforgery }
    });
    expect(response.ok()).toBe(true);
}

// Counts the comment boxes the page has shown: the prerendered one, then the one the circuit's first render puts in its
// place. Installed before the document loads, so the prerendered box is always the first one counted.
function countCommentBoxes() {
    window.__bwCommentBoxes = 0;
    const seen = new WeakSet();
    const scan = () => {
        const box = document.querySelector('.comment-input');
        if (box && !seen.has(box)) {
            seen.add(box);
            window.__bwCommentBoxes++;
        }
    };
    new MutationObserver(scan).observe(document, { childList: true, subtree: true });
}

const boulderUrl = (world, id) => `/walls/${world.social.wallId}/boulders/${id}`;
const commentRow = (page, text) => page.locator('.comments-section .comment-row', { hasText: text });

test('the resting kiosk gets no wall-wide activity card', async ({ page, world }) => {
    await registerKiosk(page, world.social.kioskToken);
    await page.goto(`/walls/${world.social.wallId}`);

    const card = page.locator('.settings-card').filter({ has: page.locator('h2', { hasText: 'Activity' }) });
    await expect(card).toContainText("Log in to see this wall's activity.");
    await expect(card.locator('.activity-row')).toHaveCount(0);
});

test('the resting kiosk reads a boulder read-only, naming consenting members and the setter only', async ({ page, world }) => {
    await registerKiosk(page, world.social.kioskToken);
    await page.goto(boulderUrl(world, world.social.boulder));

    const comments = page.locator('.comments-section');
    await expect(comments.locator('.comment-input')).toHaveCount(0);
    await expect(comments.locator('.comment-submit')).toHaveCount(0);
    await expect(comments.locator('.delete-btn')).toHaveCount(0);
    await expect(commentRow(page, world.social.comment).locator('.comment-author')).toHaveText('Cora');
    const dave = commentRow(page, world.social.unconsentedComment);
    await expect(dave.locator('.comment-author')).toHaveText(PLACEHOLDER);
    await expect(dave.locator('.avatar')).toHaveText('');
    await expect(dave.locator('img')).toHaveCount(0);
    await expect(page.locator('.bw-grade-proposal-notice')).toContainText(`has been proposed by ${PLACEHOLDER}`);
    await expect(page.locator('body')).not.toContainText('Dave');

    const activity = page.locator('.activity-feed');
    await expect(activity).toContainText('Cora sent this');
    await expect(activity).toContainText(`${PLACEHOLDER} flashed this`);
    await expect(activity).toContainText('Erin created this');
});

test('the resting kiosk gets new comments and activity live, still without the name', async ({ page, browser, baseURL, world }) => {
    await registerKiosk(page, world.social.kioskToken);
    await page.goto(boulderUrl(world, world.social.live));
    await expect(page.locator('.activity-feed .activity-row').first()).toBeAttached();

    // Dave comments from his own phone; the tablet's circuit gets the comment and the activity entry from the feed.
    const phone = await browser.newContext({ baseURL });
    try {
        const dave = await phone.newPage();
        await dave.addInitScript(countCommentBoxes);
        await signIn(dave, world.users.dave, boulderUrl(world, world.social.live));
        // Typed into the prerendered box, the text is gone once the circuit's first render replaces it, and Post then
        // sends nothing. That raced on WebKit (about 3 runs in 10, on main as well). Type into the interactive one.
        await expect.poll(() => dave.evaluate(() => window.__bwCommentBoxes)).toBeGreaterThanOrEqual(2);
        const text = `Live comment ${Date.now()}`;
        await dave.locator('.comment-input').fill(text);
        await dave.locator('.comment-submit').click();
        await expect(dave.locator('.comments-section')).toContainText(text);

        await expect(commentRow(page, text).locator('.comment-author')).toHaveText(PLACEHOLDER);
        await expect(page.locator('.activity-feed .activity-row').first()).toContainText(`${PLACEHOLDER} commented`);
        await expect(page.locator('body')).not.toContainText('Dave');
    } finally {
        await phone.close();
    }
});

test('a deleted attempt leaves the activity of the tablet and of the member, live and on reload', async ({ page, browser, baseURL, world }) => {
    await registerKiosk(page, world.social.kioskToken);
    await page.goto(boulderUrl(world, world.social.live));
    const tablet = page.locator('.activity-feed');
    await expect(tablet.locator('.activity-row').first()).toBeAttached();

    const phone = await browser.newContext({ baseURL });
    try {
        const cora = await phone.newPage();
        await signIn(cora, world.users.cora, boulderUrl(world, world.social.live));
        await cora.locator('.attempt-buttons .attempt-btn.send').first().click();
        await expect(tablet).toContainText('Cora sent this');
        const own = cora.locator('.activity-feed');
        await expect(own).toContainText('Cora sent this');

        await cora.locator('.history-section .attempt-row', { hasText: 'Send' }).locator('.delete-btn').first().click();
        await expect(tablet).not.toContainText('Cora sent this');
        await expect(own).not.toContainText('Cora sent this');

        await page.reload();
        await expect(tablet.locator('.activity-row').first()).toBeAttached();
        await expect(tablet).not.toContainText('Cora sent this');
        await cora.reload();
        await expect(own.locator('.activity-row').first()).toBeAttached();
        await expect(own).not.toContainText('Cora sent this');
    } finally {
        await phone.close();
    }
});

test('acting as a member, the tablet shows the names that member sees', async ({ page, world }) => {
    await registerKiosk(page, world.social.kioskToken);
    await actAs(page, world.users.cora);
    await page.goto(boulderUrl(world, world.social.boulder));

    await expect(commentRow(page, world.social.unconsentedComment).locator('.comment-author')).toHaveText('Dave');
    await expect(page.locator('.activity-feed')).toContainText('Dave flashed this');
    await expect(page.locator('.bw-grade-proposal-notice')).toContainText('has been proposed by Dave');
});

test('the resting kiosk reads nothing of an archived boulder', async ({ page, world }) => {
    await registerKiosk(page, world.social.kioskToken);
    await page.goto(boulderUrl(world, world.social.archived));

    await expect(page.locator('.comments-section')).toContainText('No comments yet.');
    await expect(page.locator('.comments-section')).not.toContainText('Old beta');
    await expect(page.locator('.activity-feed')).toContainText('No activity yet.');
});
