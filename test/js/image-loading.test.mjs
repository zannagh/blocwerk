// Stalled image downloads (image-watchdog.js, image-res.js): run by `node --test test/js/`.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';

const js = name => readFileSync(new URL(`../../src/Blocwerk.Web/wwwroot/js/${name}`, import.meta.url), 'utf8');

/** An <img> the page shows: its src, whether the browser has it all, and where it sits on screen. */
class FakeImg {
    constructor(src, { complete = false, visible = true, width = 300 } = {}) {
        this.attrs = { src };
        this.complete = complete;
        this.visible = visible;
        this.clientWidth = width;
        this.isConnected = true;
    }

    getAttribute(name) {
        return this.attrs[name] ?? null;
    }

    get src() {
        return this.attrs.src;
    }

    set src(value) {
        this.attrs.src = value;
        this.complete = false;
    }

    getBoundingClientRect() {
        return this.visible ? { width: 300, height: 200, top: 10, left: 10, bottom: 210, right: 310 } : { width: 0, height: 0, top: 0, left: 0, bottom: 0, right: 0 };
    }
}

/** Runs the scripts in one fake page: manual timers and frames, and Image objects whose loads the test settles. */
function page(images) {
    const timers = [];
    const frames = [];
    const loads = [];
    class Image {
        set src(value) {
            this._src = value;
            if (value) {
                loads.push(this);
            }
        }

        get src() {
            return this._src;
        }

        decode() {
            return new Promise((resolve, reject) => { this.finish = ok => { this.naturalWidth = ok ? 4000 : 0; this.complete = true; (ok ? resolve : reject)(); }; });
        }
    }

    const window = {
        innerWidth: 400,
        innerHeight: 800,
        devicePixelRatio: 3,
        addEventListener: () => {},
        requestAnimationFrame: cb => frames.push(cb),
        setInterval: () => 0,
    };
    const context = {
        window,
        document: { baseURI: 'https://blocwerk.app/', visibilityState: 'visible', querySelectorAll: () => images },
        Image,
        URL,
        Date,
        setTimeout: (cb, ms) => { timers.push({ cb, ms }); return timers.length; },
        clearTimeout: id => { if (timers[id - 1]) { timers[id - 1].cb = null; } },
    };
    context.window.document = context.document;
    vm.createContext(context);
    vm.runInContext(js('image-res.js'), context);
    vm.runInContext(js('image-watchdog.js'), context);
    return {
        window,
        loads,
        frame: () => frames.splice(0).forEach(cb => cb()),
        runTimers: () => timers.splice(0).forEach(t => t.cb && t.cb()),
        settle: async () => { for (let i = 0; i < 5; i++) { await Promise.resolve(); } },
    };
}

test('watchdog: an on-screen image still loading after the stall time is asked for again', () => {
    const img = new FakeImg('/api/walls/w/enhancement/items/i/crop');
    const { window } = page([img]);
    const dog = window.bwImageWatchdog;

    assert.equal(dog.check(img, 0), false);
    assert.equal(dog.check(img, dog.STALL_MS - 1), false);
    assert.equal(dog.check(img, dog.STALL_MS), true);
    assert.equal(img.getAttribute('src'), '/api/walls/w/enhancement/items/i/crop?bwretry=1');
});

test('watchdog: a loaded, an off-screen and a lazy-not-yet-asked image are left alone', () => {
    const loaded = new FakeImg('/a.jpg', { complete: true });
    const offscreen = new FakeImg('/b.jpg', { visible: false });
    const { window } = page([loaded, offscreen]);
    const dog = window.bwImageWatchdog;

    for (const t of [0, dog.STALL_MS, dog.STALL_MS * 3]) {
        assert.equal(dog.check(loaded, t), false);
        assert.equal(dog.check(offscreen, t), false);
    }

    assert.equal(loaded.getAttribute('src'), '/a.jpg');
    assert.equal(offscreen.getAttribute('src'), '/b.jpg');
});

test('watchdog: retries stop after the limit, and a new picture starts counting afresh', () => {
    const img = new FakeImg('/p.jpg?w=1280');
    const { window } = page([img]);
    const dog = window.bwImageWatchdog;
    let t = 0;
    dog.check(img, t);
    for (let n = 1; n <= dog.MAX_RETRIES; n++) {
        t += dog.STALL_MS;
        assert.equal(dog.check(img, t), true);
    }

    t += dog.STALL_MS;
    assert.equal(dog.check(img, t), false);
    assert.equal(img.getAttribute('src'), `/p.jpg?w=1280&bwretry=${dog.MAX_RETRIES}`);

    img.src = '/other.jpg';
    dog.check(img, t);
    assert.equal(dog.check(img, t + dog.STALL_MS), true);
});

test('image-res: an upgrade that stalls stops blocking, so the next measurement asks again', async () => {
    const img = new FakeImg('/api/walls/w/panels/p/photo?w=1280', { complete: true, width: 1000 });
    const p = page([img]);
    p.window.bwImageRes.refresh(null, 4);
    p.frame();
    assert.equal(p.loads.length, 1);

    p.window.bwImageRes.refresh(null, 4);
    p.frame();
    assert.equal(p.loads.length, 1, 'still waiting on the first upgrade');

    p.runTimers();
    p.window.bwImageRes.refresh(null, 4);
    p.frame();
    assert.equal(p.loads.length, 2, 'the stalled load was dropped and a new one started');
    assert.equal(p.loads.at(-1).src, '/api/walls/w/panels/p/photo');
});

test('image-res: an upgrade that lands after the viewer swapped panels does not bring the old photo back', async () => {
    const img = new FakeImg('/api/walls/w/panels/one/photo?w=1280', { complete: true, width: 1000 });
    const p = page([img]);
    p.window.bwImageRes.refresh(null, 4);
    p.frame();
    const upgrade = p.loads[0];

    img.attrs.src = '/api/walls/w/panels/two/photo?w=1280';
    upgrade.finish(true);
    await p.settle();

    assert.equal(img.getAttribute('src'), '/api/walls/w/panels/two/photo?w=1280');
});

test('image-res: the watchdog retry marker never leaks into the upgraded url', async () => {
    const img = new FakeImg('/api/walls/w/panels/p/photo?w=1280&bwretry=1', { complete: true, width: 1000 });
    const p = page([img]);
    p.window.bwImageRes.refresh(null, 4);
    p.frame();
    p.loads[0].finish(true);
    await p.settle();

    assert.equal(img.getAttribute('src'), '/api/walls/w/panels/p/photo');
});
