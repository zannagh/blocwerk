// The resume cookie (prefs.js): a boulder page is remembered as the boulder list it was opened from, so coming back lands
// on that list and not on the boulder browser's cards that a bare ?view=boulders opens.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';

const source = readFileSync(new URL('../../src/Blocwerk.Web/wwwroot/js/prefs.js', import.meta.url), 'utf8');
const WALL = '11111111-1111-1111-1111-111111111111';
const BOULDER = '22222222-2222-2222-2222-222222222222';

// Loads prefs.js on a page at `path` + `search` and returns the last-page cookie it wrote.
function lastPage(path, search = '') {
    const cookies = [];
    const document = {
        readyState: 'complete',
        get cookie() { return ''; },
        set cookie(value) { cookies.push(value); },
        addEventListener() { },
    };
    const window = { addEventListener() { } };
    const location = { pathname: path, search, protocol: 'http:' };
    vm.runInNewContext(source, { window, document, location, setInterval: () => 0, clearInterval() { }, URLSearchParams });
    const written = cookies.find(c => c.startsWith('blocwerk-last-page='));
    return written ? decodeURIComponent(written.split(';')[0].slice('blocwerk-last-page='.length)) : null;
}

test('a boulder opened from a hold type list resumes on that list', () => {
    assert.equal(lastPage(`/walls/${WALL}/boulders/${BOULDER}`, '?hold=crimp'), `/walls/${WALL}?view=boulders&hold=crimp`);
});

test('a boulder opened from a style list resumes on that list', () => {
    assert.equal(lastPage(`/walls/${WALL}/boulders/${BOULDER}`, '?style=power'), `/walls/${WALL}?view=boulders&style=power`);
});

test('a boulder opened from the favourites list resumes on that list', () => {
    assert.equal(lastPage(`/walls/${WALL}/boulders/${BOULDER}`, '?mine=favorites'), `/walls/${WALL}?view=boulders&mine=favorites`);
});

test('a boulder opened without a list resumes on the whole list, not on the cards', () => {
    assert.equal(lastPage(`/walls/${WALL}/boulders/${BOULDER}`), `/walls/${WALL}?view=boulders&style=all`);
});

test('any other page is remembered as it is', () => {
    assert.equal(lastPage(`/walls/${WALL}`, '?view=boulders&browse=holds'), `/walls/${WALL}?view=boulders&browse=holds`);
});
