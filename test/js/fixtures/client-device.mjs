// A device for the offline client's store tests (client-store*.test.mjs): a client sandbox whose ledger holds walls
// W1 and W2 for a label (each with a wall and a comments document cached in -3d), their marks, and the same for other
// partitions. An `anon` row keeps the share hash of TOKEN, as asset-manifest.js writes it for a share link.
import { createHash } from 'node:crypto';
import assert from 'node:assert/strict';
import { loadClient, bodyCaches } from './client-sandbox.mjs';
import { ORIGIN } from './page-sandbox.mjs';
import { W1, W2 } from './asset-pages.mjs';

export const DAY = 24 * 60 * 60 * 1000;
export const TOKEN = 'tok3n';
export const plain = value => JSON.parse(JSON.stringify(value));
// The sandbox's arrays are of another realm: compare them as plain JSON.
export const same = (actual, expected, message) => assert.deepEqual(plain(actual), plain(expected), message ?? '');
// A 64-digit sha from a short hex pattern.
export const sha = pattern => pattern.repeat(64).slice(0, 64);
export const doc = (kind, wallId, extra = {}) => JSON.stringify(Object.assign({ v: 1, wallId, kind }, extra));
export const shareHash = token => createHash('sha256').update(token).digest('hex');
export const cacheNames = caches => [...caches.stores.keys()].sort();
const tick = id => ({ boulderId: id, attempts: 2, sent: false, flashed: true, lastAt: null });

/** more: loadClient's options, plus others (more labels to fill), meta (merged into the ledger's), share (anon's token). */
export async function device(label, more = {}) {
    const caches = more.caches || bodyCaches();
    const client = loadClient(Object.assign({ caches }, more));
    const ledger = await client.ledger();
    for (const p of [label].concat(more.others || [])) {
        for (const [wallId, c] of [[W1, '1'], [W2, '2']]) {
            const shas = { wall: sha(c + 'a'), comments: sha(c + 'c') };
            const share = p === 'anon' ? shareHash(more.share ?? TOKEN) : undefined;
            await ledger.updateWall(p, wallId, () => ({ partition: p, wallId, member: p.startsWith('u-'), share, data: { wall: { sha: shas.wall, bytes: 1, cursor: '1.2.3' }, comments: { sha: shas.comments, bytes: 1, cursor: '1.2.3' } } }));
            await caches.putText(`bw-a1-${p}-3d`, `${ORIGIN}/__bw/${shas.wall}`, doc('wall', wallId));
            await caches.putText(`bw-a1-${p}-3d`, `${ORIGIN}/__bw/${shas.comments}`, doc('comments', wallId));
            await ledger.putMine({ partition: p, wallId, cursor: '1.2.3', ticks: [tick(W1)], favourites: [], fetchedAt: 1 });
        }
    }
    await ledger.setMeta(Object.assign({ activePartition: label }, more.meta || {}));
    return { client, ledger, caches };
}
