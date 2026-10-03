// Pointer handling for the marker planner's net canvas (MarkerPlanCanvas.razor). The SVG draws the net
// in mm with y flipped (svg y = -net y). Dragging a marker is purely visual here — a translate on its
// <g> — and only the drop is reported to .NET, in NET coordinates (y up), which converts it into the
// segment frame, snaps it and re-renders. Clicks on a surface are reported with their net point so the
// page can select that surface or, in add mode, place a marker there.

const DRAG_THRESHOLD_PX = 4;

export function attach(svg, dotnet) {
    let drag = null;

    const toSvg = (evt) => {
        const matrix = svg.getScreenCTM();
        if (!matrix) {
            return null;
        }
        const point = svg.createSVGPoint();
        point.x = evt.clientX;
        point.y = evt.clientY;
        return point.matrixTransform(matrix.inverse());
    };

    const reset = () => {
        if (drag) {
            drag.g.classList.remove('dragging');
            drag.g.removeAttribute('transform');
        }
        drag = null;
    };

    const onDown = (evt) => {
        const g = evt.target.closest('[data-marker-id]');
        const start = g && evt.button === 0 ? toSvg(evt) : null;
        if (!start) {
            return;
        }
        evt.preventDefault();
        g.setPointerCapture(evt.pointerId);
        g.classList.add('dragging');
        drag = { g, start, pointerId: evt.pointerId, clientX: evt.clientX, clientY: evt.clientY, dx: 0, dy: 0, moved: false };
    };

    const onMove = (evt) => {
        if (!drag || evt.pointerId !== drag.pointerId) {
            return;
        }
        const p = toSvg(evt);
        if (!p) {
            return;
        }
        drag.moved ||= Math.hypot(evt.clientX - drag.clientX, evt.clientY - drag.clientY) > DRAG_THRESHOLD_PX;
        drag.dx = p.x - drag.start.x;
        drag.dy = p.y - drag.start.y;
        if (drag.moved) {
            drag.g.setAttribute('transform', `translate(${drag.dx} ${drag.dy})`);
        }
    };

    const onUp = async (evt) => {
        if (!drag || evt.pointerId !== drag.pointerId) {
            return;
        }
        const d = drag;
        const id = Number(d.g.dataset.markerId);
        drag = null;
        d.g.classList.remove('dragging');
        try {
            if (!d.moved) {
                await dotnet.invokeMethodAsync('OnMarkerClicked', id);
                return;
            }
            const x = Number(d.g.dataset.cx) + d.dx;
            const y = Number(d.g.dataset.cy) + d.dy;
            await dotnet.invokeMethodAsync('OnMarkerDropped', id, x, -y);
        } finally {
            d.g.removeAttribute('transform');
        }
    };

    const onClick = (evt) => {
        if (evt.target.closest('[data-marker-id]')) {
            return;
        }
        const p = toSvg(evt);
        if (!p) {
            return;
        }
        const surface = evt.target.closest('[data-segment]');
        const index = surface ? Number(surface.dataset.segment) : -1;
        dotnet.invokeMethodAsync('OnCanvasClicked', index, p.x, -p.y);
    };

    svg.addEventListener('pointerdown', onDown);
    svg.addEventListener('pointermove', onMove);
    svg.addEventListener('pointerup', onUp);
    svg.addEventListener('pointercancel', reset);
    svg.addEventListener('click', onClick);

    return {
        dispose() {
            svg.removeEventListener('pointerdown', onDown);
            svg.removeEventListener('pointermove', onMove);
            svg.removeEventListener('pointerup', onUp);
            svg.removeEventListener('pointercancel', reset);
            svg.removeEventListener('click', onClick);
        },
    };
}
