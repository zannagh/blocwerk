/*
 * Swipe to turn the cards of a card deck (the wall look-over, the update's check step): a mostly horizontal
 * one-finger swipe across the deck calls OnSwipe(1) for the next card (to the left) or OnSwipe(-1) for the previous
 * one. A swipe that starts on something that is panned or typed in (a wall photo, a form field, a scrolling row of
 * chips) is left to it.
 */
const listeners = new WeakMap();
const MIN_DISTANCE = 64;
const OWN_GESTURES = '.panel-step-viewport, input, textarea, select, .deck-chips, .bw-modal-backdrop';

export function attach(element, dotnet) {
    if (!element || listeners.has(element)) {
        return;
    }

    let start = null;
    const onStart = e => {
        const t = e.touches.length === 1 ? e.touches[0] : null;
        start = t && !e.target.closest(OWN_GESTURES) ? { x: t.clientX, y: t.clientY } : null;
    };
    const onEnd = e => {
        const t = e.changedTouches[0];
        if (!start || !t) {
            return;
        }

        const dx = t.clientX - start.x;
        const dy = t.clientY - start.y;
        start = null;
        if (Math.abs(dx) >= MIN_DISTANCE && Math.abs(dx) > 2 * Math.abs(dy)) {
            dotnet.invokeMethodAsync('OnSwipe', dx < 0 ? 1 : -1).catch(() => { });
        }
    };
    element.addEventListener('touchstart', onStart, { passive: true });
    element.addEventListener('touchend', onEnd, { passive: true });
    listeners.set(element, { onStart, onEnd });
}

export function detach(element) {
    const entry = element ? listeners.get(element) : null;
    if (entry) {
        element.removeEventListener('touchstart', entry.onStart);
        element.removeEventListener('touchend', entry.onEnd);
        listeners.delete(element);
    }
}
