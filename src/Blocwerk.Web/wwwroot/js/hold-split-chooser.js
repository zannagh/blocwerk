// The split row's per-boulder chooser (HoldSplitRow.razor): a manual popover (top layer, so the toolbar row's
// fixed height and sideways scroll cannot clip it) placed against that row.

const placed = new WeakMap();

/** Shows the popover just below its toolbar row (above it when the row is docked at the bottom) and keeps it there. */
export function openBelowRow(pop) {
    if (!pop) {
        return;
    }

    try {
        if (!pop.matches(':popover-open')) {
            pop.showPopover();
        }
    } catch (_) { /* no Popover API: it renders in flow */ }

    const place = () => {
        const row = pop.closest('.bw-toolbar-row') || pop.closest('.option-bar') || pop.parentElement;
        if (!row || !pop.isConnected) {
            return;
        }

        const r = row.getBoundingClientRect();
        const w = Math.min(pop.offsetWidth, window.innerWidth - 16);
        const h = pop.offsetHeight;
        pop.style.left = `${Math.max(8, Math.min(r.left + 8, window.innerWidth - w - 8))}px`;
        const below = r.bottom + 6;
        pop.style.top = `${below + h > window.innerHeight - 8 ? Math.max(8, r.top - h - 6) : below}px`;
    };
    place();
    if (!placed.has(pop)) {
        window.addEventListener('resize', place);
        window.addEventListener('scroll', place, true);
        placed.set(pop, place);
    }
}

export function closePopover(pop) {
    const place = pop ? placed.get(pop) : null;
    if (place) {
        window.removeEventListener('resize', place);
        window.removeEventListener('scroll', place, true);
        placed.delete(pop);
    }

    try {
        pop.hidePopover();
    } catch (_) { /* not open */ }
}
