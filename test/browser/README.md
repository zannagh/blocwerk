# Browser tests (service worker and asset cache)

Real-browser tests of `wwwroot/js/service-worker.js`, `sw-asset-cache.js` and the page-side asset cache
(`asset-*.js`), against the real app. Design: `.me/design-2026-10-21-local-first.md`, §6.6 to §6.11 (PR 2.10).

## Run

```sh
cd test/browser
npm ci
npm run browsers   # once: Playwright's Chromium and WebKit
npm test
```

Prerequisites: Docker, the .NET SDK from `global.json`, Node 20 or later. `npm test` starts everything it needs and
removes it again. The first run takes about a minute longer, for the publish and the seed.

- `BW_BROWSERS=chromium npm test` runs one browser (default `chromium,webkit`).
- `npx playwright test tests/sw-partition.spec.mjs` runs one file.
- `BW_BROWSER_PG=postgres://user:pw@127.0.0.1:port/bw_browser_x` uses a Postgres server you provide instead of a
  container (CI does this). The host must be `localhost` or `127.0.0.1` and the database name must start with
  `bw_browser_`: the harness drops and recreates that database at every start, so re-runs begin from the same seed.
- In CI a test gets one retry. `harness/flaky-report.mjs` reads the JSON report and annotates every test that passed
  only on its retry with a `::warning::`.

## What it starts (`harness/environment.mjs`)

1. A throwaway `postgres:17` container on a random local port, with a random password.
2. `dotnet publish` of `Blocwerk.Web` into `.out/web`. The app runs from that output, so `wwwroot` in the source tree is
   never served live. The publish refuses to run if `src/Blocwerk.Web/appsettings.json` exists, because it would copy
   that file into the app.
3. `seed/` (`Blocwerk.Browser.Seed`), which migrates the database and writes a fixed world to `.out/seed.json`:
   - Alice's wall *Alpha*: 2 panels, an active 3D model (texture, mask, splat), a beta-video thumbnail, a kiosk key,
     and Carol as a kiosk-consented member;
   - Alice's wall *Beta*: 1 panel;
   - Bob's wall *Bobs*: 1 panel.
4. The app in `Development` (for `/dev/login`), with an environment built from scratch: no variable from your shell
   except `PATH`, `DOTNET_ROOT`, `TMPDIR` and `LANG`. `HOME` points at a temp directory, so the data-protection keys
   are throwaway too.
5. `harness/proxy.mjs`, a same-origin proxy in front of the app. The browser talks only to it. It adds a request log
   (to prove a request never reached the server), fault rules (404/503/delay/dropped connection/body rewrite per
   path), and fixed Clear-Site-Data probe pages under `/__pw/`.

## Layout

- `tests/sw-*.spec.mjs`: the worker alone (`workerOnly`, which sets `window.__bwAssetCacheManual` so the page
  modules never boot): install and activate, `/a/` routing, offline, partitions, kiosk no-store, and a slow
  partition lookup (`sw-slow-lookup`: read-only cache hits after 2.5 s, later requests at once).
- `tests/clear-site-data.spec.mjs`, with the worker blocked: what each engine does with `Clear-Site-Data: "cache"`
  (WebKit ignores it, even on a 200), and that an `/a/` asset (`private, no-cache`) is never answered from the HTTP
  cache for the next identity.
- `tests/logout.spec.mjs`, `tests/page-fill.spec.mjs`, the last test of `sw-partition`, and the second test of `quota`:
  the 2.7 page modules. They skip themselves on a build without `asset-cache.js`.
- `tests/cache-js-move.spec.mjs`: the PR 3.7 move of the cache modules to `/_content/Blocwerk.Shared.Ui/js/`, under the
  released worker (`released/`, frozen from main) and its update, and an old document's enhanced navigation.
- `tests/quota.spec.mjs`: refused writes, forced with Chromium's `Storage.overrideQuotaForOrigin` (Chromium only).

## Engine notes

- **WebKit runs in a persistent context** (a fresh profile per test, as a Safari tab has). In Playwright's ephemeral
  WebKit contexts, Cache Storage entries written while the origin has no service worker registration are dropped on
  the next navigation. A real profile does not do that.
- **WebKit's `context.setOffline(true)` fails requests before the worker sees them**, cached ones included, so the
  offline test uses a dropped connection at the proxy on WebKit instead. Chromium runs both.
- **Chromium hides `Clear-Site-Data` from Playwright's response headers.** The header test reads it from the test
  process.
- Requests with `Range` are checked in both browsers. The quota override exists only in Chromium.
