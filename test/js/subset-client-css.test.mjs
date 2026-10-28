// The offline client's generated stylesheets (build/subset-client-css.mjs: client-base, client-components and
// client-wall3d) are committed. A Server CSS edit or a new class in the client's markup without a rerun of the script
// would leave the client's copy stale while the versioned modules (wall3d.js) move on. Run by `node --test test/js/`.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { build, OUTPUTS } from '../../build/subset-client-css.mjs';

test('every generated client stylesheet is what the script builds now (rerun node build/subset-client-css.mjs)', () => {
    for (const [name, sources] of Object.entries(OUTPUTS)) {
        const committed = readFileSync(new URL(`../../src/Blocwerk.Client/wwwroot/css/${name}`, import.meta.url), 'utf8');
        assert.ok(build(sources) === committed, `${name} is stale`);
    }
});
