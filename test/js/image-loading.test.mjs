// Stalled image downloads (image-watchdog.js, image-res.js): run by `node --test test/js/`.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';

const js = name => readFileSync(new URL(`../../src/Blocwerk.Web/wwwroot/js/${name}`, import.meta.url), 'utf8');

/** An <img> the page shows: its attributes, whether the browser has it all, and its rect. */
class FakeImg {
    constructor(src, { complete = false, width = 300, attrs = {} } = {}) {
        this.attrs = { src, ...attrs };
        this.complete = complete;
        this.clientWidth = width;
        this.isConnected = true;
    }

    getAttribute(name) {
        return this.attrs[name] ?? null;
    }

    hasAttribute(name) {
        return Object.hasOwn(this.attrs, name);
    }

    get src() {
        return this.attrs.src;
    }

    set src(value) {
        this.attrs.src = value;
        this.complete = false;
    }

    getBoundingClientRect() {
        return { width: 300, height: 200, top: 10, left: 10, bottom: 210, right: 310 };
    }
}

/**
 * Runs both scripts in one fake page: manual timers and frames, Image objects whose loads the test settles, and an
 * IntersectionObserver the test drives with `see(img, inView)`.
 */
function page(images) {
    const timers = [];
    const frames = [];
    const loads = [];
    const listeners = {};
    let observe = null;
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
            return new Promise((resolve, reject) => {
                this.finish = ok => { this.naturalWidth = ok ? 4000 : 0; this.complete = true; (ok ? resolve : reject)(); };
            });
        }
    }

    class IntersectionObserver {
        constructor(callback) {
            observe = (img, isIntersecting) => callback([{ target: img, isIntersecting }]);
        }

        observe() {}
    }

    const window = {
        innerWidth: 400,
        innerHeight: 800,
        devicePixelRatio: 3,
        addEventListener: () => {},
        requestAnimationFrame: cb => frames.push(cb),
        setInterval: () => 0,
        IntersectionObserver,
    };
    const document = {
        baseURI: 'https://blocwerk.app/',
        visibilityState: 'visible',
        querySelectorAll: () => images,
        addEventListener: (name, cb) => { listeners[name] = cb; },
    };
    const context = {
        window,
        document,
        Image,
        URL,
        Date,
        setTimeout: (cb, ms) => { timers.push({ cb, ms }); return timers.length; },
        clearTimeout: id => { if (timers[id - 1]) { timers[id - 1].cb = null; } },
    };
    window.document = document;
    vm.createContext(context);
    vm.runInContext(js('image-res.js'), context);
    vm.runInContext(js('image-watchdog.js'), context);
    const dog = window.bwImageWatchdog;
    return {
        window,
        document,
        dog,
        loads,
        listeners,
        see: (img, inView = true) => { dog.check(img, 0); observe(img, inView); },
        frame: () => frames.splice(0).forEach(cb => cb()),
        runTimers: () => timers.splice(0).forEach(t => t.cb && t.cb()),
        settle: async () => { for (let i = 0; i < 5; i++) { await Promise.resolve(); } },
        measure: zoom => { window.bwImageRes.refresh(null, zoom); frames.splice(0).forEach(cb => cb()); },
    };
}

test('watchdog: an in-view image still loading after its stall time is asked for again', () => {
    const img = new FakeImg('/api/walls/w/panels/p/photo?w=1280', { attrs: { 'data-bw-res': '' } });
    const p = page([img]);
    p.see(img);
    const stall = p.dog.stallMsFor(img, 0);

    assert.equal(p.dog.check(img, 1), false);
    assert.equal(p.dog.check(img, stall), false);
    assert.equal(p.dog.check(img, stall + 1), true);
    assert.equal(img.getAttribute('src'), '/api/walls/w/panels/p/photo?w=1280&bwretry=1');
});

test('watchdog: a loaded image, and a lazy one the observer says is clipped out of view, are left alone', () => {
    const loaded = new FakeImg('/a.jpg', { complete: true });
    const clipped = new FakeImg('/b.jpg', { attrs: { loading: 'lazy' } });
    const p = page([loaded, clipped]);
    p.see(loaded);
    p.see(clipped, false);

    for (const t of [1, 100000, 1000000]) {
        assert.equal(p.dog.check(loaded, t), false);
        assert.equal(p.dog.check(clipped, t), false);
    }

    assert.equal(loaded.getAttribute('src'), '/a.jpg');
    assert.equal(clipped.getAttribute('src'), '/b.jpg');
});

test('watchdog: each retry waits twice as long, crops start later, and retries stop at the limit', () => {
    const photo = new FakeImg('/p.jpg?w=1280', { attrs: { 'data-bw-res': '' } });
    const crop = new FakeImg('/crop', { attrs: { 'data-bw-watch': '' } });
    const p = page([photo, crop]);
    assert.equal(p.dog.stallMsFor(photo, 1), 2 * p.dog.stallMsFor(photo, 0));
    assert.equal(p.dog.stallMsFor(photo, 2), 4 * p.dog.stallMsFor(photo, 0));
    assert.ok(p.dog.stallMsFor(crop, 0) > p.dog.stallMsFor(photo, 0));

    p.see(photo);
    let t = 1;
    p.dog.check(photo, t);
    for (let n = 0; n < p.dog.MAX_RETRIES; n++) {
        const wait = p.dog.stallMsFor(photo, n);
        assert.equal(p.dog.check(photo, t + wait - 1), false, `retry ${n + 1} not before ${wait} ms`);
        assert.equal(p.dog.check(photo, t + wait + 1), true, `retry ${n + 1} after ${wait} ms`);
        t += wait + 1;
    }

    assert.equal(p.dog.check(photo, t + 100 * p.dog.stallMsFor(photo, 0)), false);
    assert.equal(photo.getAttribute('src'), `/p.jpg?w=1280&bwretry=${p.dog.MAX_RETRIES}`);

    photo.src = '/other.jpg';
    p.dog.check(photo, t);
    assert.equal(p.dog.check(photo, t + p.dog.stallMsFor(photo, 0) + 1), true, 'a new picture counts afresh');
});

test('watchdog: time in a background tab does not count as a stall', () => {
    const img = new FakeImg('/p.jpg?w=1280', { attrs: { 'data-bw-res': '' } });
    const p = page([img]);
    p.see(img);
    p.dog.check(img, 1);

    p.document.visibilityState = 'hidden';
    p.document.visibilityState = 'visible';
    p.listeners.visibilitychange();
    const back = 10 * p.dog.stallMsFor(img, 0);

    assert.equal(p.dog.check(img, back), false, 'the clock starts again on return');
    assert.equal(p.dog.check(img, back + p.dog.stallMsFor(img, 0) + 1), true);
});

test('image-res: a slow upgrade is neither cancelled nor started twice, and still lands', async () => {
    const img = new FakeImg('/api/walls/w/panels/p/photo?w=1280', { complete: true, width: 1000 });
    const p = page([img]);
    p.measure(4);
    assert.equal(p.loads.length, 1);
    const slow = p.loads[0];

    p.runTimers();
    p.measure(4);
    p.measure(5);
    assert.equal(p.loads.length, 1, 'the download in flight is reused');
    assert.equal(slow.src, '/api/walls/w/panels/p/photo', 'and never cancelled');

    slow.finish(true);
    await p.settle();
    assert.equal(img.getAttribute('src'), '/api/walls/w/panels/p/photo');
});

test('image-res: once an upgrade has held the gate too long, a sharper rung may start, and a slower late one never downgrades', async () => {
    const img = new FakeImg('/api/walls/w/panels/p/photo?w=1280', { complete: true, width: 1000 });
    const p = page([img]);
    p.measure(2);
    assert.equal(p.loads.at(-1).src, '/api/walls/w/panels/p/photo?w=2560');
    const mid = p.loads.at(-1);

    p.measure(4);
    assert.equal(p.loads.length, 1, 'gated while the first is outstanding');
    p.runTimers();
    p.measure(4);
    assert.equal(p.loads.length, 2);
    const original = p.loads.at(-1);

    original.finish(true);
    await p.settle();
    mid.finish(true);
    await p.settle();
    assert.equal(img.getAttribute('src'), '/api/walls/w/panels/p/photo');
});

test('image-res: an upgrade that lands after the viewer swapped panels does not bring the old photo back', async () => {
    const img = new FakeImg('/api/walls/w/panels/one/photo?w=1280', { complete: true, width: 1000 });
    const p = page([img]);
    p.measure(4);
    const upgrade = p.loads[0];

    img.attrs.src = '/api/walls/w/panels/two/photo?w=1280';
    upgrade.finish(true);
    await p.settle();

    assert.equal(img.getAttribute('src'), '/api/walls/w/panels/two/photo?w=1280');
});

test('image-res: the watchdog retry marker never leaks into the upgraded url', async () => {
    const img = new FakeImg('/api/walls/w/panels/p/photo?w=1280&bwretry=1', { complete: true, width: 1000 });
    const p = page([img]);
    p.measure(4);
    p.loads[0].finish(true);
    await p.settle();

    assert.equal(img.getAttribute('src'), '/api/walls/w/panels/p/photo');
});
