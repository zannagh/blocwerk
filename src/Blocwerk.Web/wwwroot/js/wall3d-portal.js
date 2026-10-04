// Full screen for the 3D stage: moves the stage host to <body> while it is open and puts it back on
// close. The host is `position: fixed`, but inside the page it sat under ancestors that form their own
// stacking contexts (the sticky columns, carousels), so its z-index only counted inside them and the page
// content, top bar and nav painted over it. A direct child of <body> competes with nothing.
// Moving the node keeps the WebGL canvas and the viewer alive; wall3d.js's ResizeObserver refits it.
// Blazor is safe with this because the host is the ONLY child of its Blazor parent (the shell).

const states = new Map();

function focusStage(host) {
    const el = host.querySelector('.wall3d-stage-el');
    if (el && el.focus) {
        el.focus({ preventScroll: true });
    }
}

function nudge() {
    // The ResizeObserver covers the box change; this covers a browser that coalesces it away.
    window.dispatchEvent(new Event('resize'));
}

/** Moves `host` to <body>; Escape (when nothing else consumed it) asks `dotnet` to close. */
export function enter(host, dotnet) {
    if (!host || states.has(host)) {
        return;
    }

    const home = host.parentElement;
    const onKey = e => {
        if (e.key === 'Escape' && !e.defaultPrevented) {
            e.preventDefault();
            dotnet.invokeMethodAsync('CloseFromJs').catch(() => { /* circuit gone */ });
        }
    };
    states.set(host, { home, onKey });
    document.body.appendChild(host);
    document.addEventListener('keydown', onKey);
    document.documentElement.classList.add('wall3d-fs-active');
    focusStage(host);
    nudge();
}

/** Puts `host` back where Blazor rendered it, or removes it when that parent is already gone. */
export function exit(host) {
    const state = host && states.get(host);
    if (!state) {
        return;
    }

    states.delete(host);
    document.removeEventListener('keydown', state.onKey);
    if (states.size === 0) {
        document.documentElement.classList.remove('wall3d-fs-active');
    }

    if (state.home && state.home.isConnected) {
        state.home.appendChild(host);
        focusStage(host);
        nudge();
    } else {
        host.remove();
    }
}
