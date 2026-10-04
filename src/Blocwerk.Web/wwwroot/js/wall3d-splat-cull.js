// Drops the splats nobody can see before they reach the GPU. The solid wall body (wall3d-body.js) and its
// ceiling cap already hide whatever the capture put behind the wall and above the room, but only by depth
// testing: those splats were still uploaded, sorted and rasterised on every frame. Spark has no per-splat
// cull ahead of its sort (its dyno modifiers only change a splat, every splat stays in the sort), so the
// filter runs once per level, while the level is built: the file is decoded into a scratch PackedSplats and
// the survivors are pushed into the SplatMesh (`constructSplats`). What is left costs less in the upload,
// the per-camera-move sort and the raster, on a phone and on the Raspberry Pi kiosk alike.
//
// A splat is dropped when its centre lies
//   behind  — deeper than CULL_MARGIN_MM behind a body piece's front plane, inside that piece's extruded
//             outline (the same prism the body draws, trims and closed slots included). The body's own front
//             face sits BODY_FRONT_MM behind the facet, so the margin leaves the real surface, the holds
//             that stand out, and a splat's fuzzy half-depth alone;
//   above   — higher than CEILING_MARGIN_MM above the ceiling cap (the wall's top edge). The cap hides it
//             already; the margin keeps the wall's real top;
//   header  — (the top-right fringe) above a wall piece that stops short of the ceiling cap, within
//             HEADER_MIN_MM..HEADER_MAX_MM of it: the cap hangs flat over the whole wall, so over a lower side
//             structure (The Attic: the roof and side panels right of the main wall, 450-650 mm under the cap) it
//             leaves a gap, and what the capture put in it is a band of smeared, half-transparent splats
//             (its ceiling plane seen edge-on and floaters, nothing the viewer needs). Above the piece's top
//             edge (+ HEADER_CLEAR_MM, so the real top and its holds stay), over its footprint widened by
//             HEADER_REACH_MM, the splats go and the cap shows there, as it does everywhere else.
// Everything else (the floor and mats, the surroundings in front, the fade near the camera) stays and is
// handled by the shader clip (wall3d-splat-clip.js), which depends on the camera. Visually the result is the
// same as without the cull, as the dropped splats were behind opaque geometry.
//
// On everywhere (`?splatCull=trim` keeps only the above and header rules, `=off` loads every splat): a phone
// or the kiosk gains most, but a desktop measured ~8% cheaper per frame too, for ~25 ms more at load than the
// header rule alone costs. The pipeline's splats carry no spherical harmonics (SH degree 0), so the copy loses
// nothing. Measured on The Attic (250k / 800k levels): 17% / 9% of the splats go.
import { backwards, BODY_DEPTH_MM, BODY_FRONT_MM, pointOf } from './wall3d-body.js';

/** A splat must be at least this far behind a body piece's front plane (the facet's plane for the wall) to go. */
export const CULL_MARGIN_MM = 120;
/** ... and this far above the ceiling cap. */
export const CEILING_MARGIN_MM = 100;
/** Header: pieces whose top edge is this far under the ceiling cap (at least / at most); lower ones are no wall top. */
export const HEADER_MIN_MM = 250;
export const HEADER_MAX_MM = 800;
/** ... splats this far above the top edge go; the footprint on the floor plan is widened by the reach. */
export const HEADER_CLEAR_MM = 80;
export const HEADER_REACH_MM = 300;

/** A body piece as flat numbers: origin, axes, the way back and the outline's edge normals. */
function prismOf(p) {
    const d = backwards(p);
    const edges = [];
    p.outline.forEach(([ax, ay], i) => {
        const [bx, by] = p.outline[(i + 1) % p.outline.length];
        const ex = bx - ax, ey = by - ay, len = Math.hypot(ex, ey) || 1;
        // Outline is convex and counter-clockwise: inside is where nx*a + ny*b <= c on every edge.
        edges.push(ey / len, -ex / len, (ey * ax - ex * ay) / len);
    });
    return {
        o: p.o.toArray(), u: p.u.toArray(), v: p.v.toArray(), n: p.n.toArray(), d: d.toArray(),
        dn: d.dot(p.n), depth: p.depth ?? BODY_DEPTH_MM, edges,
    };
}

/** True when world point (x, y, z) lies inside the prism, deeper than the margin behind its front plane. */
function inside(q, x, y, z) {
    const dx = x - q.o[0], dy = y - q.o[1], dz = z - q.o[2];
    const h = dx * q.n[0] + dy * q.n[1] + dz * q.n[2];
    if (h > -CULL_MARGIN_MM || q.dn > -1e-6) {
        return false;
    }
    const t = (h + BODY_FRONT_MM) / q.dn;               // how far back from the front plane the point lies
    if (t > q.depth) {
        return false;
    }
    const rx = dx - q.d[0] * t, ry = dy - q.d[1] * t, rz = dz - q.d[2] * t;
    const a = rx * q.u[0] + ry * q.u[1] + rz * q.u[2];
    const b = rx * q.v[0] + ry * q.v[1] + rz * q.v[2];
    const e = q.edges;
    for (let i = 0; i < e.length; i += 3) {
        if (e[i] * a + e[i + 1] * b > e[i + 2]) {
            return false;
        }
    }
    return true;
}

/** Convex hull of 2D points, counter-clockwise. */
function hull(points) {
    const pts = points.slice().sort((a, b) => a[0] - b[0] || a[1] - b[1]);
    const cross = (o, a, b) => (a[0] - o[0]) * (b[1] - o[1]) - (a[1] - o[1]) * (b[0] - o[0]);
    const half = list => {
        const out = [];
        for (const p of list) {
            while (out.length >= 2 && cross(out[out.length - 2], out[out.length - 1], p) <= 0) {
                out.pop();
            }
            out.push(p);
        }
        out.pop();
        return out;
    };
    return [...half(pts), ...half(pts.slice().reverse())];
}

/** The header of a body piece ({ z, edges }) when it stops short of the ceiling cap by HEADER_MIN..MAX, else null. */
function headerOf(p, ceilingZ) {
    const corners = p.outline.map(([a, b]) => pointOf(p, a, b));
    const top = Math.max(...corners.map(c => c.z));
    const gap = ceilingZ - top;
    const poly = hull(corners.map(c => [c.x, c.y]));
    // A vertical piece has no footprint (its top is a line on the floor plan), a roof piece is the closed recess's.
    if (p.roof || Math.abs(p.n.z) < 0.1 || gap < HEADER_MIN_MM || gap > HEADER_MAX_MM || poly.length < 3) {
        return null;
    }
    const edges = [];
    poly.forEach(([ax, ay], i) => {
        const [bx, by] = poly[(i + 1) % poly.length];
        const ex = bx - ax, ey = by - ay, len = Math.hypot(ex, ey) || 1;
        edges.push(ey / len, -ex / len, (ey * ax - ex * ay) / len + HEADER_REACH_MM);
    });
    return { z: top + HEADER_CLEAR_MM, edges };
}

function inHeader(h, x, y, z) {
    if (z <= h.z) {
        return false;
    }
    const e = h.edges;
    for (let i = 0; i < e.length; i += 3) {
        if (e[i] * x + e[i + 1] * y > e[i + 2]) {
            return false;
        }
    }
    return true;
}

/**
 * What the cull drops, from `?splatCull=`: 'full' (the default: behind, above and header), 'trim' (above and
 * header only, nothing the body hides) or null ('off': every splat loads, for comparing).
 */
export function cullMode(q = new URLSearchParams(location.search)) {
    const v = q.get('splatCull');
    return v === 'off' ? null : v === 'trim' ? 'trim' : 'full';
}

/**
 * `body`: buildBody(view) ({ pieces, ceilingZ }), `matrix`: view.splatMatrix (column-major, splat → world).
 * `keep(x, y, z)` takes a splat centre in the file's own coordinates.
 */
export function createSplatCull(body, matrix, mode = 'full') {
    const prisms = mode === 'full' ? body.pieces.map(prismOf) : [];
    const headers = body.ceilingZ == null ? [] : body.pieces.map(p => headerOf(p, body.ceilingZ)).filter(Boolean);
    const m = matrix;
    const ceiling = body.ceilingZ == null ? Infinity : body.ceilingZ + CEILING_MARGIN_MM;
    const keep = (x, y, z) => {
        const wx = m[0] * x + m[4] * y + m[8] * z + m[12];
        const wy = m[1] * x + m[5] * y + m[9] * z + m[13];
        const wz = m[2] * x + m[6] * y + m[10] * z + m[14];
        if (wz > ceiling) {
            return false;
        }
        for (const h of headers) {
            if (inHeader(h, wx, wy, wz)) {
                return false;
            }
        }
        for (const q of prisms) {
            if (inside(q, wx, wy, wz)) {
                return false;
            }
        }
        return true;
    };

    /** A SplatMesh of the splats of `fileBytes` that survive `keep`; `mesh.cullStats` = { kept, total }. */
    function mesh(spark, fileBytes, options) {
        const stats = { kept: 0, total: 0 };
        const next = new spark.SplatMesh({
            ...options,
            constructSplats: async splats => {
                const source = new spark.PackedSplats({ fileBytes, fileType: 'spz' });
                await source.initialized;
                stats.total = source.numSplats;
                source.forEachSplat((i, center, scales, quaternion, opacity, color) => {
                    if (keep(center.x, center.y, center.z)) {
                        splats.pushSplat(center, scales, quaternion, opacity, color);
                        stats.kept++;
                    }
                });
                source.dispose();
            },
        });
        next.cullStats = stats;
        return next;
    }

    return { keep, mesh };
}
