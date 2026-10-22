// The 401 lock of the page cache (Phase 3 PR 3.5, asset-lock.js with asset-session.js and asset-wipe.js) against the
// real page modules: an expired session locks the `u-` partition instead of wiping it; it survives anonymous pages (the
// login page, a share link, both sweeps) hidden, for up to 7 days; the same user unlocks it with the data intact;
// another identity, logout, `forced`, `gone`, Clear offline data and day 8 wipe it; a running page's 401 `expired` locks
// and any other 401 or 410 (manifest, docs index, marks) wipes; kiosk and anonymous partitions never lock; a degraded
// page keeps the lock from the identity mirror. Run by `node --test test/js/`.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { loadPage, fakeCaches, json, USER_A, USER_B, KIOSK } from './fixtures/page-sandbox.mjs';
import { W1, W2, memberPage } from './fixtures/asset-pages.mjs';

const DAY = 24 * 60 * 60 * 1000;
const MARKER = '/__bw/locked';
const mineBody = wallId => ({ v: 1, wallId, partition: USER_A, cursor: '1.2.3', ticks: [], favourites: [] });
const mineRoutes = { [`/api/me/walls/${W1}/mine`]: () => json(mineBody(W1)), [`/api/me/walls/${W2}/mine`]: () => json(mineBody(W2)) };
const refusal = (status, reason) => () => new Response('', { status, headers: reason ? { 'x-blocwerk-signout': reason } : {} });

function banner() {
    const button = { dataset: {}, addEventListener: (type, fn) => { button.click = fn; } };
    return { hidden: true, button, querySelector: () => button };
}

// Alice's device after a signed-in page: both walls, their assets and marks cached under USER_A.
async function signedIn(more = {}) {
    const device = { caches: fakeCaches(), storage: new Map(), banner: banner() };
    const page = memberPage(Object.assign({ user: 'alice', caches: device.caches, storage: device.storage, banner: device.banner }, more, {
        routes: Object.assign({}, mineRoutes, more.routes || {})
    }));
    await page.boot();
    device.ledger = await page.ledger();
    device.page = page;
    assert.ok(await holds(device, USER_A), 'alice\'s data is cached');
    return device;
}

// The next page load on the same device (its ledger, caches, localStorage and banner).
async function next(device, opts) {
    const page = loadPage(Object.assign({ swReply: 'none', ledger: device.ledger, caches: device.caches, storage: device.storage, banner: device.banner }, opts));
    await page.boot();
    return page;
}

const anonPage = (device, more = {}) => next(device, Object.assign({ label: 'anon', path: '/oauth-select', signOut: 'expired' }, more));

async function holds(device, label) {
    const caches = [...device.caches.stores.keys()].some(name => name === `bw-a1-${label}-img` || name === `bw-a1-${label}-3d`);
    const walls = (await device.ledger.wallsOf(label)).length > 0;
    const mine = (await device.ledger.mineOf(label)).length;
    return caches && walls ? { mine } : null;
}

const meta = device => device.ledger.getMeta();
const marker = device => device.caches.stores.get('bw-sw-state')?.get(MARKER);
const mirror = device => JSON.parse(device.storage.get('blocwerk-asset-identity') || '{}');

test('plan(): the lock after a page load, event by event', () => {
    const { bwAssetLock: Lock } = loadPage().win;
    const now = 1e12;
    const lock = { label: USER_A, since: now - DAY };
    const plain = value => JSON.parse(JSON.stringify(value));
    const p = (previous, stored, label, reason, userChanged = false) => plain(Lock.plan(previous, stored, { label, reason, userChanged }, now));
    assert.deepEqual(p(USER_A, null, 'anon', 'expired'), { lock: { label: USER_A, since: now }, drop: null, unlocked: false }, 'expiry locks');
    assert.deepEqual(p('anon', lock, 'anon', 'expired'), { lock, drop: null, unlocked: false }, 'an anon page keeps it');
    assert.deepEqual(p('anon', lock, USER_A, null), { lock: null, drop: null, unlocked: true }, 'the same label unlocks');
    for (const [label, reason] of [[USER_B, null], [KIOSK, null], ['none', null], [null, null], ['anon', 'forced'], ['anon', 'gone'], ['anon', null]]) {
        assert.deepEqual(p('anon', lock, label, reason), { lock: null, drop: USER_A, unlocked: false }, `${label} ${reason} wipes it`);
    }
    assert.deepEqual(p(USER_A, null, 'anon', 'forced'), { lock: null, drop: null, unlocked: false }, 'forced never locks');
    assert.deepEqual(p('anon', lock, 'offline', null), { lock, drop: null, unlocked: false });
    assert.deepEqual(p('anon', lock, 'unknown', null), { lock, drop: null, unlocked: false });
    assert.deepEqual(p('anon', lock, 'unknown', null, true), { lock: null, drop: USER_A, unlocked: false });
    const old = { label: USER_A, since: now - 7 * DAY };
    for (const label of ['anon', 'offline', 'unknown', USER_A]) {
        assert.deepEqual(p('anon', old, label, 'expired'), { lock: null, drop: USER_A, unlocked: false }, `day 7 is the end, on ${label} too`);
    }
    assert.deepEqual(p(KIOSK, null, 'anon', 'expired'), { lock: null, drop: null, unlocked: false }, 'a kiosk never locks');
    assert.deepEqual(p('anon', null, 'anon', 'expired'), { lock: null, drop: null, unlocked: false }, 'anon never locks');
    assert.deepEqual(p(USER_A, { label: USER_B, since: now }, 'anon', 'expired').drop, USER_B, 'one lock at most');
});

test('an expired session locks the partition: kept on the login page, hidden, the worker told, the banner shown', async () => {
    const device = await signedIn();
    const login = await anonPage(device);
    const stored = await meta(device);
    assert.equal(stored.activePartition, 'anon');
    assert.equal(stored.locked.label, USER_A);
    assert.deepEqual(await holds(device, USER_A), { mine: 2 }, 'walls, assets and marks are kept');
    assert.equal(await marker(device).clone().text(), USER_A, 'the worker\'s marker names the locked label');
    assert.equal(mirror(device).locked.label, USER_A, 'the mirror carries the lock');
    assert.equal(device.banner.hidden, false, 'the sign-in banner shows');
    assert.ok(login.posted.some(m => m && m.type === 'sw-refresh-partition'), 'the worker is told');
    assert.equal(login.cache.state().active, 'anon', 'the page caches under anon, never the locked label');
    assert.equal((await login.cache.state().env.ledger.wallsOf('anon')).length, 0);
});

test('the lock survives anonymous browsing: a share link (both sweeps) and a tokenless anon page (rule 6)', async () => {
    const device = await signedIn();
    await anonPage(device);
    await anonPage(device, { path: `/walls/${W1}/shared/tok`, routes: { [`/api/walls/${W1}/manifest`]: () => json({ v: 1, wallId: W1, sha: 'w', assets: [] }) } });
    assert.ok(await holds(device, USER_A), 'after the share link');
    await anonPage(device, { path: '/' });
    await anonPage(device, { path: '/', signOut: 'expired', label: 'anon' });
    assert.ok(await holds(device, USER_A), 'after rule 6');
    assert.equal((await meta(device)).locked.label, USER_A);
});

test('the same user signing in again unlocks it with the data intact, and the banner goes', async () => {
    const device = await signedIn();
    await anonPage(device);
    const back = memberPage({ user: 'alice', ledger: device.ledger, caches: device.caches, storage: device.storage, banner: device.banner, routes: mineRoutes });
    await back.boot();
    const stored = await meta(device);
    assert.equal(stored.activePartition, USER_A);
    assert.equal(stored.locked, null);
    assert.deepEqual(await holds(device, USER_A), { mine: 2 });
    assert.equal(marker(device), undefined, 'the marker is gone');
    assert.equal(mirror(device).locked, null);
    assert.equal(device.banner.hidden, true);
    assert.equal(back.cache.state().active, USER_A);
});

test('another user, a kiosk, logout, the signed-out page and Clear offline data wipe a locked partition at once', async () => {
    const cases = {
        'another user': device => next(device, { label: USER_B, user: 'bob', path: '/' }),
        'a kiosk': device => next(device, { label: KIOSK, path: '/' }),
        'none': device => next(device, { label: 'none', path: '/' }),
        'forced': device => anonPage(device, { signOut: 'forced' }),
        'gone': device => anonPage(device, { signOut: 'gone' }),
        'no reason': device => anonPage(device, { signOut: null }),
        'the signed-out page': async device => { const p = await anonPage(device); await p.win.bwAssetWipe.signedOut(); },
        'Clear offline data': async device => { await anonPage(device); await device.banner.button.click(); await new Promise(r => setTimeout(r, 20)); },
    };
    for (const [name, act] of Object.entries(cases)) {
        const device = await signedIn();
        await anonPage(device);
        await act(device);
        assert.equal(await holds(device, USER_A), null, `${name}: the data goes`);
        assert.equal((await device.ledger.mineOf(USER_A)).length, 0, `${name}: the marks go`);
        assert.equal((await meta(device)).locked ?? null, null, `${name}: the lock goes`);
        assert.equal(marker(device), undefined, `${name}: the worker marker goes`);
    }
});

test('a forced or gone sign-out after a signed-in page wipes at once and never locks', async () => {
    for (const reason of ['forced', 'gone']) {
        const device = await signedIn();
        await anonPage(device, { signOut: reason });
        assert.equal(await holds(device, USER_A), null, reason);
        assert.equal((await meta(device)).locked ?? null, null);
        assert.equal(device.banner.hidden, true);
    }
});

test('day 8 wipes a lock on any page, offline.html included; day 6 keeps it', async () => {
    for (const [age, kept] of [[6 * DAY, true], [8 * DAY, false]]) {
        for (const opts of [{}, { label: 'offline', signOut: null }]) {
            const device = await signedIn();
            await anonPage(device);
            await device.ledger.setMeta({ locked: { label: USER_A, since: Date.now() - age } });
            await anonPage(device, opts);
            assert.equal(!!(await holds(device, USER_A)), kept, `${age / DAY} days, ${opts.label || 'anon'}`);
        }
    }
});

test('a running page\'s 401 expired (manifest, docs index, marks) locks; it stops caching at once', async () => {
    for (const path of [`/api/walls/${W1}/manifest`, `/api/walls/${W1}/docs`, `/api/me/walls/${W1}/mine`]) {
        const device = await signedIn();
        device.page.win.routes[path] = refusal(401, 'expired');
        await device.page.cache.manifestChanged(W1);
        await device.page.settle();
        const stored = await meta(device);
        assert.deepEqual([stored.activePartition, stored.locked && stored.locked.label], ['anon', USER_A], path);
        assert.deepEqual(await holds(device, USER_A), { mine: 2 }, `${path}: nothing is wiped`);
        assert.equal(device.page.cache.state().active, 'none', `${path}: the page stops`);
        assert.equal(await marker(device).clone().text(), USER_A);
        assert.equal(device.banner.hidden, false);
    }
});

test('a running page\'s 401 forced or gone, or a 410, wipes the partition (marks included) across manifest, docs and mine', async () => {
    const paths = [`/api/walls/${W1}/manifest`, `/api/walls/${W1}/docs`, `/api/me/walls/${W1}/mine`];
    for (const path of paths) {
        for (const [status, reason] of [[401, 'forced'], [401, 'gone'], [401, null], [410, null], [410, 'expired']]) {
            const device = await signedIn();
            device.page.win.routes[path] = refusal(status, reason);
            await device.page.cache.manifestChanged(W1);
            await device.page.settle();
            const what = `${path} ${status} ${reason}`;
            assert.equal(await holds(device, USER_A), null, what);
            assert.equal((await device.ledger.mineOf(USER_A)).length, 0, `${what}: no mine row`);
            assert.equal((await meta(device)).locked ?? null, null, `${what}: no lock`);
            assert.ok(device.page.events.includes('bw:identity-changed'), `${what}: the identity change is announced`);
        }
    }
});

test('a stale tab (the ledger names another label already) only stops on a 401', async () => {
    const device = await signedIn();
    await device.ledger.setMeta({ activePartition: USER_B });
    device.page.win.routes[`/api/walls/${W1}/manifest`] = refusal(401, 'forced');
    await device.page.cache.manifestChanged(W1);
    await device.page.settle();
    assert.ok(await holds(device, USER_A), 'nothing wiped by a tab that no longer owns the ledger');
    assert.equal(device.page.cache.state().active, 'none');
});

test('kiosk and anonymous partitions never lock: an expired anon page after a kiosk wipes it, a 401 there wipes it', async () => {
    const caches = fakeCaches();
    const tablet = loadPage({ label: KIOSK, caches, path: `/walls/${W1}`, summary: { wallsWith3d: 0, walls: [W1] },
        routes: { [`/api/walls/${W1}/manifest`]: () => json({ v: 1, wallId: W1, sha: 'w', assets: [{ sha: 'f'.repeat(64), kind: 'panel', tier: 'core', bytes: 1 }] }) } });
    await tablet.boot();
    const device = { caches, storage: new Map(), ledger: await tablet.ledger(), banner: banner() };
    assert.ok(await holds(device, KIOSK));
    await anonPage(device);
    assert.equal(await holds(device, KIOSK), null);
    assert.equal((await meta(device)).locked ?? null, null);

    const again = loadPage({ label: KIOSK, caches, ledger: device.ledger, path: `/walls/${W1}`, summary: { wallsWith3d: 0, walls: [W1] },
        routes: { [`/api/walls/${W1}/manifest`]: () => json({ v: 1, wallId: W1, sha: 'w', assets: [{ sha: 'f'.repeat(64), kind: 'panel', tier: 'core', bytes: 1 }] }) } });
    await again.boot();
    assert.ok(await holds(device, KIOSK));
    again.win.routes[`/api/walls/${W1}/manifest`] = refusal(401, 'expired');
    await again.cache.manifestChanged(W1);
    await again.settle();
    assert.equal(await holds(device, KIOSK), null, 'a 401 in k- wipes, whatever the reason');
    assert.equal((await meta(device)).locked ?? null, null);
});

test('a degraded page (pending upgrade, refused storage) keeps a locked partition from the mirror, and wipes it for another user', async () => {
    const device = await signedIn();
    await anonPage(device);
    const imgCache = `bw-a1-${USER_A}-img`;
    const degraded = opts => loadPage(Object.assign({ swReply: 'none', degraded: true, caches: device.caches, storage: device.storage, banner: device.banner }, opts)).boot();
    await degraded({ label: 'anon', path: '/', signOut: 'expired' });
    assert.ok(device.caches.stores.has(imgCache), 'the every-start sweep of a degraded anon page keeps the locked caches');
    await degraded({ label: 'anon', path: `/walls/${W1}/shared/tok`, signOut: 'expired' });
    assert.ok(device.caches.stores.has(imgCache), 'and so does a share link');
    assert.equal(mirror(device).locked.label, USER_A);
    await degraded({ label: USER_B, user: 'bob', path: '/' });
    assert.equal(device.caches.stores.has(imgCache), false, 'another user on a degraded page wipes the locked caches');
    assert.equal(mirror(device).locked, null);
});

test('a degraded page locks too: the mirror and the worker marker carry it, and the next durable start keeps it', async () => {
    const device = await signedIn();
    await loadPage({ swReply: 'none', degraded: true, caches: device.caches, storage: device.storage, label: 'anon', path: '/', signOut: 'expired' }).boot();
    assert.equal(mirror(device).locked.label, USER_A);
    assert.equal(await marker(device).clone().text(), USER_A);
    assert.ok(device.caches.stores.has(`bw-a1-${USER_A}-img`));
    const since = mirror(device).locked.since;
    await anonPage(device);
    const stored = await meta(device);
    assert.deepEqual([stored.locked.label, stored.locked.since], [USER_A, since], 'the durable ledger adopts the first lock time');
    assert.ok(await holds(device, USER_A));
});
