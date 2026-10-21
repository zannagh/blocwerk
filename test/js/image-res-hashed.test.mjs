// image-res.js on content-addressed urls (AssetUrls): run by `node --test test/js/`.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { FakeImg, page } from './image-page.mjs';

// A content-addressed url (AssetUrls): the width is the path segment after the sha, the query keeps the share token.
const HASHED = '/api/walls/0b0e8a8e-3c2a-4d7e-9a51-6f1f1f1f1f1f/a/' + 'ab'.repeat(32);

test('image-res: a hashed url is upgraded by its width segment, keeping the token and never a w parameter', async () => {
    const img = new FakeImg(`${HASHED}/1280?token=t`, { complete: true, width: 1000 });
    const p = page([img]);
    p.measure(2);
    assert.equal(p.loads.at(-1).src, `${HASHED}/2560?token=t`);
    p.loads.at(-1).finish(true);
    await p.settle();
    assert.equal(img.getAttribute('src'), `${HASHED}/2560?token=t`);
});

test('image-res: a hashed url upgraded to the original drops the width segment, and a retry marker never leaks', async () => {
    const img = new FakeImg(`${HASHED}/1280?bwretry=1`, { complete: true, width: 1000 });
    const p = page([img]);
    p.measure(4);
    assert.equal(p.loads[0].src, HASHED);
    p.loads[0].finish(true);
    await p.settle();
    assert.equal(img.getAttribute('src'), HASHED);
});

test('image-res: a hashed url already at the needed width is not asked for again', () => {
    const img = new FakeImg(`${HASHED}/1920`, { complete: true, width: 600 });
    const p = page([img]);
    p.measure(1);
    assert.equal(p.loads.length, 0);
});
