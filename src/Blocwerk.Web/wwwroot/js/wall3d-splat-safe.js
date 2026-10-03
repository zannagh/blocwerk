// Shader failures of the photo-real view (wall3d-splat.js): what a failing program reports, and
// the plainer Spark render path the view retries once before it falls back to Schematic.
//
// iOS Safari translates WebGL shaders to Metal (ANGLE); a translator bug there fails the program at
// link time with an MSL error (see tools/vendor/patch-spark-ios.py). A phone has no console, so the
// full link log and the start of each shader go to the diagnostics beacon (wall3d-splat-diag.js).

/** Lines of each shader's source sent with a shader failure. */
const SHADER_LINES = 120;
/** Lines around each line a compile log names (`ERROR: 0:94: …`), when past SHADER_LINES. */
const AROUND = 6;

/**
 * The retry path: no LOD machinery, no covariance / 2D splats, no edits or raycasting, and the
 * extended (float) splat encoding instead of the packed one. The encoding switch matters: three.js
 * caches programs by source, so a retry with the same shaders would reuse the failed program; the
 * extended encoding reads and accumulates splats through different shader code.
 */
export const RETRY_RENDERER = {
    enableLod: false, enableDriveLod: false, enableLodFetching: false,
    accumExtSplats: true, covSplats: false, enable2DGS: false,
};
export const RETRY_MESH = { enableLod: false, extSplats: true, editable: false, raycastable: false };

/**
 * The one retry after a shader failure. `start(failure, reload)` switches the options to RETRY_* and
 * reloads (on the next task, not mid-render); true while the retry is on, false once it was used.
 * `loaded()` after the retry's level shows: a retry that still draws with the failed program (a
 * shader both paths share, never recompiled, so no new error comes) calls `giveUp`.
 * (Not `renderer.info.programs`: Spark keeps the old generator's material, so it stays listed.)
 */
export function createShaderRetry(renderer, say, giveUp) {
    let state = 'idle';             // 'pending' while the retry loads, then 'done'
    let failed = null;
    const on = () => state !== 'idle';
    return {
        get rendererOptions() { return on() ? RETRY_RENDERER : {}; },
        get meshOptions() { return on() ? RETRY_MESH : {}; },
        start(failure, reload) {
            if (state === 'pending') return true;          // more programs of the same frame
            if (state === 'done') return false;
            state = 'pending';
            failed = failure.program;
            say('shader-retry', { detail: `shader: ${failure.log}`, shader: failure.source });
            setTimeout(reload, 0);
            return true;
        },
        loaded() {
            if (state !== 'pending') return;
            state = 'done';
            // Which programs the next frames bind: an own-property wrapper, removed afterwards.
            const gl = renderer.getContext();
            let reused = 0;
            gl.useProgram = function (program) {
                if (program === failed) reused++;
                return WebGL2RenderingContext.prototype.useProgram.call(this, program);
            };
            setTimeout(() => {
                delete gl.useProgram;
                if (!reused) return;
                say('failed', { detail: 'the shader retry still draws with the failed program' });
                giveUp();
            }, 1500);
        },
    };
}

function numbered(source, lines) {
    const all = String(source || '').split('\n');
    const keep = new Set();
    for (let i = 0; i < Math.min(SHADER_LINES, all.length); i++) keep.add(i);
    for (const n of lines) {
        for (let i = Math.max(0, n - 1 - AROUND); i < Math.min(all.length, n + AROUND); i++) keep.add(i);
    }
    const out = [];
    let last = -1;
    for (const i of [...keep].sort((a, b) => a - b)) {
        if (i !== last + 1) out.push('   …');
        out.push(`${String(i + 1).padStart(4)}: ${all[i]}`);
        last = i;
    }
    if (last < all.length - 1) out.push(`   … (${all.length} lines)`);
    return out.join('\n');
}

/**
 * What a failed program says, from three.js's `debug.onShaderError(gl, program, vs, fs)`:
 * `log` is the program's link log plus each stage's compile log (full text), `source` each stage's
 * first SHADER_LINES lines, numbered, plus the lines around any line the logs name.
 */
export function describeShaderFailure(gl, program, vertexShader, fragmentShader) {
    const parts = [];
    const sources = [];
    try {
        parts.push(`link: ${gl.getProgramInfoLog(program) || '(empty)'}`);
        for (const [stage, shader] of [['vertex', vertexShader], ['fragment', fragmentShader]]) {
            if (!shader) continue;
            const ok = gl.getShaderParameter(shader, gl.COMPILE_STATUS);
            const log = gl.getShaderInfoLog(shader) || '';
            if (!ok || log.trim()) parts.push(`${stage} compile ${ok ? 'ok' : 'FAILED'}: ${log}`);
            const named = [...log.matchAll(/ERROR: \d+:(\d+)/g)].map(m => +m[1]);
            sources.push(`--- ${stage} shader${ok ? '' : ' (compile failed)'}\n${numbered(gl.getShaderSource(shader), named)}`);
        }
    } catch (err) {
        parts.push(`(could not read the program: ${err?.message || err})`);
    }
    return { log: parts.join('\n'), source: sources.join('\n'), program };
}
