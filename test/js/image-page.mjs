// The fake page image-res.js and image-watchdog.js run in for the node tests (image-loading, image-res-hashed).
import { readFileSync } from 'node:fs';
import vm from 'node:vm';

const js = name => readFileSync(new URL(`../../src/Blocwerk.Web/wwwroot/js/${name}`, import.meta.url), 'utf8');

/** An <img> the page shows: its attributes, whether the browser has it all, and its rect. */
export class FakeImg {
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
export function page(images) {
    const timers = [];
    const frames = [];
    const loads = [];
    const listeners = {};
    let observe = null;
    const unobserved = [];
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

        removeAttribute(name) {
            if (name === 'src') {
                this._src = undefined;
                this.removed = true;
            }
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

        unobserve(img) {
            unobserved.push(img);
        }
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
    vm.runInContext(js('image-res-url.js'), context);
    vm.runInContext(js('image-res.js'), context);
    vm.runInContext(js('image-watchdog.js'), context);
    const dog = window.bwImageWatchdog;
    return {
        window,
        document,
        dog,
        loads,
        listeners,
        unobserved,
        see: (img, inView = true) => { dog.check(img, 0); observe(img, inView); },
        frame: () => frames.splice(0).forEach(cb => cb()),
        // Fires the pending timers due within `upTo` ms (all of them without it); later ones stay pending.
        runTimers: (upTo = Infinity) => timers.filter(t => t.cb && t.ms <= upTo).forEach(t => { const cb = t.cb; t.cb = null; cb(); }),
        settle: async () => { for (let i = 0; i < 5; i++) { await Promise.resolve(); } },
        measure: zoom => { window.bwImageRes.refresh(null, zoom); frames.splice(0).forEach(cb => cb()); },
    };
}
