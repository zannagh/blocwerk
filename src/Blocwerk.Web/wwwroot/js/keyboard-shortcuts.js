// Global keyboard-shortcut dispatcher.
//
// The app had no app-wide key handling: every handler was an @onkeydown on a focusable wrapper
// (Modal, PanelOverlapStepper, CarryoverStepper, HoldClassifyOverlay, WallGalleryPanel). That works
// when the user has already clicked into the thing, but desktop shortcuts have to fire without the
// user first hunting for something to focus. So this is one document-level listener that components
// register against, rather than a listener per component.
//
// Design notes:
//  - Handlers form a STACK. The most recently registered one is offered a key first, and a handler
//    only sees keys it declared, so an open editor can claim "a" while the carousel underneath still
//    gets ArrowLeft/ArrowRight through fall-through. Last in, first served.
//  - Only declared keys are sent to .NET. Every keystroke crossing the SignalR circuit would be a
//    round-trip per character, which is exactly the cost OnScroll in WallCarousel goes out of its
//    way to coalesce. Declaring keys up front keeps idle typing free.
//  - The existing element-scoped handlers keep their keys. Anything inside .panel-stepper, or inside
//    an open .bw-modal-backdrop, is left alone entirely.
window.bwKeys = (function () {
    const stack = [];
    let attached = false;
    // A kiosk is a shared, unattended tablet. A passing keyboard must not be able to drive the
    // editor's destructive shortcuts, so a kiosk session starts shortcuts OFF and the wall's admin
    // opts in per wall (Wall.AllowKioskKeyboardShortcuts). The gate lives here rather than at each
    // registration site so it also covers "?" — which has no C# scope — and any shortcut added later.
    let enabled = true;

    // Normalise a KeyboardEvent (or a declaration string) to one comparable token.
    // Single printable characters fold to lower case so "A" and "a" are the same binding, while
    // named keys (ArrowLeft, Escape, Enter) keep their exact casing.
    function normalise(key, ctrl, shift, alt) {
        let k = key;
        if (k.length === 1) {
            k = k.toLowerCase();
        }
        let out = '';
        if (ctrl) { out += 'ctrl+'; }
        if (alt) { out += 'alt+'; }
        // Shift is only meaningful on named keys. On printable characters the shifted symbol is
        // already in event.key ("?" rather than shift+/), so folding it in would never match.
        if (shift && k.length > 1) { out += 'shift+'; }
        return out + k;
    }

    // "+" is both the modifier separator and a legitimate key ("+" zooms in), so a blind split on
    // "+" parses the zoom binding to an empty string and silently never matches. Peel modifiers off
    // the FRONT and treat whatever is left as the key name.
    function parse(decl) {
        let rest = decl;
        let ctrl = false;
        let shift = false;
        let alt = false;
        for (;;) {
            const m = /^(ctrl|cmd|meta|shift|alt)\+/i.exec(rest);
            if (!m) { break; }
            const mod = m[1].toLowerCase();
            if (mod === 'ctrl' || mod === 'cmd' || mod === 'meta') { ctrl = true; }
            else if (mod === 'shift') { shift = true; }
            else { alt = true; }
            rest = rest.slice(m[0].length);
        }
        return normalise(rest, ctrl, shift, alt);
    }

    function isTypingTarget(el) {
        if (!el) { return false; }
        if (el.isContentEditable) { return true; }
        const tag = el.tagName;
        return tag === 'INPUT' || tag === 'TEXTAREA' || tag === 'SELECT' || tag === 'OPTION' || tag === 'SUMMARY';
    }

    // Surfaces that own their own keys and must not be second-guessed. Their mere PRESENCE stands
    // the dispatcher down, not merely having focus inside them: each one takes over the view and
    // already carries its own @onkeydown, and those keys are meant to work without the user first
    // clicking into the thing. A focus-based check would let the wall carousel's arrows scroll the
    // update wizard off screen mid-review, or page the wall behind an open lightbox.
    // The cross-gen and panel-link tools are here for the same reason: they are full-view takeovers,
    // so without them the editor's letter keys stay live underneath a tool that covers the screen.
    function takeoverSurfaceOpen() {
        return document.querySelector(
            '.panel-stepper, .wall-lightbox, .bw-modal-backdrop, .crossgen-tool, .panel-link-overlay') !== null;
    }

    // Enter and Space ACTIVATE whatever currently has focus. Listening in the capture phase means we
    // would otherwise beat the browser to it and turn "tab to Discard, press Enter" into whichever
    // action the page declared for Enter — the opposite of what the user aimed at, and worse the more
    // destructive the focused button is. So a focused activatable element keeps these two keys.
    // Letter keys are unaffected: a focused button does nothing with "a".
    function focusWouldActivate(e) {
        if (e.key !== 'Enter' && e.key !== ' ' && e.key !== 'Spacebar') { return false; }
        const el = document.activeElement;
        if (!el || el === document.body) { return false; }
        const tag = el.tagName;
        return tag === 'BUTTON' || tag === 'A' || tag === 'LABEL' || el.getAttribute('role') === 'button';
    }

    function onKeyDown(e) {
        if (!enabled) { return; }
        if (e.defaultPrevented || e.repeat) { return; }
        if (isTypingTarget(e.target) || takeoverSurfaceOpen()) { return; }
        if (focusWouldActivate(e)) { return; }

        // Ctrl/Cmd chords are only dispatched when a handler explicitly asked for that chord, so
        // browser shortcuts (Cmd+R, Cmd+T, Cmd+C) keep working untouched.
        const token = normalise(e.key, e.ctrlKey || e.metaKey, e.shiftKey, e.altKey);

        // "?" opens the shortcut reference. It lives here rather than in a component scope because
        // MainLayout has no @rendermode, so a C# registration would silently do nothing on every
        // static-SSR page (/about, /privacy, the help page itself). It is a plain navigation to a
        // static route with no server state behind it, so JS can own it and actually be global.
        // A registered scope still wins, so any surface that wants "?" for itself can take it.
        if (token === '?' && !stack.some(entry => entry.keys.has('?'))) {
            e.preventDefault();
            // A new tab, never a navigation: "?" is a reference lookup, and following it in place
            // would tear down the circuit — losing staged hold edits that NavigationLock does not
            // cover. Popup blockers allow this because it is directly user-initiated.
            const tab = window.open('/help/keyboard', '_blank', 'noopener');
            if (!tab) {
                window.location.href = '/help/keyboard';
            }
            return;
        }

        for (let i = stack.length - 1; i >= 0; i--) {
            const entry = stack[i];
            if (!entry.keys.has(token)) { continue; }
            // preventDefault only, deliberately NOT stopPropagation: this listener is on document
            // in the capture phase, so stopping propagation would also kill the target-phase
            // @onkeydown of whatever element the user is actually in. The guards above are what
            // keep two handlers from acting on one key.
            e.preventDefault();
            entry.ref.invokeMethodAsync('OnShortcut', token).catch(() => {
                // The circuit went away mid-keystroke (navigation, reconnect). Drop the handler so a
                // dead reference cannot keep swallowing keys for the rest of the page's life.
                unregister(entry.token);
            });
            return;
        }
    }

    function attach() {
        if (attached) { return; }
        // Capture phase, so a shortcut still fires when focus happens to sit on some unrelated
        // element. The guards above are what keep this from being greedy — in particular Enter and
        // Space are handed back to a focused button rather than claimed.
        document.addEventListener('keydown', onKeyDown, true);
        attached = true;
    }

    function register(token, ref, keys) {
        unregister(token);
        stack.push({ token: token, ref: ref, keys: new Set((keys || []).map(parse)) });
        attach();
    }

    function unregister(token) {
        const i = stack.findIndex(e => e.token === token);
        if (i >= 0) { stack.splice(i, 1); }
    }

    // Re-declare the key set of an already-registered handler without disturbing its stack position,
    // for surfaces whose available actions change per step (the wall update wizard's phases).
    function setKeys(token, keys) {
        const entry = stack.find(e => e.token === token);
        if (entry) { entry.keys = new Set((keys || []).map(parse)); }
    }

    // Off means off: drop every registration too, so a scope registered before the gate resolved
    // cannot keep a live DotNetObjectReference pointed at a surface the user may not drive.
    function setEnabled(value) {
        enabled = !!value;
        if (!enabled) {
            stack.length = 0;
        }
    }

    function isEnabled() {
        return enabled;
    }

    // Attach at load, NOT lazily from the first register(). "?" is meant to work on every page, and
    // most pages (/about, /privacy, the help page itself, boulder and settings pages) never register
    // a scope at all — a lazy attach would leave the listener uninstalled on exactly those pages.
    attach();

    return {
        register: register,
        unregister: unregister,
        setKeys: setKeys,
        setEnabled: setEnabled,
        isEnabled: isEnabled,
    };
})();
