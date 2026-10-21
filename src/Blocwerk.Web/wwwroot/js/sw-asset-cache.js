/*
 * The on-device wall-asset cache (design 2026-10-21 §6.6, §6.7), loaded into service-worker.js by importScripts.
 *
 * GET /api/walls/{w}/a/{sha} and /a/{sha}/{width} are content-addressed (their bytes never change), so they are served
 * cache-first from `bw-a1-{partition}-img` (images) or `-3d` (everything else), keyed `/__bw/{sha}[/{width}]`: the
 * key drops the wall, `token`, `rev` and `bwretry`, because the bytes are fully named by the sha. A miss goes to the
 * network and only a plain 200 whose ETag names the sha is stored (a proxy's 200 error page carries none). 404s, 206s, Range requests and anything uncacheable stay network-only.
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
 * OFFLINE. An unreachable lookup (no network, a 5xx such as the proxy's 502 during a deploy, a body that is not the
 * endpoint's JSON) falls back to the last label, kept in `bw-sw-state` so it survives the worker being stopped. The
 * cookie cannot change while the server is unreachable, so that is still the identity on the wire. A lookup that
 * hangs past ASSET_LOOKUP_TIMEOUT_MS is abandoned and that request goes network-only. Neither outcome is remembered
 * for the page, so its next asset asks again.
 *
 * STORAGE REFUSED. Any error from the cache (QuotaExceededError, a full device, a private window) leaves that asset
 * network-only, and a refused open or put posts `asset-refused` to the page. The page always gets the network
 * response; caching never fails a request.
 *
 * `unknown` (the server could not resolve the partition) is an answer like `none`: network-only, never the last label.
 */
const ASSET_ROUTE = /^\/api\/walls\/[^/]+\/a\/([0-9a-f]{64})(?:\/(\d{1,5}))?$/;
const ASSET_PARTITION_URL = '/api/me/cache-partition';
const ASSET_LABEL = /^(anon|[uk]-[a-z2-7]{20})$/;
const ASSET_STATE_CACHE = 'bw-sw-state';
const ASSET_STATE_KEY = '/__bw/partition';
const ASSET_MAX_CLIENTS = 64;
const ASSET_LOOKUP_TIMEOUT_MS = 2500;
// PROTO (the message protocol version) is service-worker.js's, which imports this file after declaring it.

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

// The server's answer for the cookie this request carries, as `{ label, settled }`. Only a real answer is settled
// (remembered for the page); a fallback is not. Answers apply in the order they were asked, so a slow lookup never
// overwrites a newer one.
function lookupPartition() {
    const seq = ++assetPartitions.issued;
    const controller = new AbortController();
    const timer = setTimeout(() => controller.abort(), ASSET_LOOKUP_TIMEOUT_MS);
    return fetch(ASSET_PARTITION_URL, { credentials: 'same-origin', cache: 'no-store', signal: controller.signal })
        .then(readPartitionAnswer, () => undefined)
        .then(answer => {
            if (controller.signal.aborted) {
                return { label: 'none', settled: false };
            }
            if (answer === undefined) {
                return lastKnownPartition().then(label => ({ label, settled: false }));
            }
            const label = isCachingPartition(answer) ? answer : 'none';
            if (seq > assetPartitions.applied) {
                assetPartitions.applied = seq;
                if (label !== assetPartitions.current) {
                    assetPartitions.current = label;
                    storePartition(label);
                }
            }
            return { label, settled: true };
        })
        .catch(() => ({ label: 'none', settled: false }))
        .finally(() => clearTimeout(timer));
}

// The endpoint's `partition`, or undefined when the server could not answer: a 5xx, status 0, an opaque or
// redirected response, or a body that is not its JSON. A 4xx is an answer and means `none`.
function readPartitionAnswer(response) {
    if (!response || response.type !== 'basic' || response.status === 0 || response.status >= 500) {
        return undefined;
    }
    if (!response.ok) {
        return 'none';
    }
    return response.json().then(
        body => (body && typeof body === 'object' && 'partition' in body ? body.partition : undefined),
        () => undefined);
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
    const byClient = assetPartitions.byClient;
    let pending = byClient.get(clientId);
    if (!pending) {
        pending = lookupPartition().then(result => {
            if (!result.settled && byClient.get(clientId) === pending) {
                byClient.delete(clientId);
            }
            return result.label;
        });
        byClient.set(clientId, pending);
        if (byClient.size > ASSET_MAX_CLIENTS) {
            byClient.delete(byClient.keys().next().value);
        }
    }
    return pending;
}

// `sw-refresh-partition` from a page that saw the worker serve another label than its own (sw-info's `partition`):
// forget what that page was told and ask the server again, so `current` follows the cookie even on a page that loads
// no /a/ image (which is otherwise the only thing that makes the worker look). Resolves with the fresh label.
function refreshPartition(clientId) {
    if (!clientId) {
        return lookupPartition().then(result => result.label);
    }
    assetPartitions.byClient.delete(clientId);
    return partitionFor(clientId);
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

// A no-store answer is never kept: the /a/ route sends one where the bytes must not outlive the request (a kiosk
// session asking for a kind outside the tablet's cacheable class).
function isStorableAsset(response, route) {
    return !!response
        && response.status === 200
        && response.type === 'basic'
        && (response.headers.get('etag') || '').includes(route.sha)
        && !/no-store/i.test(response.headers.get('cache-control') || '');
}

function storeAsset(clientId, copy, route, key, partition) {
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
            return cache.put(key, copy).then(
                () => announce(clientId, 'asset-cached', route, length),
                () => announce(clientId, 'asset-refused', route, length));
        })
        // An open that throws is the browser refusing storage too.
        .catch(() => announce(clientId, 'asset-refused', route, length));
}

// `asset-cached` after a put, `asset-refused` when the browser refused it (quota, a full device): only a refusal the
// worker saw counts as one on the page, never a slow write.
function announce(clientId, type, route, length) {
    return self.clients.get(clientId)
        .then(client => {
            if (client) {
                client.postMessage({
                    proto: PROTO,
                    type,
                    sha: route.sha,
                    width: route.width,
                    bytes: Number.isFinite(length) && length > 0 ? length : null
                });
            }
        })
        .catch(() => undefined);
}

// The miss skips the browser's HTTP cache (`no-store`): that cache is one per browser, not per identity, so a copy the
// previous identity loaded would otherwise answer this one without a request, and be stored in its partition.
function fetchAndStoreAsset(event, route, key, partition) {
    return fetch(event.request, { cache: 'no-store' }).then(response => {
        if (isStorableAsset(response, route)) {
            event.waitUntil(storeAsset(event.clientId, response.clone(), route, key, partition));
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
