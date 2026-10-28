// The budget gate's verdict: reads .out/gate/results.json (written by the gate specs) and prints one Markdown table,
// measure by profile against its threshold, then GO or NO-GO. Exits 1 on a miss or a missing measure.
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { PROFILES, THRESHOLDS } from './profiles.mjs';

const here = path.dirname(fileURLToPath(import.meta.url));
const file = path.join(here, '..', '.out', 'gate', 'results.json');
const results = fs.existsSync(file) ? JSON.parse(fs.readFileSync(file, 'utf8')) : {};
const profiles = Object.keys(PROFILES).filter(p => results[p]);

// [label, value of a profile's results, its threshold for that profile (null: recorded, not gated), unit]. Memory is
// the resident memory of the page and GPU processes.
const ROWS = [
    ['Bundle, Brotli (cold /v/ boot)', r => r.bundleBrotliKiB, () => THRESHOLDS.bundleBrotliKiB, 'KiB'],
    ['Bundle on the wire here (plain http: br in Chromium, gzip in WebKit)', r => r.bundleWireKiB, () => null, 'KiB'],
    ['Warm start (cached bundle, Fast 4G)', r => r.warmStartMs, p => THRESHOLDS.warmStartMs[p], 'ms'],
    ['Cold start (empty cache, Fast 4G)', r => r.coldStartMs, p => THRESHOLDS.coldStartFast4gMs[p] ?? null, 'ms'],
    ['Cold start (empty cache, Slow 4G)', r => r.coldStartSlow4gMs, () => null, 'ms'],
    ['.NET linear memory after boot', r => r.memory250k?.wasmHeapMiB, () => THRESHOLDS.wasmHeapMiB, 'MiB'],
    ['Client overhead, booted 20 s, no splat', r => r.memory250k?.bootOverheadMiB, () => null, 'MiB'],
    ['Client overhead at the 250k splat peak (settled)', r => r.memory250k?.peakOverheadMiB, () => THRESHOLDS.clientOverheadMiB, 'MiB'],
    ['Client overhead, 250k splat shown (settled)', r => r.memory250k?.shownOverheadMiB, () => THRESHOLDS.clientOverheadMiB, 'MiB'],
    ['Peak, client + 250k splat (settled)', r => r.memory250k?.clientPeakMiB, () => null, 'MiB'],
    ['Peak, blank page + 250k splat', r => r.memory250k?.blankPeakMiB, () => null, 'MiB'],
    ['Client overhead, booted 1.5 s, no splat', r => r.memory250kAtBoot?.bootOverheadMiB, () => null, 'MiB'],
    ['Client overhead at the 250k splat peak (at boot)', r => r.memory250kAtBoot?.peakOverheadMiB, () => null, 'MiB'],
    ['Peak, client + 250k splat (at boot)', r => r.memory250kAtBoot?.clientPeakMiB, () => null, 'MiB'],
    ['Client overhead at the 800k splat peak (settled)', r => r.memory800k?.peakOverheadMiB, () => null, 'MiB'],
    ['Peak, client + 800k splat (settled)', r => r.memory800k?.clientPeakMiB, () => null, 'MiB'],
];

let misses = 0;
const cell = (row, p) => {
    const [, value, limit] = row;
    const v = value(results[p]);
    const l = limit(p);
    if (v === undefined || v === null) {
        if (l !== null && l !== undefined) {
            misses++;
            return 'missing';
        }
        return '–';
    }
    if (l === null || l === undefined) {
        return String(v);
    }
    const ok = v <= l;
    misses += ok ? 0 : 1;
    return `${v} ${ok ? '✓' : '✗'} (≤ ${l})`;
};

const lines = [
    `| Measure | Unit | ${profiles.join(' | ')} |`,
    `|---|---|${profiles.map(() => '---').join('|')}|`,
    ...ROWS.map(row => `| ${row[0]} | ${row[3]} | ${profiles.map(p => cell(row, p)).join(' | ')} |`),
];
console.log(lines.join('\n'));
const modes = profiles.map(p => `${p}: ${(results[p].memory250k?.modes || []).join(',') || 'not run'}`).join('; ');
console.log(`\nSplat shown as: ${modes}`);
const verdict = profiles.length === Object.keys(PROFILES).length && misses === 0 ? 'GO' : 'NO-GO';
console.log(`\n${verdict}: ${misses} gated measure(s) missed or missing, ${profiles.length}/${Object.keys(PROFILES).length} profiles measured.`);
process.exit(verdict === 'GO' ? 0 : 1);
