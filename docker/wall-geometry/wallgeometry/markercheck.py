"""Marker check of a rendered facet texture: detected ArUco sizes/positions vs the geometry."""
import cv2
import numpy as np

from .refine import prepare, refine_corners


def _detector(dictionary):
    prm = cv2.aruco.DetectorParameters()
    # the settings that worked on capture 1 (see the glyph plan); CLAHE deliberately not used
    prm.cornerRefinementMethod = cv2.aruco.CORNER_REFINE_SUBPIX
    prm.adaptiveThreshWinSizeMin, prm.adaptiveThreshWinSizeMax, prm.adaptiveThreshWinSizeStep = 3, 53, 5
    prm.minMarkerPerimeterRate = 0.01
    prm.perspectiveRemovePixelPerCell = 8
    return cv2.aruco.ArucoDetector(cv2.aruco.getPredefinedDictionary(getattr(cv2.aruco, dictionary)), prm)


def marker_check(image, facet, doc, res, g):
    """Detect the facet's ArUco markers in its orthophoto, edge-refine the corners, and compare the
    side length with the declared size and the centre with the geometry's plane position."""
    gray = cv2.cvtColor(image, cv2.COLOR_BGR2GRAY)
    corners, ids, _ = _detector(doc["dictionary"]).detectMarkers(gray)
    want = {m["id"]: m for m in doc["markers"] if m["facet"] == facet["id"]}
    blurred = prepare(gray)
    rows = []
    for c, i in zip(corners, [] if ids is None else ids.ravel()):
        if int(i) not in want:
            continue
        c = c.reshape(4, 2).astype(np.float64)
        side_px = float(np.mean(np.linalg.norm(c - np.roll(c, -1, 0), axis=1)))
        c, _, ok = refine_corners(blurred, c, side_px)
        sides = np.linalg.norm(c - np.roll(c, -1, 0), axis=1) * res
        mk = want[int(i)]
        plane = np.array(mk["cornersPlaneMm"]).mean(0)
        centre = c.mean(0)
        pos = np.array([g["aMin"] + (centre[0] + 0.5) * res, g["bMax"] - (centre[1] + 0.5) * res])
        rows.append({"id": int(i), "sideMm": round(float(sides.mean()), 2),
                     "sideErrMm": round(float(sides.mean() - mk.get("sizeMm", doc["markerSizeMm"])), 2),
                     "positionErrMm": round(float(np.linalg.norm(pos - plane)), 2),
                     "cornersRefined": int(ok.sum())})
    errs = np.array([r["sideErrMm"] for r in rows])
    return {"detected": len(rows), "expected": len(want), "markers": rows,
            "sideRmsErrMm": round(float(np.sqrt(np.mean(errs ** 2))), 3) if rows else None,
            "maxPositionErrMm": max((r["positionErrMm"] for r in rows), default=None)}
