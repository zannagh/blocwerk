#!/usr/bin/env python3
"""The two surfaces the main-span orthophoto cannot carry.

Both are handled with the machinery of `stitch_wall`, applied a second and a
third time rather than with any new method:

* `kickboard()`   - the plank strip below the overhang.  Four views, so it gets
  the full treatment: pairwise plane homographies, global refinement, plane
  normal by homography decomposition, gain compensation, graph-cut seam,
  full-resolution composite, p90 resolution policy, crop, seam verification.
  Its dihedral angle to the main span is measured, not assumed.

* `left_return()` - the left return panel.  Only 1.jpeg sees it, so there are no
  correspondences and no homography to decompose: the plane has to come from
  line geometry in one view.  That is ill-conditioned here and the function
  says so, with numbers.
"""
import os
import cv2
import numpy as np

import stitch_wall as S


# --------------------------------------------------------------------------
# helpers
# --------------------------------------------------------------------------
def plane_normal_from_pairs(Hs, K, ref, others):
    """Normal of a plane in the `ref` camera frame, consistent across neighbours."""
    cand_sets = []
    for other in others:
        n_sol, Rs, Ts, Ns = cv2.decomposeHomographyMat(np.linalg.inv(Hs[other]), K)
        cands = []
        for i in range(n_sol):
            v = np.array(Ns[i]).ravel()
            v = v / np.linalg.norm(v)
            if v[2] < 0:
                v = -v
            cands.append(v)
        cand_sets.append(cands)
    best, bestcost, bestpicks = None, 1e9, None
    for c in cand_sets[0]:
        cost, picks = 0.0, [c]
        for cs in cand_sets[1:]:
            d = min(np.degrees(np.arccos(np.clip(c @ x, -1, 1))) for x in cs)
            picks.append(min(cs, key=lambda x: np.degrees(np.arccos(np.clip(c @ x, -1, 1)))))
            cost += d
        if cost < bestcost:
            bestcost, best, bestpicks = cost, c, picks
    n = np.mean(bestpicks, 0)
    n /= np.linalg.norm(n)
    spread = [float(np.degrees(np.arccos(np.clip(n @ p, -1, 1)))) for p in bestpicks]
    return n, spread


def frame_from(normal, in_plane_dir):
    """Right-handed rectifying frame: z along the normal, x along `in_plane_dir`."""
    dz = normal / np.linalg.norm(normal)
    dx = in_plane_dir - (in_plane_dir @ dz) * dz
    dx /= np.linalg.norm(dx)
    dy = np.cross(dz, dx)
    R = np.stack([dx, dy, dz], 1)
    return R


def scale_for(Htot, masks, names, corners, percentile):
    """p90 sampling-density scale and canvas box, exactly the main-span policy."""
    lo, hi = corners.min(0), corners.max(0)
    T0 = np.array([[1, 0, -lo[0]], [0, 1, -lo[1]], [0, 0, 1.0]])
    w, h = int(np.ceil(hi[0] - lo[0])), int(np.ceil(hi[1] - lo[1]))
    dens = np.stack([S.density_map(T0 @ Htot[n], masks[n], (S.H0, S.W0), (w, h), stride=8)
                     for n in names]).max(0)
    cov = dens > 0
    med = float(np.percentile(dens[cov], percentile))
    q = np.percentile(dens[cov], [5, 25, 50, 75, 95])
    ratio = dens[cov] / med
    stats = dict(density_percentiles=dict(zip(["p5", "p25", "p50", "p75", "p95"],
                                              [float(x) for x in q])),
                 percentile=percentile, chosen_scale_source_px_per_canvas_px=med,
                 frac_of_span_downsampled=float((ratio > 1.0).mean()),
                 worst_downsampling_factor=float(ratio.max()))
    return med, lo, hi, stats


# --------------------------------------------------------------------------
# the kickboard
# --------------------------------------------------------------------------
def kickboard(ctx):
    K, cache, work, report = ctx["K"], ctx["cache"], ctx["work"], ctx["report"]
    imgs, masks, args = ctx["imgs"], ctx["masks_kick"], ctx["args"]
    rep = {}
    report["kickboard"] = rep

    # ---- its own pairwise plane homographies --------------------------------
    Hpair, corr = {}, {}
    for a, b in S.PAIRS:
        cp = os.path.join(cache, f"k{a}{b}.npz")
        if os.path.exists(cp):
            z = np.load(cp)
            r = dict(H=z["H"], pa=z["pa"], pb=z["pb"], n_inl=int(z["ni"]), rms=float(z["rms"]))
        else:
            r = S.match_pair(imgs[a], masks[a], imgs[b], masks[b])
            if r is None:
                S.log(f"  kickboard pair {a}-{b}: FAILED")
                rep[f"pair_{a}-{b}"] = "no usable match"
                continue
            np.savez(cp, H=r["H"], pa=r["pa"], pb=r["pb"], ni=r["n_inl"], rms=r["rms"])
        Hpair[(a, b)] = r["H"]
        corr[(a, b)] = (r["pa"], r["pb"])
        rep[f"pair_{a}-{b}"] = dict(inliers=r["n_inl"], reproj_rms_px=float(r["rms"]))
    if len(Hpair) < len(S.PAIRS):
        S.log("kickboard: incomplete registration, skipped")
        return

    Hk = {S.REF: np.eye(3)}
    Hk["1"] = Hpair[("1", "2")]
    Hk["3"] = np.linalg.inv(Hpair[("2", "3")])
    Hk["4"] = Hk["3"] @ np.linalg.inv(Hpair[("3", "4")])
    Hk, rst = S.refine(Hk, corr, S.REF, S.IMAGES)
    rep["registration"] = rst

    # ---- IS IT THE SAME PLANE?  measured, not assumed ----------------------
    # Transfer the kickboard's own correspondences with the MAIN-SPAN
    # homographies and compare against what the main span itself achieves.
    Hs = ctx["Hs"]
    same, own = {}, {}
    for (a, b), (pa, pb) in corr.items():
        qa = cv2.perspectiveTransform(pa.reshape(-1, 1, 2), Hs[a]).reshape(-1, 2)
        qb = cv2.perspectiveTransform(pb.reshape(-1, 1, 2), Hs[b]).reshape(-1, 2)
        e = np.linalg.norm(qa - qb, axis=1)
        same[f"{a}-{b}"] = dict(n=len(pa), rms=float(np.sqrt((e ** 2).mean())),
                                median=float(np.median(e)), p95=float(np.percentile(e, 95)))
        qa = cv2.perspectiveTransform(pa.reshape(-1, 1, 2), Hk[a]).reshape(-1, 2)
        qb = cv2.perspectiveTransform(pb.reshape(-1, 1, 2), Hk[b]).reshape(-1, 2)
        e = np.linalg.norm(qa - qb, axis=1)
        own[f"{a}-{b}"] = dict(n=len(pa), rms=float(np.sqrt((e ** 2).mean())),
                               p95=float(np.percentile(e, 95)))
    rep["transfer_residual_under_main_span_homography_px"] = same
    rep["transfer_residual_under_its_own_homography_px"] = own

    n_kick, spread = plane_normal_from_pairs(Hk, K, S.REF, ["1", "3", "4"])
    n_wall = np.array(ctx["n_wall"])
    dihedral = float(np.degrees(np.arccos(np.clip(abs(n_kick @ n_wall), -1, 1))))
    rep["plane_normal_ref_cam"] = [float(x) for x in n_kick]
    rep["plane_normal_spread_deg"] = spread
    rep["dihedral_to_main_span_deg"] = dihedral

    worst_own = max(v["rms"] for v in own.values())
    worst_main = max(v["rms"] for v in same.values())
    rep["coplanar_with_main_span"] = bool(worst_main < 3.0 * worst_own)
    rep["verdict"] = (
        "The kickboard is a well-defined plane of its own (a single homography per pair "
        "explains it to %.2f px rms) but it is NOT the main span's plane: transferring the "
        "same correspondences with the main-span homographies leaves %.1f px rms, %.0fx worse, "
        "and the recovered normals differ by %.1f deg.  It is therefore rectified and "
        "delivered separately instead of being forced into the main-plane fit."
        % (worst_own, worst_main, worst_main / max(worst_own, 1e-6), dihedral))
    S.log("kickboard: own-plane rms %.2f px vs %.1f px under the main-span homography; "
          "dihedral %.2f deg -> separate plane" % (worst_own, worst_main, dihedral))

    # ---- in-plane orientation ----------------------------------------------
    # The dihedral edge between the wall and the kickboard is horizontal, so the
    # two surfaces share the world-horizontal direction the main span already
    # measured from its plank seams.  Reusing it keeps the two orthophotos
    # rotationally consistent and needs no second line fit in a 130 px strip.
    h_ref = ctx["R"][:, 0]
    rep["main_horizontal_out_of_kick_plane_deg"] = float(
        np.degrees(np.arcsin(np.clip(abs(h_ref @ n_kick), -1, 1))))
    R2 = frame_from(n_kick, h_ref)

    Htot = {n: K @ R2.T @ np.linalg.inv(K) @ Hk[n] for n in S.IMAGES}
    corners = np.concatenate([cv2.perspectiveTransform(
        np.array(S.KICK_POLY[n], np.float64).reshape(-1, 1, 2), Htot[n]).reshape(-1, 2)
        for n in S.IMAGES])
    med, lo, hi, rstats = scale_for(Htot, masks, S.IMAGES, corners, args.density_percentile)
    rep["resolution"] = rstats
    Sm = np.array([[med, 0, -med * lo[0]], [0, med, -med * lo[1]], [0, 0, 1.0]])
    Htot = {n: Sm @ Htot[n] for n in S.IMAGES}
    Wc = int(np.ceil((hi[0] - lo[0]) * med))
    Hc = int(np.ceil((hi[1] - lo[1]) * med))
    S.log("kickboard canvas %dx%d at scale %.3f" % (Wc, Hc, med))

    # The hand-traced strips do not land on exactly the same physical band in
    # every frame, so their union in the canvas is ragged and the largest
    # usable rectangle would throw away a quarter of the width.  Grow each mask
    # vertically by GROW px for compositing only - matching, the plane normal
    # and the resolution policy all still use the tight strips.
    GROW = 25
    ker = np.ones((2 * GROW + 1, 1), np.uint8)
    masks_grown = {n: cv2.dilate(masks[n], ker) for n in S.IMAGES}
    res, res_mask = S.composite(imgs, masks_grown, S.IMAGES, Htot, Wc, Hc, work, rep, "kick-")
    rep["composite_mask_grown_px"] = GROW
    res, _ = S.crop_usable(res, res_mask, masks_grown, Htot, S.IMAGES, work, rep, "kick-",
                           min_ratio=0.12, min_fill=0.97)
    png = os.path.join(work, "06-final", "wall-orthophoto-kickboard.png")
    cv2.imwrite(png, res, [cv2.IMWRITE_PNG_COMPRESSION, 9])
    rep["output"] = dict(width=int(res.shape[1]), height=int(res.shape[0]),
                         png_bytes=os.path.getsize(png), file=os.path.basename(png))

    m, longs = S.measure_rectification(res, min_span_frac=0.40, min_pts=20)
    rep["rectification_check"] = m
    S.log_rectification("kickboard rectification check", m)
    S.grid_overlay(res, longs, m, os.path.join(work, "06-final", "grid-check-kickboard.jpg"))


# --------------------------------------------------------------------------
# the left return panel
# --------------------------------------------------------------------------
def crease_line(gray, seed, band=60, ang_tol=8.0, y_lo=950, y_hi=3000):
    """Robust refit of the crease between the main span and the return panel."""
    segs = cv2.createLineSegmentDetector().detect(gray)[0].reshape(-1, 4)
    L = np.hypot(segs[:, 2] - segs[:, 0], segs[:, 3] - segs[:, 1])
    ang = np.degrees(np.arctan2(segs[:, 3] - segs[:, 1], segs[:, 2] - segs[:, 0])) % 180
    mid = np.stack([segs[:, [0, 2]].mean(1), segs[:, [1, 3]].mean(1)], 1)
    p0, p1 = np.array(seed[0]), np.array(seed[1])
    dv = (p1 - p0) / np.linalg.norm(p1 - p0)
    nv = np.array([-dv[1], dv[0]])
    a0 = np.degrees(np.arctan2(dv[1], dv[0])) % 180
    sel = ((L > 50) & (np.abs((mid - p0) @ nv) < band) &
           (np.abs((ang - a0 + 90) % 180 - 90) < ang_tol) &
           (mid[:, 1] > y_lo) & (mid[:, 1] < y_hi))
    if sel.sum() < 3:
        return None
    P = np.concatenate([segs[sel][:, :2], segs[sel][:, 2:]], 0)
    w0 = np.repeat(L[sel], 2)
    w = w0.copy()
    for _ in range(6):
        c = (P * w[:, None]).sum(0) / w.sum()
        Q = (P - c) * np.sqrt(w)[:, None]
        _, _, V = np.linalg.svd(Q, full_matrices=False)
        nv = np.array([-V[0][1], V[0][0]])
        r = (P - c) @ nv
        sd = 1.4826 * np.median(np.abs(r)) + 1e-9
        w = w0 * (np.abs(r) < 3 * sd)
    ell = np.array([nv[0], nv[1], -nv @ c])
    return ell, dict(n_segments=int(sel.sum()), n_points=int((w > 0).sum()),
                     rms_px=float(np.sqrt((r[w > 0] ** 2).mean())),
                     span_px=float(np.linalg.norm(P.max(0) - P.min(0))))


def joint_segments(gray, mask, min_len=70, ang_tol=25.0):
    """Near-vertical straight segments lying wholly on the return panel."""
    segs = cv2.createLineSegmentDetector().detect(gray)[0].reshape(-1, 4)
    L = np.hypot(segs[:, 2] - segs[:, 0], segs[:, 3] - segs[:, 1])
    ang = np.degrees(np.arctan2(segs[:, 3] - segs[:, 1], segs[:, 2] - segs[:, 0])) % 180
    mx = segs[:, [0, 2]].mean(1)
    my = segs[:, [1, 3]].mean(1)
    ii = np.clip(my.astype(int), 0, S.H0 - 1)
    jj = np.clip(mx.astype(int), 0, S.W0 - 1)
    sel = (L > min_len) & (mask[ii, jj] > 0) & (np.abs(ang - 90) < ang_tol)
    return segs[sel], L[sel]


def joint_spread(n2, P, W, K):
    """Length-weighted rms scatter of the rectified joint directions, in degrees.

    Rotation invariant (measured against the weighted circular mean), so it
    tests only whether the joints come out *parallel* - which is exactly the
    one thing the plane normal controls.
    """
    R2 = frame_from(n2, np.array([0.0, 1.0, 0.0]) if abs(n2[1]) < 0.9
                    else np.array([1.0, 0.0, 0.0]))
    Q = cv2.perspectiveTransform(P, K @ R2.T @ np.linalg.inv(K)).reshape(2, -1, 2)
    d = Q[1] - Q[0]
    a = np.arctan2(d[:, 1], d[:, 0]) * 2
    mu = np.arctan2((W * np.sin(a)).sum(), (W * np.cos(a)).sum())
    r = np.degrees(np.abs((a - mu + np.pi) % (2 * np.pi) - np.pi)) / 2
    return float(np.sqrt((W * np.minimum(r, 3.0) ** 2).sum() / W.sum()))


def left_return(ctx):
    K, work, report, args = ctx["K"], ctx["work"], ctx["report"], ctx["args"]
    im = ctx["imgs"]["1"]
    gray = cv2.cvtColor(im, cv2.COLOR_BGR2GRAY)
    rep = {}
    report["left_return_panel"] = rep
    rep["sources"] = dict(
        used=["1"],
        note="2/3/4.jpeg stop short of the crease and never see the panel; 5.jpeg shows the "
             "RIGHT return panel, not this one.  One view only, so there are no "
             "correspondences and no homography to decompose.")

    mask = np.zeros(gray.shape, np.uint8)
    cv2.fillPoly(mask, [np.array(S.LEFT_POLY_UNDIST, np.int32)], 255)
    ov = im.copy()
    ov[mask == 0] = (ov[mask == 0] * 0.25).astype(np.uint8)
    cv2.imwrite(os.path.join(work, "03-masks", "1-left-return.jpg"),
                cv2.resize(ov, (1500, 2000)), [cv2.IMWRITE_JPEG_QUALITY, 82])

    # ---- constraint 1: the crease, which lies in BOTH planes ---------------
    got = crease_line(gray, S.LEFT_CREASE_SEED)
    if got is None:
        rep["status"] = "failed: crease not found"
        return
    ell, cstats = got
    rep["crease"] = cstats

    # main-span normal and rectified world basis, expressed in camera 1
    Hs = ctx["Hs"]
    n_sol, Rs, Ts, Ns = cv2.decomposeHomographyMat(Hs["1"], K)     # 1 -> 2, N in cam 1
    n_wall = np.array(ctx["n_wall"])
    best = None
    for i in range(n_sol):
        N = np.array(Ns[i]).ravel()
        R21 = np.array(Rs[i])
        for sgn in (1, -1):
            v = sgn * N / np.linalg.norm(N)
            a = np.degrees(np.arccos(np.clip((R21 @ v) @ n_wall, -1, 1)))
            if best is None or a < best[0]:
                best = (a, v, R21)
    agree, n1_c1, R21 = best
    Rw1 = R21.T @ ctx["R"]            # rectified world axes expressed in camera 1
    rep["main_normal_transfer_to_cam1_deg"] = float(agree)

    d = np.cross(K.T @ ell, n1_c1)
    d /= np.linalg.norm(d)
    if d[1] < 0:
        d = -d
    rep["crease_direction_cam1"] = [float(x) for x in d]
    rep["crease_vs_main_span_fall_line_deg"] = float(
        np.degrees(np.arccos(np.clip(abs(d @ (-Rw1[:, 1])), -1, 1))))

    # ---- constraint 2: the panel's own board joints ------------------------
    C, LS = joint_segments(gray, mask)
    P = np.concatenate([C[:, :2], C[:, 2:]], 0).reshape(-1, 1, 2).astype(np.float64)
    W = LS
    rep["board_joints"] = dict(n_segments=int(len(C)),
                               total_length_px=float(LS.sum()),
                               x_span_px=float(np.ptp(C[:, [0, 2]].mean(1))))

    # 1-DOF search: every plane that contains the crease direction.
    b1 = np.cross(d, np.array([0.0, 0.0, 1.0]))
    b1 /= np.linalg.norm(b1)
    b2 = np.cross(d, b1)

    def normal_at(t):
        v = np.cos(t) * b1 + np.sin(t) * b2
        n = np.cross(d, v)
        n /= np.linalg.norm(n)
        return (-n if n[2] < 0 else n)

    grid = np.deg2rad(np.arange(0.0, 180.0, 0.5))
    costs = np.array([joint_spread(normal_at(t), P, W, K) for t in grid])
    t0 = grid[int(np.argmin(costs))]
    fine = np.linspace(t0 - np.deg2rad(0.5), t0 + np.deg2rad(0.5), 41)
    fcost = [joint_spread(normal_at(t), P, W, K) for t in fine]
    t_best = fine[int(np.argmin(fcost))]
    n2 = normal_at(t_best)
    cmin = float(np.min(fcost))

    # How much of that 1-DOF is actually pinned down?  Report the whole range of
    # plane orientations whose joint scatter is within 10% of the best - i.e.
    # every plane the photograph cannot tell apart from the optimum.
    cw = Rw1.T @ n2
    yaw = float(np.degrees(np.arctan2(cw[0], cw[2])))
    yaws = []
    for t in grid[costs <= cmin * 1.10]:
        c = Rw1.T @ normal_at(t)
        y = float(np.degrees(np.arctan2(c[0], c[2])))
        yaws.append(yaw + ((y - yaw + 90.0) % 180.0) - 90.0)   # a plane has period 180 deg
    tilt = float(np.degrees(np.arcsin(np.clip(cw[1], -1, 1))))
    dihedral = float(np.degrees(np.arccos(np.clip(abs(n2 @ n1_c1), -1, 1))))
    rep["plane_normal_cam1"] = [float(x) for x in n2]
    rep["plane_normal_in_main_span_basis"] = [float(x) for x in cw]
    rep["yaw_vs_main_span_deg"] = yaw
    rep["tilt_vs_main_span_deg"] = tilt
    rep["dihedral_to_main_span_deg"] = dihedral
    rep["joint_parallelism_rms_deg"] = cmin
    rep["yaw_range_within_10pct_of_best_deg"] = [float(min(yaws)), float(max(yaws))]
    rep["stored_segment_yaw_deg"] = -8.0
    rep["yaw_discrepancy_vs_stored_deg"] = float(yaw - (-8.0))

    span = max(yaws) - min(yaws)
    rep["well_constrained"] = bool(span < 10.0)
    rep["verdict"] = (
        "Single-view rectification.  The crease with the main span gives one in-plane "
        "direction exactly (the main plane's normal is already known, so the crease's 3D "
        "direction follows from its image line alone).  The second direction has to come "
        "from the panel's board joints, and they are too close to parallel in the image to "
        "fix a vanishing point: the joint-parallelism cost bottoms out at %.2f deg - the "
        "noise floor of the segments themselves - and stays within 10%% of that over a "
        "%.0f deg range of plane orientations (yaw %.0f..%.0f deg).  The delivered image "
        "uses the best-fitting plane (yaw %.1f deg) but its horizontal scale is uncertain "
        "by roughly that whole range.  It is NOT of the same standard as the main span."
        % (cmin, span, min(yaws), max(yaws), yaw))
    rep["to_fix"] = (
        "Two or three extra frames of the left return panel from clearly different "
        "positions - step 0.8-1.5 m to the right and/or back along the mat, keep the panel "
        "filling most of the frame, 40-50%% overlap with each other AND with the left third "
        "of the current 1.jpeg, same ultra-wide lens.  Correspondences between them give a "
        "homography whose decomposition fixes the normal the same way it does for the main "
        "span, which removes the ambiguity entirely."
    )
    S.log("left return panel: joint parallelism floor %.2f deg, yaw %.1f deg "
          "(indistinguishable over %.0f deg), stored -8 deg -> discrepancy %.1f deg"
          % (cmin, yaw, span, yaw + 8.0))

    # ---- rectify anyway, flagged --------------------------------------------
    up = -Rw1[:, 1]
    R2 = frame_from(n2, np.cross(up, n2))
    if R2[:, 1] @ up < 0:                       # keep the panel the right way up
        R2 = np.stack([-R2[:, 0], -R2[:, 1], R2[:, 2]], 1)
    H = K @ R2.T @ np.linalg.inv(K)

    # Where do the board joints actually point in 3D under this plane?  If they
    # are plumb, their angle to the main span's fall line IS the wall's overhang.
    Q = cv2.perspectiveTransform(P, H).reshape(2, -1, 2)
    dd = Q[1] - Q[0]
    aa = np.arctan2(dd[:, 1], dd[:, 0]) * 2
    mu = np.arctan2((W * np.sin(aa)).sum(), (W * np.cos(aa)).sum()) / 2
    v_board = R2 @ np.array([np.cos(mu), np.sin(mu), 0.0])
    rep["board_joint_vs_main_span_fall_line_deg"] = float(np.degrees(np.arccos(
        np.clip(abs(v_board @ (-Rw1[:, 1])), -1, 1))))

    kb = report.get("kickboard", {}).get("dihedral_to_main_span_deg")
    if kb is not None:
        report["overhang_cross_check"] = dict(
            from_kickboard_deg=float(kb),
            from_left_return_board_joints_deg=rep["board_joint_vs_main_span_fall_line_deg"],
            difference_deg=float(abs(kb - rep["board_joint_vs_main_span_fall_line_deg"])),
            note="Two independent readings of how far the main span overhangs vertical, "
                 "one assuming the kickboard is plumb, one assuming the left return "
                 "panel's boards are plumb.  They are measured from different surfaces "
                 "with different machinery, so their agreement is a check on both.")

    poly = np.array(S.LEFT_POLY_UNDIST, np.float64).reshape(-1, 1, 2)
    corners = cv2.perspectiveTransform(poly, H).reshape(-1, 2)
    med, lo, hi, rstats = scale_for({"1": H}, {"1": mask}, ["1"], corners,
                                    args.density_percentile)
    rep["resolution"] = rstats
    Sm = np.array([[med, 0, -med * lo[0]], [0, med, -med * lo[1]], [0, 0, 1.0]])
    T = Sm @ H
    Wc = int(np.ceil((hi[0] - lo[0]) * med))
    Hc = int(np.ceil((hi[1] - lo[1]) * med))
    res = cv2.warpPerspective(im, T, (Wc, Hc), flags=cv2.INTER_LANCZOS4)
    res_mask = cv2.warpPerspective(mask, T, (Wc, Hc), flags=cv2.INTER_NEAREST)
    res[res_mask == 0] = 0
    S.log("left return canvas %dx%d at scale %.3f" % (Wc, Hc, med))

    # The panel is a triangle, so the largest inscribed rectangle would drop a
    # third of the holds.  Deliver the whole warped panel (mask bounding box)
    # and let the coverage statistics say where it is thin.
    S.crop_usable(res, res_mask, {"1": mask}, {"1": T}, ["1"], work, rep,
                  "left-return-diagnostic-rect-", min_ratio=0.12, min_fill=0.90)
    ys, xs = np.nonzero(res_mask)
    res = res[ys.min():ys.max() + 1, xs.min():xs.max() + 1]
    png = os.path.join(work, "06-final", "wall-orthophoto-left-return.png")
    cv2.imwrite(png, res, [cv2.IMWRITE_PNG_COMPRESSION, 9])
    rep["output"] = dict(width=int(res.shape[1]), height=int(res.shape[0]),
                         png_bytes=os.path.getsize(png), file=os.path.basename(png))

    # The panel's straight-line family runs vertically, so it is measured on the
    # transposed image with the same sub-pixel tracer the main span uses.
    tr = cv2.rotate(res, cv2.ROTATE_90_COUNTERCLOCKWISE)
    m, longs = S.measure_rectification(tr, min_span_frac=0.35, min_pts=20)
    if m is not None:
        rep["rectification_check_board_joints"] = m
        S.log_rectification("left return board-joint check", m)
        S.grid_overlay(tr, longs, m, os.path.join(work, "06-final", "grid-check-left-return.jpg"),
                       what="board joints (image transposed: joints run left-right here)")
    else:
        rep["rectification_check_board_joints"] = "too few traceable joints"
        S.grid_overlay(res, [], dict(angle_rms_deg=float("nan"), angle_max_abs_deg=float("nan")),
                       os.path.join(work, "06-final", "grid-check-left-return.jpg"),
                       what="board joints (none traceable)")
