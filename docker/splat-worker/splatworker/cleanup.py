"""Post-crop cleanup of the trained splats.

The geometry-based cleanup post-step goes here (another change adds it); it runs after crop, before
export, in both the all-in-one (`splat`) and the split (`splat-finish`) path, so a runner-trained
scene is cleaned exactly like a locally trained one.
"""


def cleanup(splats, frame, geometry, log):
    """splats: the cropped splatio.Splats (COLMAP frame); frame: the frame dict (toViewer, crop, ...);
    geometry: the wall-geometry document or None; log(text): a tools.log note. Returns the Splats to
    export (today: unchanged)."""
    return splats
