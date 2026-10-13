"""Scores one detector run against the evaluation set.

    python score.py --truth <folder>/truth --run <run folder> [--out scores.json] [--panels ID,ID]

The run folder holds one JSON per photo as run-detector.sh writes it. Prints a table per panel and size class and
writes every number plus the per-hold records (which detection is which hold, every merge, split, miss and extra)
to --out, which tiles.py draws.

How things are counted
  match       a detection is a hold when their outlines overlap at IoU >= 0.5, or when one's centre lies in the other's
              outline and they overlap at IoU >= 0.1 (so a small hold on a volume is never matched to the volume).
              One detection per hold, best overlap first.
  merge       one detected outline that covers (>= 60 % of each) two or more holds that are not stacked on each other:
              the outline bled from one hold onto its neighbour.
  split       a hold with a second detection lying (>= 60 %) inside it that is at least 15 % of its size and is not a
              hold of its own; smaller ones are counted as "parts" (bolt holes, features).
  extras      detections that are no hold: "rejected" where a person already said no hold, "ignored" where the data
              says nothing, "unverified" for the rest (new holds, or false detections: only looking tells).
  precision   matched / (matched + rejected + parts + split pieces + unverified): a lower bound, since some
              unverified ones are real holds nobody recorded.
"""
import argparse
import json
import pathlib
import statistics
from collections import Counter, defaultdict

from shapely.geometry import Point
from shapely.strtree import STRtree

import geometry as geo

MATCH_IOU = 0.5
CENTRE_IOU = 0.1
COVER = 0.6
SPLIT_SHARE = 0.15
STACK_INSIDE = 0.7
STACK_AREA = 4.0


def load_run(path: pathlib.Path) -> dict:
    run = json.loads(path.read_text())
    holds = []
    for h in run["holds"]:
        shape = h.get("shapePoints")
        holds.append({
            "index": h["index"], "x": h["x"], "y": h["y"], "radius": h["radius"],
            "outline": [[h["x"] + p[0], h["y"] + p[1]] for p in shape] if shape else None,
            "volume": h.get("volume", False), "confidence": h.get("confidence"), "method": h.get("method"),
            "colour": h.get("color"), "stackedOn": h.get("stackedOn"), "onMarker": h.get("onMarker", False),
            "boxWidth": h.get("boxWidth"), "boxHeight": h.get("boxHeight"), "outlineConfidence": h.get("outlineConfidence"),
        })
    run["holds"] = holds
    return run


def derive_stacks(truth: list[dict], polys: list) -> dict[int, int]:
    """Truth index of a hold -> truth index of the hold it sits on: recorded, else by the app's rule on the curated outlines."""
    by_id = {h["id"]: i for i, h in enumerate(truth)}
    stacks = {i: by_id[h["stackedOn"]] for i, h in enumerate(truth) if h.get("stackedOn") in by_id}
    tree = STRtree(polys)
    for i, poly in enumerate(polys):
        if i in stacks:
            continue
        bases = [j for j in tree.query(poly) if j != i and polys[j].area >= STACK_AREA * poly.area
                 and geo.inside(poly, polys[j]) >= STACK_INSIDE]
        if bases:
            stacks[i] = min(bases, key=lambda j: polys[j].area)
    return stacks


def match(truth_polys: list, det_polys: list) -> dict[int, tuple[int, float]]:
    """Truth index -> (detection index, IoU), one to one, best overlap first."""
    tree = STRtree(det_polys)
    pairs = []
    for ti, tp in enumerate(truth_polys):
        for di in tree.query(tp):
            dp = det_polys[di]
            overlap = geo.iou(tp, dp)
            centre = tp.contains(Point(dp.centroid)) or dp.contains(Point(tp.centroid))
            if overlap >= MATCH_IOU or (centre and overlap >= CENTRE_IOU):
                pairs.append((overlap, ti, int(di)))
    matched, used = {}, set()
    for overlap, ti, di in sorted(pairs, reverse=True):
        if ti not in matched and di not in used:
            matched[ti] = (di, overlap)
            used.add(di)
    return matched


def score_panel(truth: dict, run: dict) -> dict:
    width, height = truth["width"], truth["height"]
    long_side = max(width, height)
    holds = truth["holds"]
    dets = [d for d in run["holds"] if not d["onMarker"]]
    tp = [geo.polygon(h, width, height) for h in holds]
    dp = [geo.polygon(d, width, height) for d in dets]
    stacks = derive_stacks(holds, tp)
    matched = match(tp, dp)
    det_to_truth = {di: ti for ti, (di, _) in matched.items()}
    truth_tree = STRtree(tp)

    records = []
    for ti, hold in enumerate(holds):
        rec = {"id": hold["id"], "size": geo.size_class(tp[ti], long_side), "colour": hold["colour"] or "none",
               "trust": hold["trust"], "detection": None, "iou": 0.0, "boundaryF": 0.0, "circleOnly": False,
               "stackedOn": holds[stacks[ti]]["id"] if ti in stacks else None, "merged": False, "split": 0, "parts": 0}
        if ti in matched:
            di, overlap = matched[ti]
            rec |= {"detection": dets[di]["index"], "iou": round(overlap, 4),
                    "boundaryF": round(geo.boundary_f(tp[ti], dp[di], geo.boundary_tolerance(tp[ti])), 4),
                    "circleOnly": dets[di]["outline"] is None}
        records.append(rec)

    # Merges: one detected outline over two or more holds that are not a hold and what it sits on.
    merges = []
    for di, poly in enumerate(dp):
        covered = [int(ti) for ti in truth_tree.query(poly) if geo.inside(tp[ti], poly) >= COVER]
        own = det_to_truth.get(di)
        peers = [ti for ti in covered if not _is_child_of(ti, own, stacks) and not _same_hold_twice(ti, covered, tp)]
        if own is not None and own not in peers and own in covered:
            peers.append(own)
        if len(set(peers)) >= 2:
            merges.append({"detection": dets[di]["index"], "holds": [holds[ti]["id"] for ti in sorted(set(peers))]})
            for ti in peers:
                records[ti]["merged"] = True

    # What every unmatched detection is.
    extras = []
    det_records = {}
    for di, poly in enumerate(dp):
        if di in det_to_truth:
            det_records[dets[di]["index"]] = {"status": "hold", "hold": holds[det_to_truth[di]]["id"]}
            continue
        host = max((int(ti) for ti in truth_tree.query(poly) if geo.inside(poly, tp[ti]) >= COVER),
                   key=lambda ti: geo.inside(poly, tp[ti]), default=None)
        centre = Point(poly.centroid)
        if host is not None:
            piece = poly.area >= SPLIT_SHARE * tp[host].area
            records[host]["split" if piece else "parts"] += 1
            status = "split" if piece else "part"
        elif _on_spot(centre, truth["negatives"], width, height):
            status = "rejected"
        elif _on_spot(centre, truth["ignore"], width, height):
            status = "ignored"
        else:
            status = "unverified"
        det_records[dets[di]["index"]] = {"status": status, "hold": holds[host]["id"] if host is not None else None}
        extras.append(status)

    return {"panel": truth["panel"], "generation": truth["generation"], "col": truth["col"], "width": width, "height": height,
            "holds": records, "detections": det_records, "merges": merges,
            "stacks": _stack_counts(holds, stacks, matched, dets, det_to_truth, dp, tp),
            "extras": dict(Counter(extras)), "detectMs": run.get("detectMs"), "outlineMs": run.get("outlineMs"),
            "detected": len(dets), "onMarkers": sum(1 for d in run["holds"] if d["onMarker"]),
            "methods": dict(Counter(d["method"] for d in dets))}


def _is_child_of(ti: int, own: int | None, stacks: dict[int, int]) -> bool:
    """True when the hold sits (directly or further up) on the hold the detection is."""
    seen = set()
    while own is not None and ti in stacks and ti not in seen:
        seen.add(ti)
        ti = stacks[ti]
        if ti == own:
            return True
    return False


def _same_hold_twice(ti: int, covered: list[int], polys: list) -> bool:
    """True for the later of two recorded holds that are one hold (they overlap at IoU >= 0.5): counted once."""
    return any(other < ti and geo.iou(polys[ti], polys[other]) >= MATCH_IOU for other in covered)


def _on_spot(centre: Point, spots: list[dict], width: int, height: int) -> bool:
    return any(geo.polygon(s, width, height).buffer(2).contains(centre) for s in spots
               if abs(s["x"] * width - centre.x) < 0.2 * width and abs(s["y"] * height - centre.y) < 0.2 * height)


def _stack_counts(holds, stacks, matched, dets, det_to_truth, dp, tp) -> dict:
    by_index = {d["index"]: i for i, d in enumerate(dets)}
    counts = Counter()
    pairs = []
    for child, base in stacks.items():
        if base not in matched:
            outcome = "base-missed"
        elif child not in matched:
            outcome = "merged" if geo.inside(tp[child], dp[matched[base][0]]) >= COVER else "child-missed"
        else:
            on = dets[matched[child][0]]["stackedOn"]
            outcome = "ok" if on is not None and by_index.get(on) == matched[base][0] else "two-holds-no-relation"
        counts[outcome] += 1
        pairs.append({"child": holds[child]["id"], "base": holds[base]["id"], "outcome": outcome})
    false_stacks = []
    for di, det in enumerate(dets):
        on = by_index.get(det["stackedOn"]) if det["stackedOn"] is not None else None
        if on is None:
            continue
        child, base = det_to_truth.get(di), det_to_truth.get(on)
        if child is None or base is None or stacks.get(child) != base:
            reason = "child-is-no-hold" if child is None else "base-is-no-hold" if base is None else "not-stacked-in-truth"
            false_stacks.append({"detection": det["index"], "on": det["stackedOn"], "reason": reason})
    counts["false-stack"] = len(false_stacks)
    return {"counts": dict(counts), "pairs": pairs, "falseStacks": false_stacks}


def summarise(panel: dict) -> dict:
    """The numbers of one panel, overall and per size class, colour and shape trust."""
    def block(records: list[dict]) -> dict:
        found = [r for r in records if r["detection"] is not None]
        shaped = [r for r in found if not r["circleOnly"]]
        return {
            "holds": len(records), "found": len(found), "recall": _ratio(len(found), len(records)),
            "circleOnly": sum(1 for r in found if r["circleOnly"]),
            "meanIou": _mean(r["iou"] for r in found), "medianIou": _median(r["iou"] for r in found),
            "meanIouOutlined": _mean(r["iou"] for r in shaped),
            "meanBoundaryF": _mean(r["boundaryF"] for r in shaped),
            "merged": sum(1 for r in records if r["merged"]), "split": sum(1 for r in records if r["split"]),
        }

    holds = panel["holds"]
    extras = panel["extras"]
    found = sum(1 for r in holds if r["detection"] is not None)
    wrong = extras.get("rejected", 0) + extras.get("part", 0) + extras.get("split", 0) + extras.get("unverified", 0)
    out = {"all": block(holds) | {
        "detections": panel["detected"], "precisionLowerBound": _ratio(found, found + wrong),
        "precisionKnownWrongOnly": _ratio(found, found + extras.get("rejected", 0) + extras.get("part", 0) + extras.get("split", 0)),
        "extras": extras, "mergeErrors": len(panel["merges"]), "splitErrors": sum(1 for r in holds if r["split"]),
        "stacks": panel["stacks"]["counts"], "detectMs": panel["detectMs"], "outlineMs": panel["outlineMs"],
        "methods": panel["methods"]}}
    for key in ("size", "trust", "colour"):
        groups = defaultdict(list)
        for r in holds:
            groups[r[key]].append(r)
        out[key] = {name: block(rs) for name, rs in sorted(groups.items())}
    return out


def _ratio(a: int, b: int) -> float | None:
    return round(a / b, 4) if b else None


def _mean(values) -> float | None:
    values = [v for v in values if v == v]
    return round(statistics.fmean(values), 4) if values else None


def _median(values) -> float | None:
    values = [v for v in values if v == v]
    return round(statistics.median(values), 4) if values else None


def print_table(name: str, summary: dict) -> None:
    a = summary["all"]
    print(f"\n{name}: {a['holds']} holds, {a['detections']} detections, recall {a['recall']}, precision >= {a['precisionLowerBound']} "
          f"(known wrong only: {a['precisionKnownWrongOnly']}), merges {a['mergeErrors']}, splits {a['splitErrors']}, "
          f"circle-only {a['circleOnly']}, extras {a['extras']}, stacks {a['stacks']}")
    print(f"  {'class':<16}{'holds':>6}{'found':>6}{'recall':>8}{'circle':>7}{'IoU':>7}{'IoU(outl)':>10}{'bF':>7}{'merged':>7}{'split':>6}")
    for key in ("size", "trust"):
        for cls, b in summary[key].items():
            print(f"  {cls:<16}{b['holds']:>6}{b['found']:>6}{_s(b['recall']):>8}{b['circleOnly']:>7}{_s(b['meanIou']):>7}"
                  f"{_s(b['meanIouOutlined']):>10}{_s(b['meanBoundaryF']):>7}{b['merged']:>7}{b['split']:>6}")


def _s(value) -> str:
    return "-" if value is None else f"{value:.3f}"


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--truth", required=True, type=pathlib.Path)
    parser.add_argument("--run", required=True, type=pathlib.Path)
    parser.add_argument("--out", type=pathlib.Path)
    parser.add_argument("--panels", default="")
    args = parser.parse_args()
    wanted = {p for p in args.panels.split(",") if p}
    result = {"run": str(args.run), "panels": {}, "summary": {}}
    for truth_file in sorted(args.truth.glob("*.json")):
        run_file = args.run / truth_file.name
        if not run_file.exists() or (wanted and truth_file.stem not in wanted):
            continue
        truth = json.loads(truth_file.read_text())
        panel = score_panel(truth, load_run(run_file))
        summary = summarise(panel)
        result["panels"][truth["panel"]] = panel
        result["summary"][truth["panel"]] = summary
        print_table(f"gen {truth['generation']} panel {truth['col']},{truth['row']}", summary)
    if args.out:
        args.out.write_text(json.dumps(result))


if __name__ == "__main__":
    main()
