// The worker installs, activates and claims the page; activate deletes only what is the shell's to delete (§6.7).
import { test, expect, controlled, cacheNames } from '../harness/fixtures.mjs';

test.use({ workerOnly: true });

test('the worker installs, activates, claims the page and reports shell v4', async ({ page }) => {
    await page.goto('/');
    await controlled(page);

    const registration = await page.evaluate(async () => {
        const reg = await navigator.serviceWorker.getRegistration('/');
        return { scope: reg.scope, state: reg.active && reg.active.state, script: reg.active && reg.active.scriptURL };
    });
    expect(registration.state).toBe('activated');
    expect(new URL(registration.scope).pathname).toBe('/');
    expect(new URL(registration.script).pathname).toBe('/js/service-worker.js');

    const info = await page.evaluate(() => new Promise((resolve, reject) => {
        const channel = new MessageChannel();
        channel.port1.onmessage = e => resolve(e.data);
        navigator.serviceWorker.controller.postMessage({ proto: 1, type: 'sw-info' }, [channel.port2]);
        setTimeout(() => reject(new Error('no sw-info reply')), 5000);
    }));
    expect(info).toMatchObject({ proto: 1, type: 'sw-info', shell: 'blocwerk-shell-v4' });

    await expect.poll(() => cacheNames(page)).toContain('blocwerk-shell-v4');
});

test('an unknown proto or type gets no reply and breaks nothing', async ({ page }) => {
    await page.goto('/');
    await controlled(page);
    const replies = await page.evaluate(() => new Promise(resolve => {
        const channel = new MessageChannel();
        const got = [];
        channel.port1.onmessage = e => got.push(e.data);
        navigator.serviceWorker.controller.postMessage({ proto: 2, type: 'sw-info' }, [channel.port2]);
        navigator.serviceWorker.controller.postMessage({ proto: 1, type: 'no-such-type' });
        setTimeout(() => resolve(got), 1000);
    }));
    expect(replies).toEqual([]);
});

test('activate keeps bw-a1-* and bw-sw-state, and deletes old shells, bw-a0-* and the probe ladder', async ({ page }) => {
    // A page the worker is not registered from, so the caches exist before the first install and activate.
    await page.goto('/__pw/blank');
    await page.evaluate(async () => {
        for (const name of ['bw-a1-u-aaaaaaaaaaaaaaaaaaaa-img', 'bw-a1-anon-3d', 'bw-sw-state', 'blocwerk-shell-v3',
            'bw-a0-u-old-img', 'bw-storage-probe-ladder', 'bw-storage-probe']) {
            await (await caches.open(name)).put(`/__bw/marker-${name}`, new Response(name));
        }
    });

    await page.goto('/');
    await controlled(page);

    await expect.poll(() => cacheNames(page)).toContain('blocwerk-shell-v4');
    const names = await cacheNames(page);
    expect(names).toEqual(expect.arrayContaining(['bw-a1-u-aaaaaaaaaaaaaaaaaaaa-img', 'bw-a1-anon-3d', 'bw-sw-state', 'bw-storage-probe']));
    expect(names).not.toContain('blocwerk-shell-v3');
    expect(names).not.toContain('bw-a0-u-old-img');
    expect(names).not.toContain('bw-storage-probe-ladder');

    // The kept caches keep their entries, not just their names.
    const kept = await page.evaluate(async () => {
        const hit = await caches.match('/__bw/marker-bw-a1-anon-3d', { cacheName: 'bw-a1-anon-3d' });
        return hit ? hit.text() : null;
    });
    expect(kept).toBe('bw-a1-anon-3d');
});
