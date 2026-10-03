"""The trained scene as a 3D runner uploads it: a slim binary float .ply with only what the exports keep
(position, DC colour, opacity logit, log scale, rotation wxyz): a quarter of an SH-3 export. The server
also accepts an .spz v2, which splat-finish turns back into the same columns (spz_columns)."""
import numpy as np

from .splatio import read_spz

SLIM_PLY_PROPS = ("x", "y", "z", "f_dc_0", "f_dc_1", "f_dc_2", "opacity", "scale_0", "scale_1", "scale_2",
                  "rot_0", "rot_1", "rot_2", "rot_3")


def write_slim_ply(cols, path):
    """SLIM_PLY_PROPS of a read_ply() dict as a binary little-endian float .ply that read_ply reads back."""
    n = len(cols["x"])
    data = np.stack([np.asarray(cols[k], "<f4") for k in SLIM_PLY_PROPS], 1)
    header = "ply\nformat binary_little_endian 1.0\nelement vertex %d\n" % n
    header += "".join(f"property float {k}\n" for k in SLIM_PLY_PROPS) + "end_header\n"
    with open(path, "wb") as fh:
        fh.write(header.encode("ascii"))
        fh.write(np.ascontiguousarray(data).tobytes())
    return n


def spz_columns(data):
    """read_ply()-style columns of an .spz v2 (8-bit quantised attributes; positions fixed point)."""
    d = read_spz(data)
    q = np.asarray(d["quat_xyz"], np.float64)
    w = np.sqrt(np.clip(1.0 - (q ** 2).sum(1), 0.0, 1.0))
    alpha = np.clip(np.asarray(d["alpha"], np.float64) / 255.0, 1e-6, 1 - 1e-6)
    dc = (np.asarray(d["colour"], np.float64) - 127.5) / (0.15 * 255)
    cols = {"x": d["xyz"][:, 0], "y": d["xyz"][:, 1], "z": d["xyz"][:, 2], "opacity": np.log(alpha / (1 - alpha)),
            "rot_0": w, "rot_1": q[:, 0], "rot_2": q[:, 1], "rot_3": q[:, 2]}
    for i in range(3):
        cols[f"f_dc_{i}"] = dc[:, i]
        cols[f"scale_{i}"] = d["log_scale"][:, i]
    return {k: np.asarray(v, np.float32) for k, v in cols.items()}
