"""Writes small COLMAP binary models (cameras.bin, images.bin, points3D.bin) for the tests."""
import os
import struct

MODEL_IDS = {"SIMPLE_PINHOLE": 0, "PINHOLE": 1, "SIMPLE_RADIAL": 2, "RADIAL": 3}


def write_model(sparse_dir, cameras, images, points):
    """cameras: {id: (model, w, h, params)}; images: [(id, name, cam_id, qvec, tvec)];
    points: [(xyz, rgb)]."""
    os.makedirs(sparse_dir, exist_ok=True)
    with open(os.path.join(sparse_dir, "cameras.bin"), "wb") as fh:
        fh.write(struct.pack("<Q", len(cameras)))
        for cid, (model, w, h, params) in cameras.items():
            fh.write(struct.pack("<iiQQ", cid, MODEL_IDS[model], w, h) + struct.pack(f"<{len(params)}d", *params))
    with open(os.path.join(sparse_dir, "images.bin"), "wb") as fh:
        fh.write(struct.pack("<Q", len(images)))
        for iid, name, cid, q, t in images:
            fh.write(struct.pack("<i7di", iid, *q, *t, cid) + name.encode() + b"\x00")
            fh.write(struct.pack("<Q", 1) + struct.pack("<ddq", 1.0, 2.0, -1))  # one 2D point
    with open(os.path.join(sparse_dir, "points3D.bin"), "wb") as fh:
        fh.write(struct.pack("<Q", len(points)))
        for i, (xyz, rgb) in enumerate(points):
            fh.write(struct.pack("<Q3d3BdQ", i + 1, *xyz, *rgb, 0.5, 2) + struct.pack("<iiii", 1, 0, 2, 0))
