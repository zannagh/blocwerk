// A synthetic Gaussian splat in the SPZ v2 layout the capture pipeline writes (gzip; header magic "NGSP", version 2,
// SH degree 0, 12 fractional bits), so the budget gate needs no capture of a real wall. The Attic's ladder starts at
// 250 000 splats (3.97 MB), the level a phone renders first; BW_GATE_SPLAT=<path of a real .spz> measures that one
// instead. Deterministic: the same count always gives the same bytes.
import fs from 'node:fs';
import path from 'node:path';
import { gunzipSync, gzipSync } from 'node:zlib';

const MAGIC = 0x5053474e;
const FRACTIONAL_BITS = 12;

function rng(seed) {
    let s = seed;
    return () => (s = (s * 16807) % 2147483647) / 2147483647;
}

/** The bytes of a splat of `count` points spread over a 4 m × 3 m wall, about 30 mm thick. */
export function syntheticSpz(count) {
    const rnd = rng(7);
    const header = Buffer.alloc(16);
    header.writeUInt32LE(MAGIC, 0);
    header.writeUInt32LE(2, 4);
    header.writeUInt32LE(count, 8);
    header.writeUInt8(0, 12);
    header.writeUInt8(FRACTIONAL_BITS, 13);
    const positions = Buffer.alloc(count * 9);
    const alphas = Buffer.alloc(count);
    const colors = Buffer.alloc(count * 3);
    const scales = Buffer.alloc(count * 3);
    const rotations = Buffer.alloc(count * 3);
    for (let i = 0; i < count; i++) {
        // Metres in the splat's own frame: x across the wall, y up it, z its relief.
        const xyz = [rnd() * 4 - 2, rnd() * 3 - 1.5, (rnd() - 0.5) * 0.03];
        xyz.forEach((v, axis) => {
            const fixed = Math.round(v * (1 << FRACTIONAL_BITS)) & 0xffffff;
            positions.writeUIntLE(fixed, i * 9 + axis * 3, 3);
        });
        alphas[i] = 160 + Math.floor(rnd() * 95);
        for (let c = 0; c < 3; c++) {
            colors[i * 3 + c] = 90 + Math.floor(rnd() * 120);
            // log-scale, (s + 10) × 16: about 4–12 mm
            scales[i * 3 + c] = Math.round((Math.log(0.004 + rnd() * 0.008) + 10) * 16);
            rotations[i * 3 + c] = 128 + Math.floor((rnd() - 0.5) * 40);
        }
    }
    return gzipSync(Buffer.concat([header, positions, alphas, colors, scales, rotations]), { level: 6 });
}

/** A file to serve: BW_GATE_SPLAT when set, else the synthetic splat of `count`, written once under `dir`. */
export function splatFile(dir, count) {
    if (process.env.BW_GATE_SPLAT) {
        return { file: process.env.BW_GATE_SPLAT, source: `real (${path.basename(process.env.BW_GATE_SPLAT)})` };
    }
    const file = path.join(dir, `synthetic-${count}.spz`);
    if (!fs.existsSync(file)) {
        fs.mkdirSync(dir, { recursive: true });
        fs.writeFileSync(file, syntheticSpz(count));
    }
    return { file, source: `synthetic ${count}` };
}

/** The point count in an SPZ file's header. */
export function splatCount(file) {
    return gunzipSync(fs.readFileSync(file)).readUInt32LE(8);
}
