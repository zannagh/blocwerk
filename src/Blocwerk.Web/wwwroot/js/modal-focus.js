// Focus management for the shared <Modal> component. Kept out of the .razor so it can run real DOM
// focus logic (querying focusables, trapping Tab, restoring focus on close) that Blazor Server cannot
// express on its own. Entries are keyed by a component-generated id rather than the element itself, so
// deactivate() still works — and still restores focus to the trigger — after Blazor removes the panel.
// Lives under wwwroot/js (like every other module in this app) and is imported by absolute path, so the
// import resolves the same way from any route (a collocated ./Components/... path breaks under /walls/{id}).

const registry = new Map();

const FOCUSABLE = [
    'a[href]',
    'button:not([disabled])',
    'textarea:not([disabled])',
    'input:not([disabled]):not([type=hidden])',
    'select:not([disabled])',
    '[tabindex]:not([tabindex="-1"])',
].join(',');

function focusable(root) {
    return Array.from(root.querySelectorAll(FOCUSABLE)).filter(
        (el) => el.offsetParent !== null || el.getClientRects().length > 0,
    );
}

export function activate(id, panel) {
    if (!panel) {
        return;
    }

    const previouslyFocused = document.activeElement;

    const onKeydown = (e) => {
        if (e.key !== 'Tab') {
            return;
        }

        const items = focusable(panel);
        if (items.length === 0) {
            e.preventDefault();
            panel.focus();
            return;
        }

        const first = items[0];
        const last = items[items.length - 1];
        const active = document.activeElement;

        if (e.shiftKey) {
            if (active === first || !panel.contains(active)) {
                e.preventDefault();
                last.focus();
            }
        } else if (active === last || !panel.contains(active)) {
            e.preventDefault();
            first.focus();
        }
    };

    panel.addEventListener('keydown', onKeydown);
    registry.set(id, { panel, onKeydown, previouslyFocused });

    // Move focus into the dialog on open.
    const items = focusable(panel);
    (items[0] || panel).focus();
}

export function deactivate(id) {
    const entry = registry.get(id);
    if (!entry) {
        return;
    }

    registry.delete(id);
    entry.panel.removeEventListener('keydown', entry.onKeydown);

    // Restore focus to whatever opened the dialog.
    const target = entry.previouslyFocused;
    if (target && typeof target.focus === 'function' && document.contains(target)) {
        try {
            target.focus();
        } catch {
            // Restoring focus is a nicety; a removed trigger must not throw.
        }
    }
}
