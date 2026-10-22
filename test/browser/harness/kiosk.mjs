// The kiosk steps the browser tests share: registering the context as the seeded wall's tablet, and acting as a member.
import { expect } from './fixtures.mjs';

export async function registerKiosk(page, token) {
    await page.goto('/oauth-select');
    await page.getByText('Register this device as a wall kiosk').click();
    await page.getByLabel('Kiosk key').fill(token);
    await page.getByRole('button', { name: 'Register kiosk' }).click();
    await page.waitForURL(/\/walls\/[0-9a-f-]{36}/);
}

// The act-as form POST, with the antiforgery token of the member's own kiosk page (a member without a PIN gets a
// confirm form; the numpad form would only add `pin`).
export async function actAs(page, user) {
    await page.goto(`/kiosk/users/${user.id}`);
    const form = page.locator('form[action="/kiosk/act-as"]').first();
    await expect(form).toBeAttached();
    const antiforgery = await form.locator('input[name="__RequestVerificationToken"]').inputValue();
    const response = await page.request.post('/kiosk/act-as', {
        form: { userId: user.id, pin: '', __RequestVerificationToken: antiforgery }
    });
    expect(response.ok()).toBe(true);
}
