// Diagnostics beacon of the photo-real view (wall3d-splat.js): what the device is and what happened
// (start, a level shown, a lost / restored WebGL context, a failure), POSTed to
// /api/diagnostics/photo-real, which only logs it. A phone has no console to read, so this is how a
// real device's limits reach the server log. Nothing personal is sent (no user agent, no ids); a
// signed-out viewer's reports are refused by the server and ignored here.

const ENDPOINT = '/api/diagnostics/photo-real';
/** Reports per page load; the server rate-limits per user too. */
const MAX_REPORTS = 40;

let sent = 0;

/** Static facts about the device and its GPU, read once per renderer. */
export function deviceFacts(renderer) {
    const facts = { dpr: window.devicePixelRatio || 1, deviceMemory: navigator.deviceMemory ?? null, mobile: null };
    try {
        const gl = renderer.getContext();
        facts.maxTextureSize = gl.getParameter(gl.MAX_TEXTURE_SIZE);
        const info = gl.getExtension('WEBGL_debug_renderer_info');
        facts.renderer = String(gl.getParameter(info ? info.UNMASKED_RENDERER_WEBGL : gl.RENDERER));
        facts.vendor = String(gl.getParameter(info ? info.UNMASKED_VENDOR_WEBGL : gl.VENDOR));
    } catch {
        // A lost context answers nothing; the report still goes out with what is known.
    }
    return facts;
}

/**
 * Sends one report. `facts` from deviceFacts; `state`: { level, levels, splats, pixelRatio, frameMs,
 * elapsedMs, lostCount, safeSplats, mobile, detail }. Fire-and-forget: never throws, never waits.
 */
export function report(event, renderer, facts, state = {}) {
    if (sent >= MAX_REPORTS) return;
    sent++;
    const canvas = renderer.domElement;
    const body = {
        event,
        level: state.level ?? null,
        levels: state.levels ?? null,
        splats: state.splats ?? null,
        renderer: facts.renderer ?? null,
        vendor: facts.vendor ?? null,
        maxTextureSize: facts.maxTextureSize ?? null,
        deviceMemory: facts.deviceMemory,
        dpr: facts.dpr,
        pixelRatio: state.pixelRatio ?? renderer.getPixelRatio(),
        canvasWidth: canvas.width,
        canvasHeight: canvas.height,
        frameMs: state.frameMs == null ? null : Math.round(state.frameMs * 10) / 10,
        elapsedMs: state.elapsedMs == null ? null : Math.round(state.elapsedMs),
        lostCount: state.lostCount ?? 0,
        safeSplats: state.safeSplats ?? null,
        mobile: state.mobile ?? null,
        detail: state.detail ? String(state.detail).slice(0, 256) : null,
    };
    try {
        fetch(ENDPOINT, {
            method: 'POST',
            credentials: 'same-origin',
            keepalive: true,
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(body),
        }).catch(() => { /* offline or signed out: diagnostics are best effort */ });
    } catch {
        // fetch itself unavailable: nothing to report with.
    }
}
