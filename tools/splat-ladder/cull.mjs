// Node checks for the splat cull geometry (wwwroot/js/wall3d-splat-cull.js): what goes and what stays.
//   node tools/splat-ladder/cull.mjs
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const js = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../../src/Blocwerk.Web/wwwroot/js');
const { bodyPieces } = await import(`${js}/wall3d-body.js`);
const { createSplatCull, cullMode, CULL_MARGIN_MM, HEADER_CLEAR_MM } = await import(`${js}/wall3d-splat-cull.js`);

let failures = 0;
const check = (ok, what) => {
    console.log(`${ok ? 'PASS' : 'FAIL'}  ${what}`);
    if (!ok) failures++;
};

const facet = (id, corners, normal, u, v, extent) => ({ id, origin: corners[0], corners, normal, u, v, extent });
// A vertical wall in the plane y = 0 facing -y (x 0..4000, z 0..2000) and a lower, leaning side piece beside it.
const main = facet('main', [[0, 0, 0], [4000, 0, 0], [4000, 0, 2000], [0, 0, 2000]], [0, -1, 0], [1, 0, 0], [0, 0, 1], { aMin: 0, aMax: 4000, bMin: 0, bMax: 2000 });
// The side piece overhangs (it leans out over the floor), 1400 mm up its slope (top at z 1260), ending 740 mm under the cap.
const lean = [0, -0.436, 0.9];
const at = (a, b) => [4000 + a, -0.436 * b, 0.9 * b];
const side = facet('side', [at(0, 0), at(1000, 0), at(1000, 1400), at(0, 1400)], [0, -0.9, -0.436], [1, 0, 0], lean, { aMin: 0, aMax: 1000, bMin: 0, bMax: 1400 });
const pieces = bodyPieces([main, side], []);
const body = { pieces, ceilingZ: 2000 };
const identity = [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1];
const full = createSplatCull(body, identity, 'full').keep;
const trim = createSplatCull(body, identity, 'trim').keep;

check(full(2000, 10, 1000) === true, 'a splat on the wall surface stays');
check(full(2000, -300, 1000) === true, 'a hold standing out in front stays');
check(full(2000, CULL_MARGIN_MM - 1, 1000) === true, 'a splat within the margin behind the wall stays');
check(full(2000, 500, 1000) === false, 'a splat well behind the wall goes');
check(full(2000, 5000, 1000) === true, 'a splat beyond the body depth (outside the prism) stays');
check(full(-500, 500, 1000) === true, 'a splat behind but beside the wall (outside its outline) stays');
check(full(2000, -500, 2100) === true, 'just above the wall top stays (within the ceiling margin)');
check(full(2000, -500, 2300) === false, 'well above the ceiling goes');
check(trim(2000, 500, 1000) === true, 'trim keeps what the body hides (no behind rule)');
check(trim(2000, -500, 2300) === false, 'trim still drops above the ceiling');
// The side piece stops 740 mm under the cap: its header goes from 80 mm over its top edge.
check(full(4500, -600, 1260 + HEADER_CLEAR_MM + 20) === false, 'over a lower side piece, above its top edge, the header goes');
check(full(4500, -600, 1260 + 20) === true, 'the side piece top edge and its holds stay');
check(full(4500, -2500, 1500) === true, 'far in front of the side piece stays (beyond the reach)');
check(full(2000, -200, 1500) === true, 'the main wall reaches the cap: no header over it');
check(cullMode(new URLSearchParams('')) === 'full', 'the full cull is the default');
check(cullMode(new URLSearchParams('splatCull=off')) === null && cullMode(new URLSearchParams('splatCull=trim')) === 'trim', '?splatCull= overrides');

console.log(failures === 0 ? '\nall cull checks passed' : `\n${failures} cull check(s) failed`);
process.exit(failures === 0 ? 0 : 1);
