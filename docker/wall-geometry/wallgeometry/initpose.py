"""Initialisation: per-observation IPPE_SQUARE poses, then greedy chaining from a root photo."""
import cv2
import numpy as np

from .camera import K_matrix, rotmat, undistort_px


def ippe_candidates(obs, cams, intr):
    """Both IPPE_SQUARE solutions per observation (marker->camera), on undistorted corners."""
    for o in obs:
        c = cams[o["image"]]
        g = intr[c["group"]]
        und = undistort_px(o["corners"], g, c["w"], c["h"], c["psign"])
        K = K_matrix(g, c["w"], c["h"], c["psign"])
        _, rvecs, tvecs, errs = cv2.solvePnPGeneric(
            o["obj"], und.astype(np.float64), K, None, flags=cv2.SOLVEPNP_IPPE_SQUARE)
        o["ippe"] = [(rotmat(r.ravel())[0], t.ravel()) for r, t in zip(rvecs, tvecs)]
        o["ippe_err"] = [float(e) for e in np.ravel(errs)]


def _reproj(Rw, tw, Rc, tc, o, cams, intr):
    """Reprojection RMS of a marker at world pose (Rw,tw) into camera (Rc,tc) for obs o."""
    from .camera import project
    c = cams[o["image"]]
    pw = o["obj"] @ Rw.T + tw
    pc = pw @ Rc.T + tc
    if np.any(pc[:, 2] <= 0):
        return 1e9
    px = project(pc, intr[c["group"]], c["w"], c["h"], c["psign"])
    return float(np.sqrt(np.mean(np.sum((px - o["corners"]) ** 2, axis=1))))


def chain(obs, cams, intr, root):
    """Greedy: place markers from known cameras, then PnP cameras from placed markers."""
    cam_pose = {root: (np.eye(3), np.zeros(3))}
    mk_pose = {}
    by_img, by_id = {}, {}
    for o in obs:
        by_img.setdefault(o["image"], []).append(o)
        by_id.setdefault(o["id"], []).append(o)
    all_imgs = set(by_img)
    while True:
        progressed = False
        # 1) place markers seen by known cameras
        for mid, olist in by_id.items():
            if mid in mk_pose:
                continue
            known = [o for o in olist if o["image"] in cam_pose]
            if not known:
                continue
            best = None
            for o in known:
                Rc, tc = cam_pose[o["image"]]
                for Rm, tm in o["ippe"]:
                    Rw = Rc.T @ Rm
                    tw = Rc.T @ (tm - tc)
                    cost = sum(_reproj(Rw, tw, *cam_pose[k["image"]], k, cams, intr) for k in known)
                    if best is None or cost < best[0]:
                        best = (cost, Rw, tw)
            mk_pose[mid] = (best[1], best[2])
            progressed = True
        # 2) add the unknown camera with the most placed markers
        cand = []
        for img in all_imgs - set(cam_pose):
            placed = [o for o in by_img[img] if o["id"] in mk_pose]
            if placed:
                cand.append((len(placed), img, placed))
        if cand:
            cand.sort(key=lambda x: -x[0])
            _, img, placed = cand[0]
            cam_pose[img] = _pnp_camera(img, placed, mk_pose, cams, intr)
            progressed = True
        if not progressed:
            break
    return cam_pose, mk_pose


def _pnp_camera(img, placed, mk_pose, cams, intr):
    c = cams[img]
    g = intr[c["group"]]
    K = K_matrix(g, c["w"], c["h"], c["psign"])
    best = None
    # candidate from each single marker (both IPPE branches), then refine on all markers
    for o in placed:
        Rw, tw = mk_pose[o["id"]]
        for Rm, tm in o["ippe"]:
            Rc = Rm @ Rw.T
            tc = tm - Rc @ tw
            cost = sum(_reproj(*mk_pose[k["id"]], Rc, tc, k, cams, intr) for k in placed)
            if best is None or cost < best[0]:
                best = (cost, Rc, tc)
    _, Rc, tc = best
    if len(placed) >= 2:
        P3 = np.vstack([o["obj"] @ mk_pose[o["id"]][0].T + mk_pose[o["id"]][1] for o in placed])
        P2 = np.vstack([undistort_px(o["corners"], g, c["w"], c["h"], c["psign"]) for o in placed])
        rv, _ = cv2.Rodrigues(Rc)
        ok, rv, tv = cv2.solvePnP(P3, P2, K, None, rv, tc.reshape(3, 1).copy(),
                                  useExtrinsicGuess=True, flags=cv2.SOLVEPNP_ITERATIVE)
        if ok:
            Rc, tc = cv2.Rodrigues(rv)[0], tv.ravel()
    return Rc, tc


def chain_fixed(new, cams, intr, cam_pose):
    """Place markers (not yet placed) using known cameras, choosing the IPPE branch by consistency."""
    by_id = {}
    for o in new:
        by_id.setdefault(o["id"], []).append(o)
    out = {}
    for mid, olist in by_id.items():
        best = None
        for o in olist:
            Rc, tc = cam_pose[o["image"]]
            for Rm, tm in o["ippe"]:
                Rw, tw = Rc.T @ Rm, Rc.T @ (tm - tc)
                cost = sum(_reproj(Rw, tw, *cam_pose[k["image"]], k, cams, intr) for k in olist)
                if best is None or cost < best[0]:
                    best = (cost, Rw, tw)
        out[mid] = (best[1], best[2])
    return cam_pose, out


def select_branches(obs, facet_of, tol_deg=20.0):
    """Resolve the IPPE two-fold ambiguity per photo using facet co-membership.

    For each photo and facet with >= 2 observed markers, pick the normal direction most markers
    agree on (within tol) and keep, per marker, the branch closest to it. Single-marker facets
    keep both branches (ordered by IPPE error) for later consistency checks across photos.
    """
    cos_tol = np.cos(np.radians(tol_deg))
    groups = {}
    for o in obs:
        groups.setdefault((o["image"], facet_of.get(o["id"])), []).append(o)
    for (img, fid), ol in groups.items():
        order = [np.argsort(o["ippe_err"]) for o in ol]
        for o, idx in zip(ol, order):
            o["ippe"] = [o["ippe"][i] for i in idx]
            o["ippe_err"] = [o["ippe_err"][i] for i in idx]
            o["resolved"] = False
        if fid is None or len(ol) < 2:
            continue
        cands = [(R[:, 2], k) for k, o in enumerate(ol) for R, _ in o["ippe"]]
        best = None
        for n, _ in cands:
            votes = sum(any(R[:, 2] @ n > cos_tol for R, _ in o["ippe"]) for o in ol)
            if best is None or votes > best[0]:
                best = (votes, n)
        if best[0] < 2:
            continue
        n = best[1]
        for o in ol:
            dots = [R[:, 2] @ n for R, _ in o["ippe"]]
            k = int(np.argmax(dots))
            if dots[k] > cos_tol:
                o["ippe"] = [o["ippe"][k]]
                o["ippe_err"] = [o["ippe_err"][k]]
                o["resolved"] = True
