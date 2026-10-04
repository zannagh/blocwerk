// Browser checks for the photo-real ladder logic (wwwroot/js/wall3d-splat*.js). Serves wwwroot and a
// ladder of synthetic .spz files from a tiny local server, drives wall3d-harness.html in headless
// Chromium and asserts what the diagnostics endpoint receives. Not part of `dotnet test`.
//
//   npm i playwright && npx playwright install chromium     (anywhere; PLAYWRIGHT_MODULE=<path to its index.mjs>)
//   node tools/splat-ladder/run.mjs
import http from 'node:http';
import fs from 'node:fs';
import path from 'node:path';
import zlib from 'node:zlib';
import { fileURLToPath } from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../../src/Blocwerk.Web/wwwroot');
const { chromium } = await import(process.env.PLAYWRIGHT_MODULE || 'playwright');
const LEVELS = [40000, 120000, 250000, 800000, 2000000, 3002678];   // labels, as on prod; the files stay tiny
const MIME = { '.js': 'text/javascript', '.html': 'text/html', '.json': 'application/json', '.css': 'text/css', '.wasm': 'application/wasm' };
const DAY = 24 * 3600 * 1000;

/** A valid SPZ v2 file of `n` random splats (gzip of header + positions, alphas, colours, scales, rotations). */
function spz(n) {
    const head = Buffer.alloc(16);
    head.writeUInt32LE(0x5053474e, 0);
    head.writeUInt32LE(2, 4);
    head.writeUInt32LE(n, 8);
    head[13] = 12;
    const pos = Buffer.alloc(9 * n);
    const rest = Buffer.alloc(n * 10);
    for (let i = 0; i < n; i++) {
        for (let a = 0; a < 3; a++) {
            const v = Math.round((Math.random() - 0.5) * 4 * 4096);
            for (let b = 0; b < 3; b++) pos[i * 9 + a * 3 + b] = (v >> (8 * b)) & 255;
        }
        rest[i] = 200;
        for (let k = 0; k < 3; k++) rest[n + i * 3 + k] = 128 + Math.floor(Math.random() * 60);
        for (let k = 0; k < 3; k++) rest[4 * n + i * 3 + k] = 100;
        for (let k = 0; k < 3; k++) rest[7 * n + i * 3 + k] = 128;
    }
    return zlib.gzipSync(Buffer.concat([head, pos, rest]));
}

const faults = new Map();            // lod -> 'garbage' | 'abort', consumed once
const reports = [];
const requests = [];
const server = http.createServer((req, res) => {
    const url = new URL(req.url, 'http://x');
    if (url.pathname === '/api/diagnostics/photo-real') {
        let b = '';
        req.on('data', d => { b += d; });
        req.on('end', () => { reports.push(JSON.parse(b)); res.writeHead(204).end(); });
        return;
    }
    if (url.pathname === '/splat.spz') {
        const lod = url.searchParams.get('lod');
        requests.push(lod);
        const fault = faults.get(lod);
        faults.delete(lod);
        const body = fault === 'garbage' ? Buffer.from('not a splat file at all') : spz(1500 + 500 * LEVELS.indexOf(+lod));
        if (fault === 'abort') {        // the connection dies mid-body (a plain reset would be retried by the browser)
            res.writeHead(200, { 'Content-Length': body.length });
            res.write(body.subarray(0, 100));
            setTimeout(() => req.socket.destroy(), 50);
            return;
        }
        res.writeHead(200, { 'Content-Type': 'application/octet-stream', 'Content-Length': body.length }).end(body);
        return;
    }
    if (url.pathname === '/frame.json') {
        res.writeHead(200, { 'Content-Type': 'application/json' });
        res.end(JSON.stringify({ toWorldMm: [[1000, 0, 0, 1500], [0, 1000, 0, 0], [0, 0, 1000, 800], [0, 0, 0, 1]] }));
        return;
    }
    const file = path.join(root, url.pathname);
    if (!file.startsWith(root) || !fs.existsSync(file) || fs.statSync(file).isDirectory()) { res.writeHead(404).end(); return; }
    res.writeHead(200, { 'Content-Type': MIME[path.extname(file)] || 'application/octet-stream' }).end(fs.readFileSync(file));
});
await new Promise(r => server.listen(0, r));
const base = `http://localhost:${server.address().port}`;
const FILE_KEY = `${base}/splat.spz`;
const browser = await chromium.launch({ args: ['--use-gl=angle', '--use-angle=swiftshader', '--enable-unsafe-swiftshader', '--ignore-gpu-blocklist'] });
let failures = 0;
const check = (ok, what, extra = '') => {
    console.log(`${ok ? 'PASS' : 'FAIL'}  ${what} ${extra}`);
    if (!ok) failures++;
};

/** Opens the harness in a fresh browser context (own localStorage) and watches the level until `done`. */
async function session({ seed = null, wait = 90000, done = () => false }) {
    reports.length = 0;
    requests.length = 0;
    const ctx = await browser.newContext({ viewport: { width: 900, height: 600 } });
    const page = await ctx.newPage();
    const errors = [];
    page.on('pageerror', e => errors.push(String(e)));
    page.on('console', m => { if (m.type() === 'error' || /ReferenceError/.test(m.text())) errors.push(m.text()); });
    if (seed) {
        await page.addInitScript(v => {
            if (sessionStorage.getItem('seeded')) return;
            sessionStorage.setItem('seeded', '1');
            for (const [a, b] of Object.entries(v)) localStorage.setItem(a, b);
        }, seed);
    }
    const t0 = Date.now();
    const q = `splat=${encodeURIComponent(FILE_KEY)}&frame=${encodeURIComponent(`${base}/frame.json`)}&levels=${LEVELS.join(',')}`
        + '&photo=1&splatStepMs=1000&splatRetryMs=2000&w=900&h=600';
    await page.goto(`${base}/js/wall3d-harness.html?${q}`);
    const level = () => page.evaluate(() => window.__wall3d?.stats().splat ?? null).catch(() => null);
    const reached = [];
    while (Date.now() < t0 + wait) {
        const l = await level();
        if (l && l.index >= 0 && (reached.length === 0 || reached.at(-1).index !== l.index)) reached.push({ index: l.index, at: Date.now() - t0 });
        if (done(l)) break;
        await page.waitForTimeout(200);
    }
    return { ctx, reached, final: await level(), errors, elapsed: Date.now() - t0 };
}
const top = LEVELS.length - 1;
const atTop = l => l?.index === top;
const summary = r => r.reached.map(x => `${LEVELS[x.index]}@${(x.at / 1000).toFixed(1)}s`).join(' -> ');
const events = () => reports.map(r => `${r.event}${r.level != null ? `:${r.level}` : ''}`).join(',');

// 1. A fast probe climbs the whole ladder.
let r = await session({ done: atTop });
check(atTop(r.final), 'fast probe steps up through every level', `(${summary(r)}; ${(r.elapsed / 1000).toFixed(1)}s)`);
check([1, 2, 3, 4, 5].every(i => reports.some(x => x.event === 'level' && x.level === i)), 'a success report per level', `[${events()}]`);
check(r.errors.length === 0, 'no console errors / ReferenceError', r.errors.join(' | '));
await r.ctx.close();

// 2. A level that fails to decode: level-failed reported, the cap lowered, a re-probe reaches it later.
faults.set('250000', 'garbage');
r = await session({ done: atTop });
const failed = reports.find(x => x.event === 'level-failed' && x.level === 2);
check(!!failed, 'decode error sends level-failed for the failed level', failed ? `(${failed.detail?.slice(0, 60)})` : `[${events()}]`);
check(reports.some(x => x.event === 'reprobe'), 'a re-probe is reported', `[${events()}]`);
check(atTop(r.final), 'the re-probe climbs past the failed level', `(${summary(r)})`);
check(!r.errors.some(e => /ReferenceError/.test(e)), 'no ReferenceError on failure', r.errors.join(' | '));
await r.ctx.close();

// 3. An aborted fetch behaves the same.
faults.set('120000', 'abort');
r = await session({ done: atTop });
check(reports.some(x => x.event === 'level-failed' && x.level === 1), 'aborted fetch sends level-failed', `[${events()}]`);
check(atTop(r.final), 'recovers to the top after the abort', `(${summary(r)})`);
await r.ctx.close();

// 4. A failure remembered 8 days ago is ignored; one from yesterday still caps; the legacy v1 key is dropped.
const seed = days => ({ 'bw.photoreal.lod.v2': JSON.stringify({ files: { [FILE_KEY]: { failSplats: 40000, failAt: Date.now() - days * DAY } } }) });
r = await session({ seed: seed(8), done: atTop });
check(atTop(r.final), 'failSplats older than 7 days is ignored', `(${summary(r)})`);
await r.ctx.close();
r = await session({ seed: seed(1), wait: 10000 });
check(r.final?.index === 0 && requests.every(x => x === '40000'), 'failSplats from yesterday still caps at the first level', `(${summary(r)})`);
await r.ctx.close();
r = await session({ seed: { 'bw.photoreal.lod.v1': JSON.stringify({ failSplats: 40000, at: Date.now() }) }, done: atTop });
check(atTop(r.final), 'the legacy global failure no longer caps', `(${summary(r)})`);
await r.ctx.close();

// 5. The store itself: per file, expiry, success clears, storage that throws.
const ctx = await browser.newContext();
const page = await ctx.newPage();
await page.goto(`${base}/js/wall3d-harness.html`);
const unit = await page.evaluate(async () => {
    const s = await import('/js/wall3d-splat-store.js');
    const L = n => ({ splats: n });
    const out = {};
    const t = Date.now();
    const day = 86400000;
    s.rememberFailure('A', L(250000), t);
    out.perFile = [s.remembered('A', t).failSplats, s.remembered('B', t).failSplats ?? null];
    out.expiry = [s.remembered('A', t + 6 * day).failSplats, s.remembered('A', t + 8 * day).failSplats ?? null];
    s.rememberSuccess('A', L(800000), t);
    out.cleared = s.remembered('A', t).failSplats ?? null;
    s.rememberFailure('A', L(250000), t);
    s.rememberRendered('A', L(120000), t);
    out.keptBelow = s.remembered('A', t).failSplats;
    const proto = Object.getPrototypeOf(localStorage);
    const get = proto.getItem;
    const set = proto.setItem;
    proto.getItem = proto.setItem = () => { throw new Error('storage off'); };
    try {
        s.rememberFailure('A', L(1), t);
        out.throwing = s.remembered('A', t);
    } catch (e) {
        out.throwing = `THREW ${e.message}`;
    }
    proto.getItem = get;
    proto.setItem = set;
    return out;
});
check(unit.perFile[0] === 250000 && unit.perFile[1] === null, 'failures are remembered per splat file', JSON.stringify(unit.perFile));
check(unit.expiry[0] === 250000 && unit.expiry[1] === null, 'a failure expires after 7 days', JSON.stringify(unit.expiry));
check(unit.cleared === null, 'rendering a higher level clears the failure');
check(unit.keptBelow === 250000, 'rendering a lower level keeps it');
check(typeof unit.throwing === 'object', 'throwing localStorage does not break the store', JSON.stringify(unit.throwing));
await ctx.close();

await browser.close();
server.close();
console.log(failures === 0 ? '\nall checks passed' : `\n${failures} check(s) failed`);
process.exit(failures === 0 ? 0 : 1);
