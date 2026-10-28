# Released worker (frozen)

`service-worker.js` and `sw-asset-cache.js` exactly as main `7140dad` (#203) ships them: the worker installed apps
on prod run until they update. `tests/cache-js-move.spec.mjs` serves them through the proxy's `body` rule in place of
this build's, to test a page of this build under the released worker and the worker's update (PR 3.7). Never edit
them; refreeze them only when the released worker changes and a test needs that release.
