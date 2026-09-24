"""3DGS .ply (Brush / Inria layout) -> cropped `.splat` (antimatter15) and `.spz` (Niantic, v2).

Both outputs keep the splats in the SAME coordinates as the trained .ply (COLMAP frame); frame.json
carries the transform into metres. `.spz` layout (public format, github.com/nianticlabs/spz), gzip of:
  header  u32 magic 0x5053474e "NGSP", u32 version 2, u32 count, u8 shDegree, u8 fractionalBits,
          u8 flags, u8 reserved
  then, each as one array over all points: positions 3x24-bit signed fixed point, alphas u8
  (sigmoid*255), colours 3xu8 (SH DC * 0.15*255 + 127.5), scales 3xu8 ((log s + 10) * 16),
  rotations 3xu8 (x,y,z of the unit quaternion with w >= 0, *127.5 + 127.5), no SH rest (degree 0).
Checked against an .spz written by Spark 2.2's writeSpz from the same splats (tests/test_splatio.py).
"""
import gzip
import struct

import numpy as np

SH_C0 = 0.28209479177387814
SPZ_MAGIC, SPZ_VERSION = 0x5053474E, 2
SPLAT_DTYPE = np.dtype([("p", "<f4", 3), ("s", "<f4", 3), ("c", "u1", 4), ("r", "u1", 4)])


def read_ply(path):
    """Returns dict of float32 columns (binary little-endian, float properties only)."""
    with open(path, "rb") as f:
        props, n, fmt = [], 0, None
        while True:
            line = f.readline()
            if not line:
                raise ValueError("truncated PLY header")
            line = line.decode("ascii", "replace").strip()
            if line.startswith("format"):
                fmt = line.split()[1]
            elif line.startswith("element vertex"):
                n = int(line.split()[-1])
            elif line.startswith("property"):
                _, typ, name = line.split()
                props.append((name, typ))
            elif line == "end_header":
                break
        if fmt != "binary_little_endian" or any(t != "float" for _, t in props):
            raise ValueError(f"unsupported PLY layout ({fmt}, {set(t for _, t in props)})")
        data = np.frombuffer(f.read(n * 4 * len(props)), dtype="<f4").reshape(n, len(props))
    return {name: data[:, i] for i, (name, _) in enumerate(props)}


class Splats:
    """Decoded splat attributes (linear scale, rgb 0..1, alpha 0..1, unit quaternion wxyz)."""

    def __init__(self, xyz, log_scale, dc, opacity_logit, rot):
        self.xyz, self.log_scale, self.dc = xyz, log_scale, dc
        self.alpha = 1.0 / (1.0 + np.exp(-opacity_logit))
        q = rot / np.linalg.norm(rot, axis=1, keepdims=True)
        self.rot = q

    @classmethod
    def from_ply(cls, cols):
        return cls(np.stack([cols["x"], cols["y"], cols["z"]], 1).astype(np.float64),
                   np.stack([cols[f"scale_{i}"] for i in range(3)], 1),
                   np.stack([cols[f"f_dc_{i}"] for i in range(3)], 1),
                   cols["opacity"], np.stack([cols[f"rot_{i}"] for i in range(4)], 1))

    def __len__(self):
        return len(self.xyz)

    def subset(self, mask):
        s = object.__new__(Splats)
        s.xyz, s.log_scale, s.dc, s.alpha, s.rot = (self.xyz[mask], self.log_scale[mask], self.dc[mask],
                                                   self.alpha[mask], self.rot[mask])
        return s


def crop_mask(splats, to_viewer, box, min_alpha=0.02):
    """Keep splats whose centre, mapped by the 4x4 row-major `to_viewer`, lies inside box [lo, hi]."""
    keep = splats.alpha > min_alpha
    if box is not None:
        M = np.array(to_viewer, float)
        P = splats.xyz @ M[:3, :3].T + M[:3, 3]
        lo, hi = np.array(box[0]), np.array(box[1])
        keep &= np.all((P >= lo) & (P <= hi), axis=1)
    return keep


def write_splat(splats, path):
    """antimatter15 .splat: 32 B per splat, sorted by importance (volume x opacity), largest first."""
    scale = np.exp(splats.log_scale)
    order = np.argsort(-(scale.prod(1) * splats.alpha))
    rgb = 0.5 + SH_C0 * splats.dc
    out = np.zeros(len(splats), dtype=SPLAT_DTYPE)
    out["p"] = splats.xyz[order]
    out["s"] = scale[order]
    out["c"] = np.clip(np.concatenate([rgb, splats.alpha[:, None]], 1)[order] * 255, 0, 255).astype(np.uint8)
    out["r"] = np.clip(splats.rot[order] * 128 + 128, 0, 255).astype(np.uint8)
    out.tofile(path)
    return len(out)


def _u8(x):
    return np.clip(np.round(x), 0, 255).astype(np.uint8)


def spz_bytes(splats, fractional_bits=12):
    n = len(splats)
    ext = float(np.abs(splats.xyz).max()) if n else 0.0
    while fractional_bits > 0 and ext * (1 << fractional_bits) >= (1 << 23) - 1:
        fractional_bits -= 1  # keep every coordinate inside the 24-bit range
    fixed = np.round(splats.xyz * (1 << fractional_bits)).astype(np.int32) & 0xFFFFFF
    pos = np.stack([fixed & 0xFF, (fixed >> 8) & 0xFF, (fixed >> 16) & 0xFF], axis=-1).astype(np.uint8)
    q = splats.rot.copy()  # w, x, y, z
    q[q[:, 0] < 0] *= -1
    header = struct.pack("<IIIBBBB", SPZ_MAGIC, SPZ_VERSION, n, 0, fractional_bits, 0, 0)
    body = b"".join([
        pos.reshape(n, 9).tobytes(),
        _u8(splats.alpha * 255).tobytes(),
        _u8(splats.dc * (0.15 * 255) + 127.5).tobytes(),
        _u8((splats.log_scale + 10.0) * 16.0).tobytes(),
        _u8(q[:, 1:4] * 127.5 + 127.5).tobytes(),
    ])
    return gzip.compress(header + body, compresslevel=9, mtime=0)


def write_spz(splats, path):
    data = spz_bytes(splats)
    with open(path, "wb") as fh:
        fh.write(data)
    return len(data)


def spz_subset(data, keep):
    """The .spz `data` with only the splats where `keep` is true, byte for byte (no re-quantisation):
    each per-splat array of the file is filtered as stored. Any SH degree."""
    raw = gzip.decompress(data)
    magic, version, n, sh, fb, flags, res = struct.unpack("<IIIBBBB", raw[:16])
    if magic != SPZ_MAGIC or version != 2:
        raise ValueError("not an spz v2 file")
    keep = np.asarray(keep, bool)
    if len(keep) != n:
        raise ValueError(f"mask has {len(keep)} entries for {n} splats")
    sh_bytes = {0: 0, 1: 9, 2: 24, 3: 45}[sh]
    parts, o = [struct.pack("<IIIBBBB", magic, version, int(keep.sum()), sh, fb, flags, res)], 16
    for width in (9, 1, 3, 3, 3, sh_bytes):  # positions, alphas, colours, scales, rotations, SH rest
        block = np.frombuffer(raw[o:o + width * n], np.uint8).reshape(n, width) if width else None
        o += width * n
        if block is not None:
            parts.append(block[keep].tobytes())
    return gzip.compress(b"".join(parts), compresslevel=9, mtime=0)


def read_spz(data):
    """Decoder for tests / sanity checks: returns dict(xyz, alpha, rgb_u8, log_scale, quat_xyz)."""
    raw = gzip.decompress(data)
    magic, version, n, sh, fb, _flags, _ = struct.unpack("<IIIBBBB", raw[:16])
    if magic != SPZ_MAGIC or version != 2:
        raise ValueError("not an spz v2 file")
    o = 16
    p = np.frombuffer(raw[o:o + 9 * n], np.uint8).reshape(n, 3, 3).astype(np.int32)
    o += 9 * n
    v = p[..., 0] | (p[..., 1] << 8) | (p[..., 2] << 16)
    v = np.where(v & 0x800000, v - (1 << 24), v)
    alpha = np.frombuffer(raw[o:o + n], np.uint8)
    o += n
    col = np.frombuffer(raw[o:o + 3 * n], np.uint8).reshape(n, 3)
    o += 3 * n
    sc = np.frombuffer(raw[o:o + 3 * n], np.uint8).reshape(n, 3)
    o += 3 * n
    rot = np.frombuffer(raw[o:o + 3 * n], np.uint8).reshape(n, 3)
    return {"xyz": v / (1 << fb), "alpha": alpha, "colour": col, "log_scale": sc / 16.0 - 10.0,
            "quat_xyz": rot / 127.5 - 1.0, "shDegree": sh}
