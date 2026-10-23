/*
 * The offline client's precache (plan 2026-10-22 §1, PR 3.9), loaded into service-worker.js by importScripts after
 * sw-client-cache.js, whose state, names and helpers it uses; see PRECACHE there. Split out to keep each worker file
 * short; it runs only while the server is reachable (a page asked for it).
 */
const clientMeta = (html, name) => {
    const match = new RegExp(`<meta name="${name}" content="([^"]*)"`).exec(html);
    return match ? match[1] : null;
};

// The shell of the deploy the worker reaches: its markup, version and routes. A redirect (a kiosk session is sent
// elsewhere) or any other answer than the shell's 200 fails the fill.
function readClientShell() {
    return fetch(CLIENT_SHELL_PATH, { credentials: 'same-origin', cache: 'no-store', redirect: 'error' }).then(response => {
        if (!clientFileOk(response)) {
            throw new Error('client shell: ' + response.status);
        }
        return response.text().then(html => {
            const version = clientMeta(html, 'bw-client-version');
            const routes = clientMeta(html, 'bw-client-routes');
            if (!CLIENT_VERSION.test(version || '') || routes === null) {
                throw new Error('client shell: no version');
            }
            // Only the type: the body is decoded text now, so the network's encoding and length no longer apply.
            const headers = { 'content-type': response.headers.get('content-type') || 'text/html; charset=utf-8' };
            return { html, version, routes: routes.split(/\s+/).filter(Boolean), headers };
        });
    });
}

// Every root-relative file the shell names: script and stylesheet URLs, and the import map's modules.
function clientShellFiles(html) {
    const files = [];
    const tag = /<(?:script|link)\b[^>]*?\s(?:src|href)="(\/(?!\/)[^"]*)"/g;
    for (let match = tag.exec(html); match; match = tag.exec(html)) {
        files.push(match[1]);
    }
    const map = /<script type="importmap"[^>]*>([\s\S]*?)<\/script>/.exec(html);
    const imports = map ? (JSON.parse(map[1]).imports || {}) : {};
    files.push(...Object.values(imports).filter(url => typeof url === 'string' && /^\/(?!\/)/.test(url)));
    return { files, dotnet: imports['/v/_framework/dotnet.js'] || null };
}

// The eager files of dotnet.js's boot manifest, beside dotnet.js. An SDK that changes the layout fails the fill.
function clientBootFiles(dotnetUrl, source) {
    const start = source.indexOf('/*json-start*/');
    const end = source.indexOf('/*json-end*/');
    if (start < 0 || end < start) {
        throw new Error('client boot manifest: not found');
    }
    const resources = JSON.parse(source.slice(start + 14, end)).resources || {};
    const base = dotnetUrl.slice(0, dotnetUrl.lastIndexOf('/') + 1);
    return CLIENT_BOOT_KINDS
        .flatMap(kind => (Array.isArray(resources[kind]) ? resources[kind] : []))
        .map(resource => resource && resource.name)
        .filter(name => typeof name === 'string' && /^[\w.-]+$/.test(name))
        .map(name => base + name);
}

// One file into the version's cache, unless it is already there (a resumed fill). Resolves with its response.
function storeClientFile(cache, path) {
    const key = clientUrl(path);
    return cache.match(key).then(hit => hit || fetch(key, { credentials: 'same-origin' }).then(response => {
        if (!clientFileOk(response)) {
            throw new Error('client file: ' + path);
        }
        return cache.put(key, response.clone()).then(() => response);
    }));
}

function storeClientFiles(cache, paths) {
    const queue = [...new Set(paths)];
    const next = () => (queue.length > 0 ? storeClientFile(cache, queue.shift()).then(next) : Promise.resolve());
    return Promise.all(Array.from({ length: Math.min(CLIENT_FILL_PARALLEL, queue.length) }, next));
}

// The files first, the shell last: a version's cache holds a shell only when everything it names is there.
function fillClientVersion(cache, shell) {
    const { files, dotnet } = clientShellFiles(shell.html);
    if (!dotnet) {
        throw new Error('client shell: no dotnet.js');
    }
    return storeClientFile(cache, dotnet)
        .then(response => response.text())
        .then(source => storeClientFiles(cache, [...files, ...clientBootFiles(dotnet, source)]))
        .then(() => cache.put(clientUrl(CLIENT_SHELL_PATH), new Response(shell.html, { status: 200, headers: shell.headers })));
}

// The completeness record, then every other version goes.
function commitClient(shell) {
    const record = { version: shell.version, routes: shell.routes };
    return caches.open(ASSET_STATE_CACHE)
        .then(cache => cache.put(CLIENT_STATE_KEY, new Response(JSON.stringify(record), { headers: { 'content-type': 'application/json' } })))
        .then(() => {
            clientState.record = record;
            clientState.read = Promise.resolve(record);
            return caches.keys();
        })
        .then(names => Promise.all(names
            .filter(name => name.startsWith(CLIENT_CACHE_PREFIX) && name !== clientCacheName(record.version))
            .map(name => caches.delete(name))))
        .then(() => record.version);
}

function fillClient() {
    const connection = self.navigator && self.navigator.connection;
    if (connection && connection.saveData) {
        return Promise.reject(new Error('client precache: Save-Data'));
    }
    return lastKnownPartition()
        .then(partition => {
            // A kiosk gets no offline client until PR 3.15 (the server refuses it /v as well).
            if (/^k-/.test(partition || '')) {
                throw new Error('client precache: kiosk');
            }
            return Promise.all([readClientShell(), completeClient(true)]);
        })
        .then(([shell, record]) => caches.open(clientCacheName(shell.version)).then(cache => cache.match(clientUrl(CLIENT_SHELL_PATH))
            .then(present => (record && record.version === shell.version && present
                ? shell.version
                : fillClientVersion(cache, shell).then(() => commitClient(shell))))));
}

// `client-precache`: one fill at a time; resolves `{ version, complete }` (version null when it is unknown).
function precacheClient() {
    if (!clientState.filling) {
        clientState.filling = fillClient()
            .then(version => ({ version, complete: true }), () => ({ version: clientState.record ? clientState.record.version : null, complete: false }))
            .then(result => {
                clientState.filling = null;
                return result;
            });
    }
    return clientState.filling;
}

