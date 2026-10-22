/*
 * The worker's half of the 401 lock (Phase 3 PR 3.5), loaded into service-worker.js by importScripts after
 * sw-asset-cache.js. A `u-` partition whose session expired is LOCKED page-side (asset-lock.js): kept on the device for
 * the same user's return, but never served. The page writes the locked label to `bw-sw-state` (ASSET_LOCK_KEY) and
 * removes it on unlock or wipe; this worker never serves from, and never stores into, a partition of that label.
 *
 * Online the label the worker serves is the cookie's, which is never the locked one (the session expired), so the lock
 * is a second guard there. It is the only guard on the OFFLINE path: an unreachable lookup falls back to the last known
 * label, which may still be the locked one. So that path always reads the marker fresh, while the online path uses the
 * copy read once per worker start and again on every `sw-refresh-partition` (the page posts one after each change).
 */
const ASSET_LOCK_KEY = '/__bw/locked';

const assetLock = { label: null, read: null };

// The locked label, or null. `fresh` reads the marker again instead of the copy.
function lockedPartition(fresh) {
    if (fresh || !assetLock.read) {
        assetLock.read = caches.match(ASSET_LOCK_KEY, { cacheName: ASSET_STATE_CACHE })
            .then(stored => (stored ? stored.text() : null))
            .then(label => {
                assetLock.label = typeof label === 'string' && label ? label : null;
                return assetLock.label;
            })
            .catch(() => assetLock.label);
    }
    return assetLock.read;
}

// The next request reads the marker again (a page changed the lock).
function forgetLockedPartition() {
    assetLock.read = null;
}

// True when `label` must not be served or stored: it is the locked partition.
function isLockedPartition(label, fresh) {
    return lockedPartition(fresh).then(locked => locked !== null && locked === label);
}
