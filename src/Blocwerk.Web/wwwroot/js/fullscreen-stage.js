/*
 * bwStage — the full-screen landscape takeover for a wall photo + its hold overlay.
 *
 * Registered from the app shell like every other viewport helper (BlocwerkApp.razor), so both it
 * and css/fullscreen-stage.css go through @Assets[] fingerprinting; nothing here injects a
 * stylesheet at runtime.
 *
 * DOM shape it drives (all of it rendered by Components/Shared/StageOverlay.razor):
 *   .bw-stage-overlay            fixed, inset 0 — the takeover surface
 *     .bw-stage-viewport         NEVER rotated, so its getBoundingClientRect() stays axis-true
 *       .bw-stage-rot            rotated 90deg when the device viewport is portrait
 *         .bw-stage-world        the pan/zoom layer: photo + svg overlay
 *         chrome (close button)  inside the rotated layer, so it is upright in landscape
 *
 * Landscape is reached two ways. Where the platform allows it (Android/Chrome) we ask for real
 * fullscreen plus screen.orientation.lock('landscape'), which makes the viewport itself landscape
 * and leaves the rotation at 0 — the identity case, which costs nothing. iOS Safari can do
 * neither, so the fallback rotates `.bw-stage-rot` by a quarter turn and swaps its dimensions.
 *
 * The fullscreen/orientation request itself lives in fullscreen-stage-lock.js, which arms it from
 * the tap and unwinds it if no overlay ever comes up — see there for why the two must be paired.
 *
 * The long-press magnifier lens is deliberately NOT available here: it reads the image's own
 * getBoundingClientRect() directly (viewport-gestures.js), so no adapter could fix it under a
 * rotated stage. It arms only on `.photo-editor img.wall-photo`, which the world layer is not.
 *
 * There is no dispose in the gesture recogniser, so detach() kills the model (every intent
 * becomes a no-op) and drops our own listeners; the element goes with the Blazor render.
 */
window.bwStage = (function () {
    'use strict';

    // Holds and shapes are tapped, not dragged, so a press on one must not start a pan — same rule
    // the pickers apply through the scroll model's isInteractiveTarget.
    const NO_PAN_INTERACTIVE = 'button, a, input, select, textarea, .hold-overlay circle, ' +
        '.hold-overlay rect, .hold-overlay polygon, .hold-overlay path';
    const NO_PAN_READONLY = 'button, a, input, select, textarea';

    // Same quiet window as the orientation coordinator: a rotation, a URL-bar collapse and an
    // on-screen keyboard all arrive as bursts, and re-fitting on each one throws the zoom away.
    const RESIZE_QUIET_MS = 180;
    let current = null;

    /**
     * Sizes the rotated stage and re-seats the photo at fit. Only does the work when the stage box
     * or the rotation ACTUALLY changed, or when `force` says the content did (a new photo).
     * Everything else — a soft keyboard, the URL bar collapsing, a desktop window nudged by a
     * pixel — leaves the user's zoom and pan alone, which is the point of an inspection surface.
     */
    function relayout(ctx, force) {
        const rect = ctx.viewport.getBoundingClientRect();
        const vw = rect.width;
        const vh = rect.height;
        if (vw < 1 || vh < 1) {
            return;
        }

        // Portrait viewport: turn the stage a quarter and give it the swapped dimensions, so its own
        // coordinate space is landscape. Landscape viewport: identity, no transform at all.
        const rotation = vh > vw ? 90 : 0;
        if (!force && rotation === ctx.rotation &&
            Math.abs(vw - ctx.lastW) < 1 && Math.abs(vh - ctx.lastH) < 1) {
            return;
        }

        ctx.rotation = rotation;
        ctx.lastW = vw;
        ctx.lastH = vh;
        const sw = rotation === 90 ? vh : vw;
        const sh = rotation === 90 ? vw : vh;
        ctx.stage.style.width = sw + 'px';
        ctx.stage.style.height = sh + 'px';
        ctx.stage.style.transform = rotation === 90
            ? 'translate(' + vw + 'px, 0) rotate(90deg)'
            : '';
        ctx.stage.setAttribute('data-bw-rot', String(rotation));
        document.documentElement.classList.toggle('bw-stage-rot90', rotation === 90);

        fit(ctx, sw, sh);
    }

    /**
     * Fit expressed as geometry: the world is sized to the largest box of the photo's aspect that
     * fits the stage, which makes zoom 1 the fit zoom and keeps every later gesture ordinary.
     */
    function fit(ctx, sw, sh) {
        const img = ctx.world.querySelector('img');
        const iw = img && img.naturalWidth > 0 ? img.naturalWidth : sw;
        const ih = img && img.naturalHeight > 0 ? img.naturalHeight : sh;
        const scale = Math.min(sw / iw, sh / ih);
        const w = iw * scale;
        const h = ih * scale;
        ctx.world.style.width = w + 'px';
        ctx.world.style.height = h + 'px';
        ctx.model.reset(sw, sh, w, h);
    }

    /**
     * Binds the overlay. `dotnet` is the StageOverlay instance: Escape and a system-driven exit from
     * fullscreen both close it from here, because the overlay root cannot rely on holding focus
     * once a hold inside it has been tapped.
     */
    function attach(root, viewport, stage, world, interactive, dotnet) {
        if (!viewport || !viewport.style || !world) {
            // Nothing came up, so nothing may stay locked.
            detach();
            window.bwStageLock.release();
            return;
        }

        detach();

        const model = window.bwStageModel.stageModel(world, interactive ? NO_PAN_INTERACTIVE : NO_PAN_READONLY);
        const ctx = {
            root: root,
            viewport: viewport,
            stage: stage,
            world: world,
            model: model,
            rotation: 0,
            lastW: -1,
            lastH: -1,
            resizeTimer: 0,
            dotnet: dotnet,
        };

        // A new photo (or the first one finishing its decode) is the one case that MUST re-fit.
        ctx.onImageLoad = function () { relayout(ctx, true); };
        ctx.onResize = function () {
            if (ctx.resizeTimer) {
                clearTimeout(ctx.resizeTimer);
            }

            ctx.resizeTimer = setTimeout(function () {
                ctx.resizeTimer = 0;
                relayout(ctx, false);
            }, RESIZE_QUIET_MS);
        };
        ctx.onKeyDown = function (e) {
            if (e.key === 'Escape' && !e.defaultPrevented) {
                e.preventDefault();
                close(ctx);
            }
        };
        // Leaving fullscreen by the system gesture should leave the takeover too, so the user is never
        // left with a rotated page they did not ask for.
        ctx.onFullscreenChange = function () {
            if (!document.fullscreenElement) {
                close(ctx);
            }
        };

        window.addEventListener('resize', ctx.onResize);
        window.addEventListener('orientationchange', ctx.onResize);
        document.addEventListener('keydown', ctx.onKeyDown, true);
        document.addEventListener('fullscreenchange', ctx.onFullscreenChange);

        const img = world.querySelector('img');
        if (img && !img.complete) {
            img.addEventListener('load', ctx.onImageLoad, { once: true });
        }

        window.bwGestures.bind(viewport, window.bwStageModel.rotationAdapter(
            model, viewport, function () { return ctx.rotation; }));
        document.documentElement.classList.add('bw-stage-open');
        current = ctx;
        // The overlay is up, so the lock the tap armed is now accounted for.
        window.bwStageLock.settled();
        relayout(ctx, true);

        if (root && root.focus) {
            root.focus({ preventScroll: true });
        }
    }

    /**
     * Re-fits for NEW content under the same overlay: a panel switch, or an enhanced-nav move to
     * another boulder, which retains the component so attach() never runs again.
     */
    function refresh() {
        const ctx = current;
        if (!ctx) {
            return;
        }

        const img = ctx.world.querySelector('img');
        if (img && !img.complete) {
            img.addEventListener('load', ctx.onImageLoad, { once: true });
        }

        relayout(ctx, true);
    }

    function close(ctx) {
        if (!ctx.dotnet) {
            return;
        }

        const dotnet = ctx.dotnet;
        ctx.dotnet = null;
        dotnet.invokeMethodAsync('CloseFromJsAsync').catch(function () { /* circuit already gone */ });
    }

    function detach() {
        const ctx = current;
        if (!ctx) {
            return;
        }

        current = null;
        ctx.dotnet = null;
        ctx.model.kill();
        if (ctx.resizeTimer) {
            clearTimeout(ctx.resizeTimer);
            ctx.resizeTimer = 0;
        }

        window.removeEventListener('resize', ctx.onResize);
        window.removeEventListener('orientationchange', ctx.onResize);
        document.removeEventListener('keydown', ctx.onKeyDown, true);
        document.removeEventListener('fullscreenchange', ctx.onFullscreenChange);
        // The gesture recogniser has no dispose: the node leaves with the Blazor render and the model
        // above is already inert, so a stray in-flight intent can no longer move anything.
        document.documentElement.classList.remove('bw-stage-open', 'bw-stage-rot90');
        window.bwStageLock.release();
    }

    return {
        attach: attach,
        detach: detach,
        refresh: refresh,
        /** True while a takeover is bound. Read by the lock's watchdog (fullscreen-stage-lock.js). */
        isOpen: function () { return current !== null; },
    };
})();
