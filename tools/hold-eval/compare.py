"""Builds the hold-by-hold comparison of a wall: what is on it now, what the old detector makes of the photo, and
what the new one does.

    python compare.py --truth <folder>/truth --photos <folder>/photos --old <run> --new <re-detect run> --out <folder>
                      [--panels ID,ID] [--verdicts verdicts.json] [--cases cases.json]

--old is a run of run-detector.sh with the old settings (--seed=circle --segmenter=false), --new the output of
run-detector.sh --redetect, which carries the proposals. Writes, in the layout the review page reads:

  holds.json      one entry per hold on the wall, then one per detection only the new detector proposes: status,
                  the measured numbers, the sprite of its tile, and the verdict where somebody looked (--verdicts maps
                  an id to {"verdict", "note"}; everything else is "not judged")
  sheet-NN.jpg    the tiles: three crops of the same spot at the same scale, on the wall / old detector / new detector
  panels/         the whole panel with the old and with the new outlines
  summary.json    counts per status and the score table, old against new

--cases names the owner's cases: {"owner-case": {"<hold id prefix on the wall>": "<the id the owner named>"},
"spots": [{"ownerId", "panel", "x", "y"}]}; a spot gets a tile of its own with whatever lies on it. The output holds photos of a private
wall: keep it out of the repository.
"""
import argparse
import json
import pathlib
from collections import Counter

import numpy as np
from PIL import Image

import geometry as geo
import score
import tiles

COLUMNS, ROWS = 5, 10
ORDER = ["split", "merged", "stacked", "new", "missing", "shape-changed", "same", "spot"]
PANEL_NAMES = {0: "centre", 1: "right"}


def by_hold(proposals: list[dict]) -> dict[str, dict]:
    """The proposal of each stored hold; a merge and a stack also name their other hold."""
    out: dict[str, dict] = {}
    for p in proposals:
        for hold in [p.get("holdId")] + list(p.get("otherHoldIds") or []):
            if hold and (hold not in out or ORDER.index(status_of(p)) < ORDER.index(status_of(out[hold]))):
                out[hold] = p
    return out


def status_of(proposal: dict | None) -> str:
    return {"Split": "split", "Merge": "merged", "Stack": "stacked", "NewHold": "new", "NotFound": "missing",
            "HandShape": "shape-changed", "BetterShape": "shape-changed"}.get((proposal or {}).get("kind"), "same")


def measured(poly_truth, det_poly) -> dict:
    return {"iou": round(geo.iou(poly_truth, det_poly), 3), "thin": round(max(geo.implausibility(det_poly)), 3),
            "covers": round(geo.inside(poly_truth, det_poly), 3)}


def shared_detection(poly, det_polys: list) -> int | None:
    """The detection that overlaps a hold best although another hold already has it: the same hold recorded twice."""
    best = max(range(len(det_polys)), key=lambda i: geo.iou(poly, det_polys[i]), default=None)
    return best if best is not None and geo.iou(poly, det_polys[best]) >= 0.3 else None


def panel_entries(truth: dict, old: dict, new: dict, photo: Image.Image, spots: list[dict]) -> tuple[list[dict], list[Image.Image], dict]:
    width, height = truth["width"], truth["height"]
    name = PANEL_NAMES.get(truth["col"], f"{truth['col']},{truth['row']}")
    holds = truth["holds"]
    tp = [geo.polygon(h, width, height) for h in holds]
    olds = [d for d in old["holds"] if not d["onMarker"]]
    news = new["holds"]
    op, np_ = [geo.polygon(d, width, height) for d in olds], [geo.polygon(d, width, height) for d in news]
    old_match, new_match = score.match(tp, op), score.match(tp, np_)
    proposals = by_hold(new["proposals"])
    index = {h["id"]: i for i, h in enumerate(holds)}
    new_at = {d["index"]: i for i, d in enumerate(news)}
    entries, images = [], []
    bgr = np.asarray(photo)[:, :, ::-1]
    for ti, hold in enumerate(holds):
        proposal = proposals.get(hold["id"])
        status = status_of(proposal)
        focus_new = [new_at[i] for i in (proposal or {}).get("detections", []) if i in new_at] if proposal and proposal.get("holdId") == hold["id"] else []
        if not focus_new and ti in new_match:
            focus_new = [new_match[ti][0]]
        shares = False
        if not focus_new and shared_detection(tp[ti], np_) is not None:
            focus_new, shares = [shared_detection(tp[ti], np_)], True
        if status == "same" and not focus_new:
            status = "missing"
        focus_old = [old_match[ti][0]] if ti in old_match else []
        if not focus_old and shared_detection(tp[ti], op) is not None:
            focus_old = [shared_detection(tp[ti], op)]
        others = [index[i] for i in [(proposal or {}).get("holdId")] + list((proposal or {}).get("otherHoldIds") or [])
                  if proposal and status in ("merged", "stacked") and i in index and i != hold["id"]]
        entry = {
            "id": hold["id"], "panel": name, "name": hold["name"], "colour": hold["colour"], "boulders": hold["boulders"],
            "handMade": hold["trust"].startswith("hand") or bool(hold["provenance"]), "status": status,
            "verdict": "not judged", "note": "", "reviewed": False,
            "tags": (["bulk-safe"] if (proposal or {}).get("safe") else []) + (["shares-detection"] if shares else [])
            + (["hand-proposal"] if (proposal or {}).get("kind") == "HandShape" else []) + (["used-by-boulder"] if hold["boulders"] else [])
            + [f"hand-{p}" for p in hold["provenance"]],
            "size": geo.size_class(tp[ti], max(width, height)), "contrast": round(geo.contrast(bgr, tp[ti]), 1),
            "circleOnWall": hold["outline"] is None,
        }
        entry["lowContrast"] = entry["contrast"] < geo.LOW_CONTRAST
        if entry["lowContrast"]:
            entry["tags"].append("low-contrast")
        if focus_old:
            o = olds[focus_old[0]]
            entry |= {"iouOld": measured(tp[ti], op[focus_old[0]])["iou"], "thinOld": measured(tp[ti], op[focus_old[0]])["thin"],
                      "circleOld": o["outline"] is None, "fragmentOld": geo.is_fragment(o, op[focus_old[0]], width, height)}
        if focus_new:
            m, d = measured(tp[ti], np_[focus_new[0]]), news[focus_new[0]]
            entry |= {"iouNew": m["iou"], "thinNew": m["thin"], "coversNew": m["covers"], "circleNew": d["outline"] is None,
                      "fragmentNew": geo.is_fragment(d, np_[focus_new[0]], width, height),
                      "roughNew": d["outline"] is not None and (d.get("outlineConfidence") or 1) <= 0.2,
                      "detectorConfidence": d.get("confidence")}
        if status == "stacked" and proposal:
            base = proposal["otherHoldIds"][0]
            entry |= {"role": "stacked", "stackedOn": base} if proposal["holdId"] == hold["id"] else {"role": "base", "children": [proposal["holdId"]]}
        if focus_old and focus_new and olds[focus_old[0]]["outline"] and news[focus_new[0]]["outline"]:
            o, n = op[focus_old[0]], np_[focus_new[0]]
            entry |= {"areaNewOverOld": round(n.area / o.area, 3) if o.area else None,
                      "newInsideOld": round(geo.inside(n, o), 3), "oldInsideNew": round(geo.inside(o, n), 3)}
        entry["blank"] = [i for i, f in ((1, focus_old), (2, focus_new)) if not f]
        frame = tiles.window([hold] + [holds[i] for i in others] + [olds[i] for i in focus_old] + [news[i] for i in focus_new], width, height)
        is_child = status == "stacked" and entry.get("role") == "stacked"
        wall_layer = tiles.Layer(holds, [ti] if not others or not is_child else [], others if is_child else [], [ti] if is_child else others if status == "stacked" else [])
        if status == "merged":
            wall_layer = tiles.Layer(holds, [ti] + others)
        images.append(tiles.tile(photo, [wall_layer, tiles.Layer(olds, focus_old) if focus_old else None,
                                         tiles.Layer(news, focus_new) if focus_new else None], frame))
        entries.append(entry)
    for proposal in new["proposals"]:
        if proposal["kind"] != "NewHold":
            continue
        di = new_at[proposal["detections"][0]]
        near = [i for i, p in enumerate(op) if geo.iou(p, np_[di]) >= 0.3]
        entries.append({
            "id": f"new-{name}-{proposal['detections'][0]:04d}", "panel": name, "name": None, "colour": news[di]["colour"], "boulders": [],
            "handMade": False, "status": "new", "verdict": "not judged", "note": "", "reviewed": False, "tags": [],
            "size": geo.size_class(np_[di], max(width, height)), "thinNew": round(max(geo.implausibility(np_[di])), 3),
            "circleNew": news[di]["outline"] is None, "blank": [0] + ([] if near else [1]), "alsoOldDetector": bool(near),
        })
        frame = tiles.window([news[di]], width, height)
        images.append(tiles.tile(photo, [tiles.Layer(holds, []), tiles.Layer(olds, near[:1]) if near else None, tiles.Layer(news, [di])], frame))
    for spot in [s for s in spots if s["panel"] == name]:
        point = geo.Point(spot["x"] * width, spot["y"] * height)
        at = lambda polys: [i for i, p in enumerate(polys) if p.buffer(3).contains(point)]
        wall_at, old_at, new_at_spot = at(tp), at(op), at(np_)
        shown = [holds[i] for i in wall_at] + [olds[i] for i in old_at] + [news[i] for i in new_at_spot]
        frame = tiles.window(shown, width, height) if shown else (point.x, point.y, 90.0)
        entries.append({
            "id": f"spot-{spot['ownerId']}", "ownerId": spot["ownerId"], "panel": name, "name": None, "colour": None, "boulders": [],
            "handMade": False, "status": "spot", "verdict": "not judged", "note": "", "reviewed": False, "tags": ["owner-case"],
            "wallHolds": [holds[i]["id"] for i in wall_at], "oldDetections": len(old_at), "newDetections": len(new_at_spot),
            "newProposed": sum(1 for p in new["proposals"] for i in new_at_spot if news[i]["index"] in p["detections"]),
            "blank": [i for i, f in ((0, wall_at), (1, old_at), (2, new_at_spot)) if not f],
        })
        images.append(tiles.tile(photo, [tiles.Layer(holds, wall_at), tiles.Layer(olds, old_at) if old_at else None,
                                         tiles.Layer(news, new_at_spot) if new_at_spot else None], frame))
    overlays = {"old": tiles.panel_image(photo, olds), "new": tiles.panel_image(photo, news), "wall": tiles.panel_image(photo, holds)}
    return entries, images, overlays


def sort_key(entry: dict) -> tuple:
    judged = {"wrong": 0, "worse": 1, "doubtful": 2}.get(entry["verdict"], 3)
    case = 0 if "owner-case" in entry["tags"] else 1
    return case, judged, ORDER.index(entry["status"]), entry.get("iouNew", 0.0), entry["id"]


def score_table(truth_dir: pathlib.Path, run_dir: pathlib.Path, panels: list[str]) -> dict:
    table = {}
    for pid in panels:
        truth = json.loads((truth_dir / f"{pid}.json").read_text())
        panel = score.score_panel(truth, score.load_run(run_dir / f"{pid}.json"))
        a = score.summarise(panel)
        width, height = truth["width"], truth["height"]
        dets = [geo.polygon(d, width, height) for d in score.load_run(run_dir / f"{pid}.json")["holds"] if d["outline"] and not d["onMarker"]]
        hand = a["trust"].get("hand-circle", {}) | {}
        table[PANEL_NAMES.get(truth["col"], pid)] = {
            "holds": a["all"]["holds"], "detections": a["all"]["detections"], "recall": a["all"]["recall"],
            "handAddedFound": hand.get("recall"), "bleedingOutlines": a["all"]["mergeErrors"], "splitHolds": a["all"]["splitErrors"],
            "circleOnly": a["all"]["circleOnly"], "implausibleOutlines": sum(1 for p in dets if geo.is_implausible(p)),
            "outlined": len(dets), "onRejectedSpots": a["all"]["extras"].get("rejected", 0),
            "unverifiedExtras": a["all"]["extras"].get("unverified", 0), "parts": a["all"]["extras"].get("part", 0),
            "bySize": {k: {"holds": v["holds"], "recall": v["recall"], "circleOnly": v["circleOnly"]} for k, v in a["size"].items()},
        }
    return table


def subset_table(entries: list[dict]) -> dict:
    """Outline failures on a set of holds, old detector against new: no shape, a fragment of the box, a rough shape."""
    def side(tag: str) -> dict:
        found = [e for e in entries if f"circle{tag}" in e]
        return {"found": len(found), "noShape": sum(1 for e in found if e[f"circle{tag}"]),
                "fragment": sum(1 for e in found if e.get(f"fragment{tag}")),
                "roughShape": sum(1 for e in found if e.get(f"rough{tag}")),
                "tracedOutline": sum(1 for e in found if not e[f"circle{tag}"] and not e.get(f"fragment{tag}") and not e.get(f"rough{tag}"))}
    return {"holds": len(entries), "old": side("Old"), "new": side("New")}


def placeholder_table(entries: list[dict]) -> dict:
    """The holds that are only a circle on the wall, and what the new detector offers for them."""
    circles = [e for e in entries if e.get("circleOnWall")]
    offered = [e for e in circles if e["status"] == "shape-changed"
               and e.get("circleNew") is False and not e.get("roughNew")]
    return {"circleOnlyOnWall": len(circles), "tracedShapeProposed": len(offered),
            "ofThoseBulkSafe": sum(1 for e in offered if "bulk-safe" in e["tags"]),
            "roughShapeProposed": sum(1 for e in circles if e.get("roughNew") and e["status"] != "same"),
            "notDetected": sum(1 for e in circles if "circleNew" not in e)}


def against_old(entries: list[dict]) -> dict:
    """Where both detectors traced the hold: how often the new outline is clearly smaller and inside the old one (a rim
    or one colour of a two-tone hold cut off, unless the old one bled), or clearly larger and around it (it grew over
    the hold around a pale patch, unless the old one was a fragment)."""
    both = [e for e in entries if e.get("areaNewOverOld") is not None]
    smaller = [e for e in both if e["areaNewOverOld"] < 0.75 and e["newInsideOld"] >= 0.85]
    larger = [e for e in both if e["areaNewOverOld"] > 1.4 and e["oldInsideNew"] >= 0.85]
    return {"bothTraced": len(both), "newClearlySmaller": len(smaller), "newClearlyLarger": len(larger),
            "smallerIds": sorted(e["id"][:8] for e in smaller), "largerIds": sorted(e["id"][:8] for e in larger),
            "tracedToCircle": sorted(e["id"][:8] for e in entries if e.get("circleOld") is False and e.get("circleNew"))}


def new_hold_table(entries: list[dict]) -> dict:
    new = [e for e in entries if e["status"] == "new"]
    return {"proposed": len(new), "alsoFoundByOldDetector": sum(1 for e in new if e.get("alsoOldDetector")),
            "verdicts": dict(Counter(e["verdict"] for e in new))}


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    for name in ("truth", "photos", "old", "new", "out"):
        parser.add_argument(f"--{name}", required=True, type=pathlib.Path)
    parser.add_argument("--panels", default="")
    parser.add_argument("--verdicts", type=pathlib.Path)
    parser.add_argument("--cases", type=pathlib.Path)
    parser.add_argument("--extra", type=pathlib.Path, help="JSON merged into summary.json (legend, worse, unverified, notes, run)")
    args = parser.parse_args()
    panels = [p for p in args.panels.split(",") if p] or sorted(f.stem for f in args.new.glob("*.json") if f.stem != "scores")
    verdicts = json.loads(args.verdicts.read_text()) if args.verdicts else {}
    cases = json.loads(args.cases.read_text()) if args.cases else {}
    (args.out / "panels").mkdir(parents=True, exist_ok=True)
    entries, images = [], []
    for pid in panels:
        truth = json.loads((args.truth / f"{pid}.json").read_text())
        photo = Image.open(args.photos / f"{pid}.jpg").convert("RGB")
        new = json.loads((args.new / f"{pid}.json").read_text())
        new["holds"] = score.load_run(args.new / f"{pid}.json")["holds"]
        e, i, overlays = panel_entries(truth, score.load_run(args.old / f"{pid}.json"), new, photo, cases.get("spots", []))
        entries += e
        images += i
        for kind, image in overlays.items():
            image.save(args.out / "panels" / f"{PANEL_NAMES.get(truth['col'], pid)}-{kind}.jpg", quality=78)
    for entry in entries:
        for today, owner in cases.get("owner-case", {}).items():
            if entry["id"].startswith(today):
                entry["tags"].append("owner-case")
                entry["ownerId"] = owner
        given = next((v for k, v in verdicts.items() if entry["id"].startswith(k)), None)
        if given:
            entry |= {"verdict": given["verdict"], "note": given.get("note", ""), "reviewed": True}
    order = sorted(range(len(entries)), key=lambda k: sort_key(entries[k]))
    for n, sheet in enumerate(tiles.sheets([images[k] for k in order], COLUMNS, ROWS), start=1):
        sheet.save(args.out / f"sheet-{n:02d}.jpg", quality=72, optimize=True)
    per_sheet = COLUMNS * ROWS
    out_entries = []
    for position, k in enumerate(order):
        entries[k]["sprite"] = {"sheet": f"sheet-{position // per_sheet + 1:02d}.jpg", "x": (position % per_sheet % COLUMNS) * 3 * tiles.CELL,
                                "y": (position % per_sheet // COLUMNS) * tiles.CELL, "w": 3 * tiles.CELL, "h": tiles.CELL}
        out_entries.append(entries[k])
    (args.out / "holds.json").write_text(json.dumps(out_entries))
    summary = {
        "crops": ["On the wall", "Old detector", "New detector"],
        "status": dict(Counter(e["status"] for e in out_entries)),
        "verdict": dict(Counter(e["verdict"] for e in out_entries)),
        "reviewed": sum(1 for e in out_entries if e["reviewed"]),
        "scores": {"old": score_table(args.truth, args.old, panels), "new": score_table(args.truth, args.new, panels)},
        "lowContrast": subset_table([e for e in out_entries if e.get("lowContrast")]),
        "otherContrast": subset_table([e for e in out_entries if e.get("lowContrast") is False]),
        "placeholders": placeholder_table(out_entries),
        "againstOldOutline": against_old(out_entries),
        "newHolds": new_hold_table(out_entries),
    }
    if args.extra:
        summary |= json.loads(args.extra.read_text())
    (args.out / "summary.json").write_text(json.dumps(summary, indent=1))
    print(json.dumps(summary["status"]), json.dumps(summary["scores"], indent=1)[:3000])


if __name__ == "__main__":
    main()
