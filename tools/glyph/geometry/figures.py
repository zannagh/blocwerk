"""Figures: isometric 3D, side section (seg 2 face-on), top-down plan. World frame, mm."""
import os

import matplotlib

matplotlib.use("Agg")
import matplotlib.pyplot as plt  # noqa: E402
import numpy as np  # noqa: E402

from data import OUT  # noqa: E402  (CLI-only debugging figures; needs matplotlib)

COL = {"0": "#d9822b", "1": "#3b7dd8", "2": "#3aa55d", "5": "#9b59b6", "5a": "#9b59b6", "5b": "#c0392b"}


def _facet_poly(fw):
    e, O, u, v = fw["extent"], fw["origin"], fw["u"], fw["v"]
    return np.array([O + a * u + b * v for a, b in
                     [(e["aMin"], e["bMin"]), (e["aMax"], e["bMin"]), (e["aMax"], e["bMax"]),
                      (e["aMin"], e["bMax"]), (e["aMin"], e["bMin"])]])


def _save(fig, name):
    path = os.path.join(OUT, name)
    fig.savefig(path, dpi=100, bbox_inches="tight")
    plt.close(fig)
    return path


def fig_3d(sol):
    wd = sol["world"]
    fig = plt.figure(figsize=(8, 6.5))
    ax = fig.add_subplot(111, projection="3d")
    for fid, fw in wd["facets"].items():
        P = _facet_poly(fw)
        ax.plot(*P.T, color=COL.get(fid, "k"), lw=1, ls="--")
        for m in sol["members"][fid]:
            C = wd["corners"][m]
            ax.plot(*np.vstack([C, C[:1]]).T, color=COL.get(fid, "k"), lw=1.5)
            ax.text(*C.mean(0), str(m), fontsize=6)
    for img, c in wd["cams"].items():
        p = c["centre"]
        ax.scatter(*p, color="k", s=8)
        ax.plot(*np.vstack([p, p + c["R"][2] * 400]).T, color="gray", lw=0.7)
        ax.text(*p, img[-2:], fontsize=6, color="gray")
    allp = np.vstack([c for c in wd["corners"].values()] + [c["centre"] for c in wd["cams"].values()])
    mid, r = allp.mean(0), np.ptp(allp, 0).max() / 2
    ax.set_xlim(mid[0] - r, mid[0] + r)
    ax.set_ylim(mid[1] - r, mid[1] + r)
    ax.set_zlim(mid[2] - r, mid[2] + r)
    ax.set_xlabel("x mm")
    ax.set_ylabel("y mm")
    ax.set_zlabel("z (up) mm")
    ax.view_init(elev=20, azim=-120)
    ax.set_title("Markers (solid) + marker-extent boxes (dashed, NOT outlines) + cameras (NN = IMG_27NN)",
                 fontsize=8)
    return _save(fig, "fig_3d.png")


def fig_side(sol):
    """Project onto the y-z plane (looking along +x, i.e. at the left triangle face-on)."""
    wd, ang = sol["world"], sol["angles"]
    fig, ax = plt.subplots(figsize=(6.5, 6))
    for fid in sol["members"]:
        for m in sol["members"][fid]:
            C = wd["corners"][m]
            ax.fill(C[:, 1], C[:, 2], color=COL.get(fid, "k"), alpha=0.6)
            ax.text(C[:, 1].mean() + 40, C[:, 2].mean(), str(m), fontsize=7)
    ref = sol["ref_facet"]
    fw = wd["facets"][ref]
    ends = np.array([fw["origin"] + b * fw["v"] for b in (fw["extent"]["bMin"], fw["extent"]["bMax"])])
    ax.plot(ends[:, 1], ends[:, 2], color=COL["0"], lw=2,
            label=f"facet {ref} plane: {ang[ref]['tiltDeg']:.2f}° from vertical")
    b = ends[0]
    ax.plot([b[1], b[1]], [b[2], b[2] + 1500], color="k", lw=0.8, ls=":", label="vertical (gravity)")
    ax.set_aspect("equal")
    ax.set_xlabel("y (into the wall) mm")
    ax.set_ylabel("z (up) mm")
    ax.legend(fontsize=7, loc="lower left")
    ax.set_title("Side section, markers projected on the y-z plane (orange 0, blue 1, green 2, purple 5)",
                 fontsize=8)
    ax.grid(alpha=0.3)
    return _save(fig, "fig_side_section.png")


def fig_plan(sol):
    wd = sol["world"]
    fig, ax = plt.subplots(figsize=(7, 5.5))
    for fid in sol["members"]:
        for m in sol["members"][fid]:
            C = wd["corners"][m]
            ax.fill(C[:, 0], C[:, 1], color=COL.get(fid, "k"), alpha=0.6)
            ax.text(C[:, 0].mean(), C[:, 1].mean() - 120, str(m), fontsize=6)
    for img, c in wd["cams"].items():
        p, d = c["centre"], c["R"][2]
        ax.plot(p[0], p[1], "k.", ms=4)
        ax.plot([p[0], p[0] + d[0] * 400], [p[1], p[1] + d[1] * 400], color="gray", lw=0.7)
        ax.text(p[0], p[1], img[-2:], fontsize=6, color="gray")
    ax.set_aspect("equal")
    ax.set_xlabel("x mm")
    ax.set_ylabel("y mm (into the wall)")
    ax.set_title("Top-down plan: markers + camera positions/view directions", fontsize=8)
    ax.grid(alpha=0.3)
    return _save(fig, "fig_plan.png")
