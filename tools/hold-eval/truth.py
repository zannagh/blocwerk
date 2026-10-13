"""Builds the evaluation set from a copy of the production database: no labelling, only what people already did.

    python truth.py --db blocwerk_stg_x --out <folder> [--wall ID] [--min-gen 2] [--port 55432]

Per panel photo that has holds it writes <out>/photos/<panel id>.jpg and <out>/truth/<panel id>.json:

  holds      every hold of that photo's generation that is really on it (virtual holds and holds that were only kept
             without being found again are left out and listed under "ignore"), with its outline in fractions of the
             photo, its colour, the boulders that use it and where its shape comes from ("trust"):
               hand            a person drew or edited the outline
               hand-circle     a person placed, moved or resized it, and it is a plain circle
               detected        an automatic outline that was left as it is
               detected-circle an automatic circle that was left as it is
  negatives  spots where a person said "no hold": holds deleted in the editor or the look-over, and discarded
             detections answered with "not a hold"
  ignore     spots that say nothing either way (open discarded detections, kept-not-confirmed and virtual holds)

Reads only. The output holds personal data (photos of a private wall): keep it out of the repository.
"""
import argparse
import io
import json
import pathlib

import psycopg
from PIL import Image
from psycopg.rows import dict_row

ATTIC = "e0959f0a-cc71-4da1-be39-889d8bbc4822"
GEOMETRY = {"X", "Y", "Radius", "ShapePoints", "ShapeControlPoints"}
JOURNAL_SQL = '''
select b."Label" label, b."Actor" is not null has_actor, e."Op" op,
       e."KeyJson"::jsonb->>'Id' id, e."BeforeJson"::jsonb before, e."AfterJson"::jsonb after
from "ChangeJournalEntries" e join "ChangeJournalBatches" b on b."Id"=e."BatchId"
where e."EntityType"='Hold' and b."Label" in ('adhoc','hold-edit','hold-delete','hold-duplicate-merge','look-over-answer')
order by e."CreatedAt", e."Seq"
'''


def rows(con, sql: str, *args) -> list[dict]:
    return [dict(r) for r in con.execute(sql, args).fetchall()]


def columns(con, table: str) -> set[str]:
    return {r["column_name"] for r in rows(con, "select column_name from information_schema.columns where table_name=%s", table)}


def journal(con) -> dict:
    """What people did to holds by hand, per hold id, and the rows of the holds they deleted."""
    out = {"added": set(), "lookOverAdded": set(), "reshaped": set(), "resized": set(), "moved": set(), "deleted": []}
    for e in rows(con, JOURNAL_SQL):
        before, after = e["before"] or {}, e["after"] or {}
        if e["op"] == 0 and e["label"] == "adhoc" and e["has_actor"]:
            out["added"].add(e["id"])
        elif e["op"] == 0 and e["label"] == "look-over-answer":
            out["lookOverAdded"].add(e["id"])
        elif e["op"] == 2 and e["label"] in ("hold-delete", "hold-duplicate-merge"):
            out["deleted"].append(before | {"_why": "merged-away" if e["label"] == "hold-duplicate-merge" else "deleted"})
        elif e["op"] == 1 and e["label"] == "hold-edit":
            changed = {k for k in after if after[k] != before.get(k)} & GEOMETRY
            if changed & {"ShapePoints", "ShapeControlPoints"}:
                out["reshaped"].add(e["id"])
            if "Radius" in changed:
                out["resized"].add(e["id"])
            if changed & {"X", "Y"}:
                out["moved"].add(e["id"])
    return out


def parse(value):
    return json.loads(value) if isinstance(value, str) and value else value


def outline(row: dict) -> list[list[float]] | None:
    shape = parse(row.get("ShapePoints"))
    if not shape or len(shape) < 3:
        return None
    return [[row["X"] + p["Dx"], row["Y"] + p["Dy"]] for p in shape]


def holes(row: dict) -> list[list[list[float]]]:
    rings = parse(row.get("ShapeHoles")) or []
    return [[[row["X"] + p["Dx"], row["Y"] + p["Dy"]] for p in ring] for ring in rings if len(ring) >= 3]


def trust(row: dict, hand: dict) -> str:
    hid = str(row["Id"])
    shaped = outline(row) is not None
    by_hand = not row["IsAutoDetected"] or row.get("OutlineSource") == 0 or bool(parse(row.get("ShapeControlPoints")))
    if shaped:
        # A hand-placed hold can carry an automatic outline (the outline upgrade): only source 0, control points or a
        # journalled reshape say a person made this outline.
        drawn = row.get("OutlineSource") == 0 or bool(parse(row.get("ShapeControlPoints"))) or hid in hand["reshaped"]
        return "hand" if drawn else "detected"
    touched = by_hand or hid in hand["added"] or hid in hand["resized"] or hid in hand["moved"]
    return "hand-circle" if touched else "detected-circle"


def provenance(row: dict, hand: dict) -> list[str]:
    hid = str(row["Id"])
    tags = [name for name in ("added", "lookOverAdded", "reshaped", "resized", "moved") if hid in hand[name]]
    if not row["IsAutoDetected"]:
        tags.append("not-auto")
    return tags


def spot(row: dict, why: str) -> dict:
    return {"x": row["X"], "y": row["Y"], "radius": row["Radius"], "outline": outline(row), "why": why,
            "id": str(row["Id"]) if row.get("Id") else None}


def panel_truth(con, panel: dict, hand: dict, stacked: bool) -> dict:
    wall, gen, pid = panel["WallId"], panel["Generation"], panel["Id"]
    holds = rows(con, 'select * from "Holds" where "WallPanelId"=%s and "Generation"=%s order by "Id"', pid, gen)
    uses = rows(con, 'select bh."HoldId", b."Name", b."IsArchived" or b."IsHistoric" old from "BoulderHolds" bh '
                     'join "Boulders" b on b."Id"=bh."BoulderId" where b."WallId"=%s', wall)
    boulders: dict[str, list[str]] = {}
    for use in uses:
        boulders.setdefault(str(use["HoldId"]), []).append(use["Name"] or "?")
    live, ignore = [], []
    for row in holds:
        if row["IsVirtual"] or row.get("KeptUnconfirmedGeneration") is not None:
            ignore.append(spot(row, "virtual" if row["IsVirtual"] else "kept-not-confirmed"))
            continue
        live.append({
            "id": str(row["Id"]), "x": row["X"], "y": row["Y"], "radius": row["Radius"], "outline": outline(row),
            "holes": holes(row), "colour": row["Color"], "name": row["Name"], "auto": row["IsAutoDetected"],
            "outlineSource": row.get("OutlineSource"), "confidence": row["Confidence"], "kickboard": row["IsOnKickboard"],
            "boulders": sorted(boulders.get(str(row["Id"]), [])), "trust": trust(row, hand),
            "provenance": provenance(row, hand),
            "stackedOn": str(row["StackedOnHoldId"]) if stacked and row.get("StackedOnHoldId") else None,
        })
    negatives = [spot(r, r["_why"]) for r in hand["deleted"]
                 if r.get("WallPanelId") == str(pid) and r.get("Generation") == gen and not r.get("IsVirtual")]
    findings = rows(con, 'select "X","Y","Radius","ShapePoints","Status","Answer" from "WallLookOverFindings" '
                         'where "PanelId"=%s and "Generation"=%s and "Kind"=8', pid, gen)
    for f in findings:
        if f["Answer"] == 6:
            continue  # added as a hold: it is in the holds
        (negatives if f["Status"] == 1 else ignore).append(spot(f, "rejected-detection" if f["Status"] == 1 else "open-discard"))
    return {"panel": str(pid), "wall": str(wall), "generation": gen, "col": panel["Col"], "row": panel["Row"],
            "holds": live, "negatives": negatives, "ignore": ignore}


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--db", required=True)
    parser.add_argument("--out", required=True, type=pathlib.Path)
    parser.add_argument("--wall", default=ATTIC)
    parser.add_argument("--min-gen", type=int, default=2)
    parser.add_argument("--host", default="localhost")
    parser.add_argument("--port", type=int, default=55432)
    parser.add_argument("--user", default="blocwerk")
    args = parser.parse_args()
    (args.out / "photos").mkdir(parents=True, exist_ok=True)
    (args.out / "truth").mkdir(parents=True, exist_ok=True)
    with psycopg.connect(host=args.host, port=args.port, user=args.user, dbname=args.db, row_factory=dict_row) as con:
        hand = journal(con)
        stacked = "StackedOnHoldId" in columns(con, "Holds")
        panels = rows(con, 'select "Id","WallId","Generation","Col","Row","Photo" from "WallPanels" '
                           'where "WallId"=%s and "Generation">=%s and "Photo" is not null order by "Generation","Col","Row"',
                      args.wall, args.min_gen)
        for panel in panels:
            photo = panel.pop("Photo")
            truth = panel_truth(con, panel, hand, stacked)
            if not truth["holds"]:
                continue
            truth["width"], truth["height"] = Image.open(io.BytesIO(photo)).size
            (args.out / "photos" / f"{panel['Id']}.jpg").write_bytes(photo)
            (args.out / "truth" / f"{panel['Id']}.json").write_text(json.dumps(truth))
            kinds: dict[str, int] = {}
            for hold in truth["holds"]:
                kinds[hold["trust"]] = kinds.get(hold["trust"], 0) + 1
            print(f"gen {truth['generation']} panel {truth['col']},{truth['row']} {panel['Id']}: {len(truth['holds'])} holds {kinds}, "
                  f"{len(truth['negatives'])} negatives, {len(truth['ignore'])} ignored")


if __name__ == "__main__":
    main()
