// Downloads of the photo-real view (wall3d-splat.js). A level's bytes are fetched here and handed to
// Spark as `fileBytes`, not as a url: Spark 2.2's url loader takes no AbortSignal, so leaving the view,
// switching the mode or a lost context could not stop a download (nor the decode after it). Here an
// abort ends the download at once and nothing reaches Spark. A download that receives nothing for
// STALL_MS fails instead of leaving the mode switch on "Loading…" (a stalled gym Wi-Fi).

/** A download that receives no bytes for this long fails. */
export const STALL_MS = 30_000;

/** The photo-real view cannot run on this device (no WebGL 2, or it failed here before). */
export class PhotoRealUnsupportedError extends Error {}

/** The switch to photo-real was cancelled (another mode picked while it loaded). Not an error to show. */
export class PhotoRealCancelledError extends Error {}

/** The scene is too large to load on this device without asking; the message says so. */
export class PhotoRealTooLargeError extends Error {}

/** A download stopped making progress. */
export class PhotoRealStalledError extends Error {}

/** Aborts every download started with its `signal`; `abort()` also hands out a fresh signal for later ones. */
export function createAborter() {
    let controller = new AbortController();
    return {
        get signal() { return controller.signal; },
        abort() {
            controller.abort();
            controller = new AbortController();
        },
    };
}

/** A cancellable first load: `cancel()` marks it and rejects `stopped` with PhotoRealCancelledError. */
export function createCancelToken() {
    const token = { cancelled: false };
    token.stopped = new Promise((_, reject) => {
        token.cancel = () => {
            token.cancelled = true;
            reject(new PhotoRealCancelledError('Cancelled.'));
        };
    });
    token.stopped.catch(() => { /* only raced, never awaited alone */ });
    return token;
}

/**
 * Fetches `url` into one Uint8Array; `progress(fraction | null)` while it streams. Resolves null when
 * `signal` aborted it; throws on an HTTP error, or PhotoRealStalledError after `stallMs` without bytes.
 */
export async function fetchSplatBytes(url, signal, progress, stallMs = STALL_MS) {
    if (signal.aborted) return null;
    const own = new AbortController();
    const onAbort = () => own.abort();
    signal.addEventListener('abort', onAbort, { once: true });
    let stalled = false;
    let timer = 0;
    const arm = () => {
        clearTimeout(timer);
        timer = setTimeout(() => { stalled = true; own.abort(); }, stallMs);
    };
    try {
        arm();
        const res = await fetch(url, { signal: own.signal, credentials: 'same-origin' });
        if (!res.ok || !res.body) throw new Error(`Failed to fetch "${url}": ${res.status} ${res.statusText}`);
        const total = Number.parseInt(res.headers.get('Content-Length') || '0', 10) || 0;
        const reader = res.body.getReader();
        const chunks = [];
        let loaded = 0;
        for (;;) {
            const { done, value } = await reader.read();
            if (done) break;
            arm();
            chunks.push(value);
            loaded += value.length;
            progress?.(total > 0 ? Math.min(1, loaded / total) : null);
        }
        return join(chunks, loaded);
    } catch (err) {
        if (stalled) throw new PhotoRealStalledError(`No data for ${Math.round(stallMs / 1000)} s from "${url}"`);
        if (signal.aborted) return null;
        throw err;
    } finally {
        clearTimeout(timer);
        signal.removeEventListener('abort', onAbort);
    }
}

function join(chunks, length) {
    if (chunks.length === 1) return chunks[0];
    const bytes = new Uint8Array(length);
    let at = 0;
    for (const c of chunks) {
        bytes.set(c, at);
        at += c.length;
    }
    return bytes;
}
