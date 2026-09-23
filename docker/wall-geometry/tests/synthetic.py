"""A one-facet synthetic scene: a vertical plane with one 125 mm ArUco marker, one pinhole camera.

Used to test the texture renderer end-to-end without real photos: the orthophoto must measure the
marker at 125 mm and place it where the geometry says it is.
"""
import cv2
import numpy as np

W, H, F = 1600, 1200, 1500.0
MARKER_ID, SIZE = 0, 125.0
CENTRE_AB = np.array([600.0, 500.0])  # marker centre on the facet plane (mm)
CAM_CENTRE = np.array([450.0, -2200.0, 700.0])  # world mm, in front of the wall (-y), off-axis


def _look_at(centre, target):
    z = target - centre
    z /= np.linalg.norm(z)
    x = np.cross(z, [0, 0, 1.0])  # image right, horizontal
    x /= np.linalg.norm(x)
    y = np.cross(z, x)  # image down
    return np.vstack([x, y, z])


def scene():
    """Returns (geometry document, BGR photo)."""
    O, u, v = np.zeros(3), np.array([1.0, 0, 0]), np.array([0, 0, 1.0])
    n = np.cross(u, v)  # (0,-1,0): toward the camera
    R = _look_at(CAM_CENTRE, np.array([600.0, 0, 500.0]))
    t = -R @ CAM_CENTRE
    K = np.array([[F, 0, (W - 1) / 2], [0, F, (H - 1) / 2], [0, 0, 1]])
    h = SIZE / 2
    ab = CENTRE_AB + np.array([[-h, h], [h, h], [h, -h], [-h, -h]])  # TL,TR,BR,BL (b up)
    world = O + ab[:, :1] * u + ab[:, 1:] * v
    pc = world @ R.T + t
    px = (pc[:, :2] / pc[:, 2:]) * F + K[:2, 2]
    # the generated marker image IS the black square (border included), side = SIZE
    mk = cv2.aruco.generateImageMarker(cv2.aruco.getPredefinedDictionary(cv2.aruco.DICT_4X4_50),
                                       MARKER_ID, 600)
    src = np.array([[-0.5, -0.5], [599.5, -0.5], [599.5, 599.5], [-0.5, 599.5]], np.float32)  # pixel edges
    ss = 4  # render 4x supersampled, then area-downsample: properly anti-aliased edges
    Hm = cv2.getPerspectiveTransform(src, (ss * (px + 0.5) - 0.5).astype(np.float32))
    big = cv2.warpPerspective(mk, Hm, (W * ss, H * ss), flags=cv2.INTER_LINEAR, borderValue=235)
    photo = cv2.resize(big, (W, H), interpolation=cv2.INTER_AREA)  # white paper around the marker
    # a little texture so the image is not flat
    rng = np.random.default_rng(0)
    photo = np.clip(photo.astype(np.int16) + rng.integers(-6, 7, photo.shape), 0, 255).astype(np.uint8)
    photo = cv2.cvtColor(photo, cv2.COLOR_GRAY2BGR)
    doc = {
        "version": 1, "units": "mm", "dictionary": "DICT_4X4_50", "idScheme": "segment*6+role",
        "markerSizeMm": SIZE, "world": {"origin": "synthetic", "up": [0, 0, 1]},
        "segments": [{"index": 0, "name": "synthetic", "facets": [{
            "id": "0", "origin": O.tolist(), "u": u.tolist(), "v": v.tolist(), "normal": n.tolist(),
            "measuredAngleDeg": 0.0, "yawDeg": 0.0,
            "extentMm": {"aMin": 300.0, "aMax": 900.0, "bMin": 200.0, "bMax": 800.0}, "markerIds": [0]}]}],
        "markers": [{"id": MARKER_ID, "segment": 0, "facet": "0", "sizeMm": SIZE,
                     "cornersPlaneMm": ab.tolist(), "cornersWorldMm": world.tolist()}],
        "cameras": [{"image": "SYN_1", "width": W, "height": H, "K": K.ravel().tolist(),
                     "dist": [0, 0, 0, 0, 0], "R": R.ravel().tolist(), "t": t.tolist()}],
    }
    return doc, photo
