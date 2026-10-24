/*
 * A same-origin test proxy in front of the real app. The browser only ever talks to this proxy, which forwards
 * everything (WebSockets included, for the Blazor circuit) to the app unchanged, and adds three things the tests
 * cannot get from Playwright's own routing once a service worker sits between the page and the network:
 *
 *   - a request log (GET /__pw/log), so a test can prove a request never reached the server;
 *   - fault rules (POST /__pw/faults), so a test can make one path answer 404 or 503, or drop the connection (`drop`,
 *     that path unreachable), delay it (`delay` ms), or rewrite a page's body (`rewrite`), without touching the app or
 *     the database;
 *   - fixed pages for the Clear-Site-Data probes (GET /__pw/csd?status=200|302, /__pw/cacheable, /__pw/blank);
 *   - the server unreachable as a whole (POST /__pw/unreachable): every open connection is closed and the port stops
 *     listening, so a connect is refused, as with a server that is down. Cleared by the next POST /__pw/faults.
 *
 * Prefer `unreachable` over a `drop` rule for '.*' wherever a test means "no network": Playwright's Linux WebKit
 * (WPE, libsoup) crashes its network process now and then when requests die on open connections, and that process
 * holds the service worker registry, so the page silently loses its worker. A refused connect never does that.
 *
 * The control API (log, mark, faults) is also served on a port of its own, which stays up while the proxy's port is
 * closed; `unreachable` is served only there.
 *
 * Control paths are under /__pw/ and never forwarded. The proxy holds no state the app relies on.
 */
import { createHash } from 'node:crypto';
import http from 'node:http';
import net from 'node:net';

const CONTROL = '/__pw/';

export async function startProxy(appPort) {
    const state = { log: [], faults: [], setUnreachable: null };

    const server = http.createServer((req, res) => {
        // The control API is always answered; the probe pages under /__pw/ are subject to the fault rules like the app.
        if (isControlApi(req.url)) {
            control(state, req, res);
            return;
        }
        // `cookie`: a short fingerprint of the Cookie header, so a test can tell which identity a request carried.
        const cookie = req.headers.cookie ? createHash('sha256').update(req.headers.cookie).digest('hex').slice(0, 8) : null;
        const entry = { seq: state.log.length, method: req.method, url: req.url, status: null, cookie };
        state.log.push(entry);
        const fault = state.faults.find(f => (!f.method || f.method === req.method) && new RegExp(f.path).test(req.url.split('?')[0]));
        if (fault && fault.drop) {
            // The connection dies without an answer. For the whole server down, see `unreachable` above.
            entry.fault = true;
            req.socket.destroy();
            return;
        }
        if (fault && fault.delay) {
            // A slow server: the request is forwarded unchanged after `delay` ms.
            setTimeout(() => forward(appPort, req, res, entry), fault.delay);
            return;
        }
        if (fault && fault.rewrite) {
            forwardRewritten(appPort, req, res, entry, fault.rewrite);
            return;
        }
        if (fault) {
            entry.status = fault.status;
            entry.fault = true;
            res.writeHead(fault.status, { 'content-type': 'text/plain', 'cache-control': 'no-store' });
            res.end(`fault ${fault.status}`);
            return;
        }
        if (req.url.startsWith(CONTROL)) {
            control(state, req, res);
            return;
        }
        forward(appPort, req, res, entry);
    });

    server.on('upgrade', (req, socket, head) => tunnel(appPort, req, socket, head));

    const sockets = new Set();
    server.on('connection', socket => {
        sockets.add(socket);
        socket.on('close', () => sockets.delete(socket));
    });
    const port = await listen(server, 0);
    state.setUnreachable = async on => {
        if (on && server.listening) {
            const closed = new Promise(resolve => server.close(() => resolve()));
            for (const socket of sockets) {
                socket.destroy();
            }
            await closed;
        } else if (!on && !server.listening) {
            await listen(server, port);
        }
    };

    const controlServer = http.createServer((req, res) => control(state, req, res, true));
    const controlPort = await listen(controlServer, 0);
    return {
        port,
        controlPort,
        close: async () => {
            await state.setUnreachable(false);
            await Promise.all([closeServer(server), closeServer(controlServer)]);
        }
    };
}

function listen(server, port) {
    return new Promise((resolve, reject) => {
        server.once('error', reject);
        server.listen(port, '127.0.0.1', () => {
            server.off('error', reject);
            resolve(server.address().port);
        });
    });
}

function isControlApi(url) {
    return /^\/__pw\/(log|mark|faults)(\?|$)/.test(url);
}

function forward(appPort, req, res, entry) {
    const upstream = http.request({
        host: '127.0.0.1', port: appPort, method: req.method, path: req.url, headers: req.headers
    }, answer => {
        entry.status = answer.statusCode;
        res.writeHead(answer.statusCode, answer.rawHeaders);
        answer.pipe(res);
    });
    upstream.on('error', () => {
        entry.status = 502;
        if (!res.headersSent) {
            res.writeHead(502, { 'content-type': 'text/plain' });
        }
        res.end('proxy: app unreachable');
    });
    req.pipe(upstream);
}

// A `rewrite: [from, to]` rule: the app's answer with every `from` in its body replaced (uncompressed, so it can be read).
function forwardRewritten(appPort, req, res, entry, [from, to]) {
    const { 'accept-encoding': _, ...headers } = req.headers;
    entry.fault = true;
    const upstream = http.request({ host: '127.0.0.1', port: appPort, method: req.method, path: req.url, headers }, answer => {
        const chunks = [];
        answer.on('data', c => chunks.push(c));
        answer.on('end', () => {
            const body = Buffer.from(Buffer.concat(chunks).toString('utf8').split(from).join(to), 'utf8');
            const { 'content-length': __, 'transfer-encoding': ___, ...rest } = answer.headers;
            entry.status = answer.statusCode;
            res.writeHead(answer.statusCode, { ...rest, 'content-length': body.length });
            res.end(body);
        });
    });
    upstream.on('error', () => res.destroy());
    req.pipe(upstream);
}

// A raw TCP tunnel for the WebSocket upgrade: the request head is replayed, then both sockets are piped.
function tunnel(appPort, req, socket, head) {
    const upstream = net.connect(appPort, '127.0.0.1', () => {
        const lines = [`${req.method} ${req.url} HTTP/${req.httpVersion}`];
        for (let i = 0; i < req.rawHeaders.length; i += 2) {
            lines.push(`${req.rawHeaders[i]}: ${req.rawHeaders[i + 1]}`);
        }
        upstream.write(lines.join('\r\n') + '\r\n\r\n');
        if (head && head.length) {
            upstream.write(head);
        }
        socket.pipe(upstream).pipe(socket);
    });
    const close = () => { socket.destroy(); upstream.destroy(); };
    upstream.on('error', close);
    socket.on('error', close);
}

function control(state, req, res, ownPort = false) {
    const url = new URL(req.url, 'http://proxy');
    const send = (status, body, headers = {}) => {
        res.writeHead(status, { 'cache-control': 'no-store', ...headers });
        res.end(body);
    };
    if (url.pathname === '/__pw/log') {
        const since = Number(url.searchParams.get('since') || 0);
        send(200, JSON.stringify(state.log.slice(since)), { 'content-type': 'application/json' });
        return;
    }
    if (url.pathname === '/__pw/mark') {
        send(200, JSON.stringify({ seq: state.log.length }), { 'content-type': 'application/json' });
        return;
    }
    if (url.pathname === '/__pw/faults' && req.method === 'POST') {
        readJson(req).then(async body => {
            state.faults = Array.isArray(body) ? body : [];
            await state.setUnreachable(false);
            send(204, '');
        }, () => send(400, 'bad json'));
        return;
    }
    // Only on the control port: on the proxy's own port the answer would die with the connections it closes.
    if (ownPort && url.pathname === '/__pw/unreachable' && req.method === 'POST') {
        state.setUnreachable(true).then(() => send(204, ''), () => send(500, 'could not close'));
        return;
    }
    if (url.pathname === '/__pw/csd') {
        csdPage(url.searchParams.get('status'), send);
        return;
    }
    if (url.pathname === '/__pw/cacheable') {
        // Fresh for an hour, so the HTTP cache answers it without the server: the probe for what Clear-Site-Data clears.
        send(200, `cacheable ${url.searchParams.get('n') || ''}`, { 'content-type': 'text/plain', 'cache-control': 'private, max-age=3600' });
        return;
    }
    if (url.pathname === '/__pw/blank') {
        send(200, '<!doctype html><title>blank</title><p>blank</p>', { 'content-type': 'text/html; charset=utf-8' });
        return;
    }
    send(404, 'unknown control path');
}

// The Clear-Site-Data probe: the same header on a redirect or on a 200 page, so a test can see which one a browser obeys.
function csdPage(status, send) {
    const headers = { 'clear-site-data': '"cache"' };
    if (status === '302') {
        send(302, '', { ...headers, location: '/__pw/blank' });
        return;
    }
    send(200, '<!doctype html><title>cleared</title><p>cleared</p>', { ...headers, 'content-type': 'text/html; charset=utf-8' });
}

function readJson(req) {
    return new Promise((resolve, reject) => {
        let body = '';
        req.on('data', chunk => { body += chunk; });
        req.on('end', () => {
            try {
                resolve(JSON.parse(body || 'null'));
            } catch (e) {
                reject(e);
            }
        });
        req.on('error', reject);
    });
}

function closeServer(server) {
    return new Promise(resolve => {
        server.close(() => resolve());
        server.closeAllConnections?.();
    });
}
