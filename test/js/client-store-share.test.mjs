// What the offline client's store (Phase 3 PR 3.10) knows of each cached wall: an `anon` row is read only on a /v/
// page whose share token hashes to the row's stored hash (the token itself is never stored); a row carries `member`
// (its documents' class, from the ledger, not the URL) and a key that changes only with what the ledger names for it.
// Run by `node --test test/js/`.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { loadPage, USER_A } from './fixtures/page-sandbox.mjs';
import { W1, W2, listing } from './fixtures/asset-pages.mjs';
import { TOKEN, device, plain, sha, shareHash } from './fixtures/client-device.mjs';

const sharePath = (wallId, token) => `/v/walls/${wallId}/shared/${token}`;

test('anon reads a wall only when the page\'s token hashes to the row\'s: a guessed token opens nothing', async () => {
    const right = await device('anon', { path: sharePath(W1, TOKEN) });
    assert.equal((await right.client.store.snapshot()).state, 'ready');
    assert.ok(await right.client.store.docs('anon', W1));

    const guessed = await device('anon', { path: sharePath(W1, 'anything') });
    assert.equal((await guessed.client.store.snapshot()).state, 'empty');
    assert.equal(await guessed.client.store.docs('anon', W1), null);
    assert.equal((await guessed.ledger.wallsOf('anon')).length, 2, 'a wrong token reads nothing, and wipes nothing either');
});

test('an anon row with no stored hash, or a browser without WebCrypto, reads nothing', async () => {
    const legacy = await device('anon', { path: sharePath(W1, TOKEN) });
    await legacy.ledger.updateWall('anon', W1, row => Object.assign(row, { share: undefined }));
    assert.equal((await legacy.client.store.snapshot()).state, 'empty');
    assert.equal(await legacy.client.store.docs('anon', W1), null);

    const bare = await device('anon', { path: sharePath(W1, TOKEN), noCrypto: true });
    assert.equal((await bare.client.store.snapshot()).state, 'empty');
});

test('a share page\'s manifest round stores the token\'s hash on the anon row, never the token', async () => {
    const page = loadPage({ label: 'anon', path: `/walls/${W1}/shared/${TOKEN}`, swReply: 'none', routes: listing(W1, sha('c')) });
    await page.boot();
    const row = await (await page.ledger()).getWall('anon', W1);
    assert.equal(row.share, shareHash(TOKEN));
    assert.ok(!JSON.stringify(row).includes(TOKEN), 'the raw token is nowhere in the row');

    const member = loadPage({ label: USER_A, path: `/walls/${W1}/shared/${TOKEN}`, swReply: 'none', summary: { wallsWith3d: 0, walls: [] }, routes: listing(W1, sha('1')) });
    await member.boot();
    assert.equal((await (await member.ledger()).getWall(USER_A, W1)).share, null, 'only anon rows keep a hash');
});

test('each row carries its class from the ledger (member) and a key that moves only with its documents', async () => {
    const { client, ledger } = await device(USER_A);
    await ledger.updateWall(USER_A, W2, row => Object.assign(row, { member: false }));
    const first = plain(await client.store.snapshot());
    const byId = id => first.walls.find(w => w.wallId === id);
    assert.equal(byId(W1).member, true);
    assert.equal(byId(W2).member, false, 'a share link\'s documents, though this /v/ URL carries no token');

    const read = plain(await client.store.docs(USER_A, W1));
    assert.equal(read.key, byId(W1).key, 'the documents name the row they were read from');

    await ledger.updateWall(USER_A, W1, row => Object.assign(row, { fetchedAt: 99, lastOpened: 5 }));
    assert.equal(plain(await client.store.snapshot()).walls.find(w => w.wallId === W1).key, byId(W1).key, 'other fields do not move it');
    await ledger.updateWall(USER_A, W1, row => Object.assign(row, { dataPrev: { wall: sha('e') } }));
    assert.notEqual(plain(await client.store.snapshot()).walls.find(w => w.wallId === W1).key, byId(W1).key, 'a predecessor standing in does');
});
