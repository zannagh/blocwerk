// "Make sizes exact" (WallScaleTapPicker.razor): where a tap landed on the photo, as a fraction of the shown
// image (0..1 across, 0..1 down). The component turns it into stored-photo pixels.
export function fraction(element, clientX, clientY) {
    const r = element.getBoundingClientRect();
    if (!r.width || !r.height) {
        return null;
    }
    return [
        Math.min(1, Math.max(0, (clientX - r.left) / r.width)),
        Math.min(1, Math.max(0, (clientY - r.top) / r.height)),
    ];
}
