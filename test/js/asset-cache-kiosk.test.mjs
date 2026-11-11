// Offline by default: wall data is always cached, 3D only with the viewer's opt-in (prefs.js, off on a fresh device),
// and a kiosk tablet keeps its wall's 3D with no setting, on a tablet and with Save-Data too. Run by `node --test test/js/`.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';
import { sha, KIOSK } from './fixtures/page-sandbox.mjs';
import { W1, memberPage, w1Routes, GALLERY } from './fixtures/asset-pages.mjs';

const IPHONE = 'Mozilla/5.0 (iPhone; CPU iPhone OS 18_7 like Mac OS X) AppleWebKit/605.1.15 Mobile/15E148';

// prefs.js on a page whose cookie jar starts as `cookie`; the jar keeps only the name=value of each write.
function prefs(cookie) {
    const jar = new Map(cookie ? cookie.split(';').map(c => c.trim().split('=')) : []);
    const document = {
        readyState: 'complete',
        addEventListener: () => undefined,
        get cookie() {
            return [...jar].map(([k, v]) => `${k}=${v}`).join('; ');
        },
        set cookie(value) {
            const [pair] = value.split(';');
            const at = pair.indexOf('=');
            jar.set(pair.slice(0, at), pair.slice(at + 1));
        },
    };
    const window = { addEventListener: () => undefined };
    const context = vm.createContext({ window, document, location: { protocol: 'https:', pathname: '/' } });
    vm.runInContext(readFileSync(new URL('../../src/Blocwerk.Web/wwwroot/js/prefs.js', import.meta.url), 'utf8'), context);
    return { prefs: window.bwPrefs, jar };
}

test('3D is an opt-in: off with no cookie or "off", on only once turned on', () => {
    assert.equal(prefs('').prefs.getCache3d(), false);
    assert.equal(prefs('blocwerk-cache-3d=off').prefs.getCache3d(), false);
    assert.equal(prefs('theme=dark; blocwerk-cache-3d=on').prefs.getCache3d(), true);
    const page = prefs('');
    page.prefs.setCache3d(true);
    assert.equal(page.jar.get('blocwerk-cache-3d'), 'on');
    assert.equal(page.prefs.getCache3d(), true);
});

test('a kiosk tablet fills its wall 3D with no opt-in, on a tablet and with Save-Data on', async () => {
    const tablet = memberPage({ label: KIOSK, path: '/', summary: { wallsWith3d: 1, walls: [W1] }, prefs3d: false, ua: IPHONE, connection: { saveData: true } });
    await tablet.boot();
    assert.ok(tablet.assetFetches().some(f => f.path.endsWith(sha('1'))), 'the wall core');
    assert.ok(tablet.assetFetches().some(f => f.path.endsWith(sha('3'))), 'the wall 3D');
    assert.equal(tablet.cache.keep3d(), true);
    assert.equal(tablet.cache.fills3dInBackground(), true);
});

test("a member's phone without the opt-in keeps its walls but no 3D", async () => {
    const phone = memberPage({ prefs3d: false, ua: IPHONE });
    await phone.boot();
    assert.ok(phone.assetFetches().some(f => f.path.endsWith(sha('1'))));
    assert.ok(!phone.assetFetches().some(f => f.path.endsWith(sha('3'))));
    assert.equal(phone.cache.keep3d(), false);
});

test("a member's page with the real prefs.js keeps no 3D without the cookie, and fills it once the cookie says on", async () => {
    const fresh = memberPage({ realPrefs: true });
    await fresh.boot();
    assert.equal(fresh.cache.keep3d(), false);
    assert.ok(fresh.assetFetches().some(f => f.path.endsWith(sha('1'))), 'the wall core');
    assert.ok(!fresh.assetFetches().some(f => f.path.endsWith(sha('3'))), 'no 3D');

    const optedIn = memberPage({ realPrefs: true, cookie: 'theme=dark; blocwerk-cache-3d=on' });
    await optedIn.boot();
    assert.equal(optedIn.cache.keep3d(), true);
    assert.ok(optedIn.assetFetches().some(f => f.path.endsWith(sha('3'))), 'the 3D');
});

test("a kiosk tablet keeps the tablet's image plan: the gallery's 640 grid, never the originals, but its 3D", async () => {
    const tablet = memberPage({ label: KIOSK, path: '/', summary: { wallsWith3d: 1, walls: [W1] }, routes: w1Routes(), ua: IPHONE });
    await tablet.boot();
    const gallery = tablet.assetFetches().filter(f => f.path.includes(GALLERY));
    assert.ok(gallery.length > 0 && gallery.every(f => f.path.endsWith(`${GALLERY}/640`)), JSON.stringify(gallery.map(f => f.path)));
    assert.ok(tablet.assetFetches().some(f => f.path.endsWith(sha('3'))), 'the 3D');
    assert.equal(tablet.cache.fills3dInBackground(), true);
});
