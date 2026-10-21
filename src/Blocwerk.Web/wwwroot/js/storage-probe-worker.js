/*
 * The storage probe's OPFS writer for engines without FileSystemFileHandle.createWritable (iOS Safari before
 * 26 has only the synchronous access handle, and only inside a dedicated worker). Messages:
 *
 *   { op: 'init', coreUrl }                 -> { ok, syncAccessHandle }   (loads storage-probe-core.js)
 *   { op: 'write', name, size }             -> { ok, bytes, ms } | { ok: false, error }
 *   { op: 'writeText', name, text }         -> { ok } | { ok: false, error }
 *
 * Reads happen on the page (File.arrayBuffer / File.stream), which is what a splat loader does.
 */
'use strict';

let core = null;

function describe(e) {
    return (e && (e.name ? e.name + ': ' : '') + (e.message || String(e))) || 'unknown error';
}

async function openHandle(name) {
    const dir = await navigator.storage.getDirectory();
    const file = await dir.getFileHandle(name, { create: true });
    if (typeof file.createSyncAccessHandle !== 'function') {
        throw new Error('unsupported: no createSyncAccessHandle');
    }
    return file.createSyncAccessHandle();
}

async function write(name, size) {
    const started = performance.now();
    const handle = await openHandle(name);
    try {
        handle.truncate(0);
        let offset = 0;
        while (offset < size) {
            const length = Math.min(core.CHUNK, size - offset);
            const chunk = core.fillChunk(new Uint8Array(length), offset);
            const written = handle.write(chunk, { at: offset });
            if (written !== length) {
                throw new Error('short write at ' + offset + ': ' + written + ' of ' + length);
            }
            offset += length;
        }
        handle.flush();
        return { ok: true, bytes: offset, ms: Math.round(performance.now() - started) };
    } finally {
        handle.close();
    }
}

async function writeText(name, text) {
    const handle = await openHandle(name);
    try {
        const bytes = new TextEncoder().encode(text);
        handle.truncate(0);
        handle.write(bytes, { at: 0 });
        handle.flush();
        return { ok: true };
    } finally {
        handle.close();
    }
}

self.onmessage = async event => {
    const msg = event.data || {};
    let reply;
    try {
        if (msg.op === 'init') {
            importScripts(msg.coreUrl);
            core = self.bwStorageProbeCore;
            const probe = typeof FileSystemFileHandle !== 'undefined'
                && typeof FileSystemFileHandle.prototype.createSyncAccessHandle === 'function';
            reply = { ok: true, syncAccessHandle: probe, opfs: !!(navigator.storage && navigator.storage.getDirectory) };
        } else if (msg.op === 'write') {
            reply = await write(msg.name, msg.size);
        } else if (msg.op === 'writeText') {
            reply = await writeText(msg.name, msg.text);
        } else {
            reply = { ok: false, error: 'unknown op ' + msg.op };
        }
    } catch (e) {
        reply = { ok: false, error: describe(e) };
    }
    self.postMessage(Object.assign({ id: msg.id }, reply));
};
