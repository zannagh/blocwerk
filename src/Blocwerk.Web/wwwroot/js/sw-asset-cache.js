/*
 * The on-device wall-asset cache (design 2026-10-21 §6.6, §6.7), loaded into service-worker.js by importScripts.
 *
 * GET /api/walls/{w}/a/{sha} and /a/{sha}/{width} are content-addressed and immutable, so they are served
 * cache-first from `bw-a1-{partition}-img` (images) or `-3d` (everything else), keyed `/__bw/{sha}[/{width}]`: the
 * key drops the wall, `token`, `rev` and `bwretry`, because the bytes are fully named by the sha. A miss goes to the
 * network and only a plain 200 is stored. 404s, 206s, Range requests and anything uncacheable stay network-only.
 *
 * THE PARTITION. Each page (client) gets its label from /api/me/cache-partition, once, at its first asset request:
 * that is the cookie identity the page's requests carry, with no page cooperation needed, so a page with old JS (or
 * none) is cached correctly. The newest answer from any page is the browser-wide `current` label, because the cookie
 * is shared by every tab. A request captures its page's label at fetch start and is cache-backed only while that
 * label is `current`; before the put it re-checks, and a switch in between discards the response. So:
 *   - a switch (sign-in, sign-out, a kiosk re-bind) stops serving the old partition the moment any page learns it;
 *   - a tab still showing the old identity goes network-only rather than reading or writing either partition;
 *   - `none` (or a failed lookup) is network-only.
 * Wiping the old partition is the page's job (2.7's wipe rules), never this file's.
 *
 * OFFLINE. An unreachable lookup falls back to the last label, kept in `bw-sw-state` so it survives the worker being
 * stopped. The cookie cannot change while the server is unreachable, so that is still the identity on the wire.
 *
 * STORAGE REFUSED. Any error from the cache (QuotaExceededError, a full device, a private window) leaves that asset
 * network-only. The page always gets the network response; caching never fails a request.
 */
const ASSET_ROUTE = /^\/api\/walls\/[^/]+\/a\/([0-9a-f]{64})(?:\/(\d{1,5}))?$/;
const ASSET_PARTITION_URL = '/api/me/cache-partition';
const ASSET_LABEL = /^(anon|[uk]-[a-z2-7]{20})$/;
const ASSET_STATE_CACHE = 'bw-sw-state';
const ASSET_STATE_KEY = '/__bw/partition';
const ASSET_MAX_CLIENTS = 64;

const assetPartitions = {
    current: null,
    issued: 0,
    applied: 0,
    byClient: new Map()
};

function assetRoute(url) {
    const match = ASSET_ROUTE.exec(url.pathname);
    if (!match) {
        return null;
    }
    return { sha: match[1], width: match[2] ? Number(match[2]) : null };
}

function assetKey(route) {
    return `${self.location.origin}/__bw/${route.sha}` + (route.width === null ? '' : `/${route.width}`);
}

// Only a real identity label caches. `none`, `offline` and anything malformed are network-only.
function isCachingPartition(label) {
    return typeof label === 'string' && ASSET_LABEL.test(label);
}

function readStoredPartition() {
    return caches.match(ASSET_STATE_KEY, { cacheName: ASSET_STATE_CACHE })
        .then(stored => (stored ? stored.text() : null))
        .catch(() => null);
}

function storePartition(label) {
    return caches.open(ASSET_STATE_CACHE)
        .then(cache => cache.put(ASSET_STATE_KEY, new Response(label)))
        .catch(() => undefined);
}

// The server's answer for the cookie this request carries. Answers apply in the order they were asked, so a slow
// lookup never overwrites a newer one.
function lookupPartition() {
    const seq = ++assetPartitions.issued;
    return fetch(ASSET_PARTITION_URL, { credentials: 'same-origin', cache: 'no-store' })
        .then(
            response => (response.ok ? response.json().then(body => body && body.partition, () => null) : null),
            () => undefined)
        .then(answer => {
            if (answer === undefined) {
                return lastKnownPartition();
            }
            const label = isCachingPartition(answer) ? answer : 'none';
            if (seq > assetPartitions.applied) {
                assetPartitions.applied = seq;
                if (label !== assetPartitions.current) {
                    assetPartitions.current = label;
                    storePartition(label);
                }
            }
            return label;
        })
        .catch(() => 'none');
}

function lastKnownPartition() {
    if (assetPartitions.current !== null) {
        return Promise.resolve(assetPartitions.current);
    }
    return readStoredPartition().then(stored => {
        const label = isCachingPartition(stored) ? stored : 'none';
        if (assetPartitions.current === null) {
            assetPartitions.current = label;
        }
        return assetPartitions.current;
    });
}

function partitionFor(clientId) {
    if (!clientId) {
        return Promise.resolve('none');
    }
    let pending = assetPartitions.byClient.get(clientId);
    if (!pending) {
        pending = lookupPartition();
        assetPartitions.byClient.set(clientId, pending);
        if (assetPartitions.byClient.size > ASSET_MAX_CLIENTS) {
            assetPartitions.byClient.delete(assetPartitions.byClient.keys().next().value);
        }
    }
    return pending;
}

function assetCacheNames(partition) {
    return [`bw-a1-${partition}-img`, `bw-a1-${partition}-3d`];
}

function matchAsset(partition, key) {
    const [img, view3d] = assetCacheNames(partition);
    const find = cacheName => caches.match(key, { cacheName, ignoreVary: true });
    return find(img)
        .then(hit => hit || find(view3d))
        .catch(() => undefined);
}

function isStorableAsset(response) {
    return !!response
        && response.status === 200
        && response.type === 'basic'
        && !/no-store/i.test(response.headers.get('cache-control') || '');
}

function storeAsset(event, copy, route, key, partition) {
    if (assetPartitions.current !== partition) {
        return Promise.resolve();
    }
    const type = copy.headers.get('content-type') || '';
    const [img, view3d] = assetCacheNames(partition);
    const length = Number(copy.headers.get('content-length'));
    return caches.open(type.startsWith('image/') ? img : view3d)
        .then(cache => {
            // Re-read the label after the await: a switch while the bytes were in flight discards them.
            if (assetPartitions.current !== partition) {
                return undefined;
            }
            return cache.put(key, copy).then(() => announceCached(event.clientId, route, length));
        })
        .catch(() => undefined);
}

function announceCached(clientId, route, length) {
    return self.clients.get(clientId)
        .then(client => {
            if (client) {
                client.postMessage({
                    proto: PROTO,
                    type: 'asset-cached',
                    sha: route.sha,
                    width: route.width,
                    bytes: Number.isFinite(length) && length > 0 ? length : null
                });
            }
        })
        .catch(() => undefined);
}

function fetchAndStoreAsset(event, route, key, partition) {
    return fetch(event.request).then(response => {
        if (isStorableAsset(response)) {
            event.waitUntil(storeAsset(event, response.clone(), route, key, partition));
        }
        return response;
    });
}

// The fetch handler's entry point. Never rejects on a cache problem: only a failing network fetch reaches the page.
function respondFromAssetCache(event, route) {
    return partitionFor(event.clientId).then(partition => {
        if (!isCachingPartition(partition) || partition !== assetPartitions.current) {
            return fetch(event.request);
        }
        const key = assetKey(route);
        return matchAsset(partition, key)
            .then(cached => cached || fetchAndStoreAsset(event, route, key, partition));
    });
}
