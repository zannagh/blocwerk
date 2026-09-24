"""COLMAP camera groups of the job's images (one camera per lens / orientation / size) and their sizes."""
import hashlib
import os


def image_sizes(img_dir):
    """(w, h) of every training image under img_dir (headers only)."""
    from PIL import Image
    sizes = []
    for root, _, files in os.walk(img_dir):
        for f in files:
            with Image.open(os.path.join(root, f)) as im:
                sizes.append(im.size)
    return sizes


def camera_group(stem, facts, small, geo_cam):
    """(group key, RADIAL params f,cx,cy,k1,k2 at the downscaled size or None).

    Photos sharing a lens (and orientation, and size) share one COLMAP camera. Intrinsics prior:
    the solver's calibrated K for this photo if the geometry has it, else the EXIF 35 mm focal length.
    Without either, the photo gets its own camera (safe for mixed lenses)."""
    w, h = small
    orient = "land" if w >= h else "port"
    gw, gh = (geo_cam or {}).get("width") or 0, (geo_cam or {}).get("height") or 0
    if geo_cam and geo_cam.get("K") and gw and gh and abs(w / gw - h / gh) < 0.01 * w / gw:
        s = w / gw  # same aspect and orientation as the photo the solver calibrated
        K = [float(v) for row in geo_cam["K"] for v in (row if isinstance(row, list) else [row])]
        fx, fy, cx, cy = K[0], K[4], K[2], K[5]  # 3x3 or flat row-major 9
        dist = list(geo_cam.get("dist") or []) + [0.0, 0.0]
        full = str(geo_cam.get("group") or "geo")
        group = "".join(ch if ch.isalnum() else "_" for ch in full)[:40]
        # The readable prefix is truncated, and two lenses of one phone share it ("iPhone 16 Pro back
        # triple camera 2.22mm" vs "…6.765mm"), so the key also carries a digest of the full group and
        # its calibration: photos only share a COLMAP camera when the solver calibrated them alike.
        digest = hashlib.sha1(f"{full}|{fx:.1f}|{fy:.1f}|{cx:.1f}|{cy:.1f}".encode()).hexdigest()[:8]
        return f"geo_{group}_{digest}_{orient}_{w}x{h}", [(fx + fy) / 2 * s, cx * s, cy * s, dist[0], dist[1]]
    if facts.get("focal35"):
        f = facts["focal35"] / 36.0 * max(w, h)
        return f"exif_{facts.get('lensKey') or 'lens'}_{int(facts['focal35'])}_{orient}_{w}x{h}", \
            [f, w / 2, h / 2, 0.0, 0.0]
    return "single", None


def video_group(small):
    """All frames of the walk-along video share one COLMAP camera (one lens, one size, no EXIF prior:
    COLMAP estimates the focal length from the many views)."""
    w, h = small
    return f"video_{'land' if w >= h else 'port'}_{w}x{h}", None
