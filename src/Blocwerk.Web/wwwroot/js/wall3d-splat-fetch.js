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

/** A cancellable wait: `cancel()` rejects `stopped` with PhotoRealCancelledError. */
function createCancelToken() {
    const token = {};
    token.stopped = new Promise((_, reject) => {
        token.cancel = () => reject(new PhotoRealCancelledError('Cancelled.'));
    });
    token.stopped.catch(() => { /* only raced, never awaited alone */ });
    return token;
}

/**
 * The first load of the photo-real view, shared by every pick of the mode: `wait()` starts `start()`
 * once and resolves when it is done, so a pick during a load (or a decode) waits for that load instead
 * of starting a second one. `cancel(forget)` makes the waiting pick throw PhotoRealCancelledError;
 * with `forget` the next pick starts afresh (the caller aborted the download). A failed load is
 * forgotten, so the next pick retries it.
 */
export function createFirstLoad(start) {
    let loading = null;
    let waiter = null;
    return {
        /** Whether a load was started (and not forgotten). */
        get started() { return !!loading; },
        async wait() {
            if (!loading) {
                const p = start().catch(err => {
                    if (loading === p) loading = null;
                    throw err;
                });
                loading = p;
            }
            const token = createCancelToken();
            waiter = token;
            try {
                await Promise.race([loading, token.stopped]);
            } finally {
                if (waiter === token) waiter = null;
            }
        },
        /** False when no pick was waiting. */
        cancel(forget) {
            if (!waiter) return false;
            waiter.cancel();
            waiter = null;
            if (forget) loading = null;
            return true;
        },
        reset() { loading = null; },
    };
}

/**
 * Fetches `url` into one Uint8Array; `progress(fraction | null)` while it streams. Resolves null when
 * `signal` aborted it; throws on an HTTP error, or PhotoRealStalledError after `stallMs` without bytes.
 * The stall timer pauses while the page is hidden (iOS throttles background tabs) and restarts on return.
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
        if (document.visibilityState === 'hidden') return;
        timer = setTimeout(() => { stalled = true; own.abort(); }, stallMs);
    };
    document.addEventListener('visibilitychange', arm);
    try {
        arm();
        const res = await fetch(url, { signal: own.signal, credentials: 'same-origin' });
        if (!res.ok || !res.body) throw new Error(`Failed to fetch "${url}": ${res.status} ${res.statusText}`);
        const total = Number.parseInt(res.headers.get('Content-Length') || '0', 10) || 0;
        return await readAll(res.body.getReader(), total, arm, progress);
    } catch (err) {
        if (stalled) throw new PhotoRealStalledError(`No data for ${Math.round(stallMs / 1000)} s from "${url}"`);
        if (signal.aborted) return null;
        throw err;
    } finally {
        clearTimeout(timer);
        document.removeEventListener('visibilitychange', arm);
        signal.removeEventListener('abort', onAbort);
    }
}

/**
 * Reads the body. With a Content-Length the bytes go straight into one buffer of that size (no copy at
 * the end); without one, or when the body turns out longer, the chunks are joined once at the end.
 */
async function readAll(reader, total, onChunk, progress) {
    let buffer = total > 0 ? new Uint8Array(total) : null;
    const chunks = [];
    let loaded = 0;
    for (;;) {
        const { done, value } = await reader.read();
        if (done) break;
        onChunk();
        if (buffer && loaded + value.length <= buffer.length) {
            buffer.set(value, loaded);
        } else {
            if (buffer) chunks.push(buffer.subarray(0, loaded));
            buffer = null;
            chunks.push(value);
        }
        loaded += value.length;
        progress?.(total > 0 ? Math.min(1, loaded / total) : null);
    }
    if (buffer) return loaded === buffer.length ? buffer : buffer.subarray(0, loaded);
    return join(chunks, loaded);
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
