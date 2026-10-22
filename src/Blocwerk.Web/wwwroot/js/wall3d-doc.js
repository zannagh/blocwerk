// The 3D view's model as a content-addressed document (design 2026-10-21 §6.5, PR 2.8). Wall3DStage sends
// `{ doc, wallId, sha, bytes, token, overlay }` instead of the whole view: `doc` is /api/walls/{w}/d/view3d/{sha},
// which the service worker serves from the device cache like an /a/ asset, so reopening a wall's 3D costs no model
// download and no large interop message. The document is wall-level: no boulder, no roles, no share token. The overlay
// puts the boulder back (its id and name, each hold's role, and the holds drawn only because that boulder uses them)
// and the page's share token goes back onto every texture and splat URL.
//
// A document that cannot be fetched or is not this wall's throws DOC_FAILED; the stage then mounts the view it sends
// over interop instead. A payload without `doc` is a whole view already (the fallback, the dev harness).

/** Prefix of the error `resolveView` throws when the document could not be used. */
export const DOC_FAILED = 'wall3d-doc:';

/**
 * How long the document may take, body included. Well inside Blazor's 60 s JS-call timeout, so a stalled fetch
 * falls back to the view over interop instead of failing the mount.
 */
export const DOC_TIMEOUT_MS = 30_000;

/** The view to draw: the payload itself, or its document with the token and the overlay applied. */
export async function resolveView(payload, fetchImpl = (url, init) => fetch(url, init), timeoutMs = DOC_TIMEOUT_MS) {
    if (!payload || !payload.doc) return payload;
    const abort = new AbortController();
    let timer = 0;
    const expired = new Promise((_, reject) => {
        timer = setTimeout(() => { abort.abort(); reject(new Error(`no answer in ${Math.round(timeoutMs / 1000)} s`)); }, timeoutMs);
    });
    let doc;
    try {
        doc = await Promise.race([readDoc(payload.doc, fetchImpl, abort.signal), expired]);
    } catch (err) {
        throw new Error(`${DOC_FAILED} ${err?.message || err}`);
    } finally {
        clearTimeout(timer);
    }
    if (!doc || doc.wallId !== payload.wallId) throw new Error(`${DOC_FAILED} not this wall's document`);
    try {
        window.bwAsset3d?.noteDoc(payload.wallId, payload.sha, payload.bytes);
    } catch {
        // The ledger is a convenience: a failed note only lets eviction treat the document as an orphan.
    }
    return applyOverlay(withToken(doc, payload.token), payload.overlay);
}

async function readDoc(url, fetchImpl, signal) {
    const res = await fetchImpl(url, { credentials: 'same-origin', signal });
    if (!res.ok) throw new Error(`HTTP ${res.status}`);
    return res.json();
}

/** `url` with the share token appended, as the server's AssetUrls would have put it. */
export function tokenized(url, token) {
    if (!url || !token) return url;
    return `${url}${url.includes('?') ? '&' : '?'}token=${encodeURIComponent(token)}`;
}

/** The view with the share token on every texture, mask and splat URL. */
export function withToken(view, token) {
    if (!token) return view;
    return {
        ...view,
        textures: (view.textures || []).map(t => ({ ...t, url: tokenized(t.url, token), maskUrl: tokenized(t.maskUrl, token) })),
        splatUrl: tokenized(view.splatUrl, token),
        splatMobileUrl: tokenized(view.splatMobileUrl, token),
        splatLevels: (view.splatLevels || []).map(l => ({ ...l, url: tokenized(l.url, token) })),
    };
}

/** The boulder's view: its id and name, every hold's role, and its own holds after the wall's. */
export function applyOverlay(view, overlay) {
    if (!overlay) return view;
    const roles = overlay.roles || {};
    const holds = (view.holds || []).map(h => (roles[h.id] ? { ...h, role: roles[h.id] } : h));
    return {
        ...view,
        boulderId: overlay.boulderId ?? null,
        boulderName: overlay.boulderName ?? null,
        holds: holds.concat(overlay.holds || []),
    };
}
