# `marker-plan.json` — the owner's marker plan (v1)

Written by the marker planner (`src/Blocwerk.Core/MarkerPlanning/`, `MarkerPlanJson.ToJson`) and meant to
**travel with every photo dump** of a marker wall. It answers the questions the solver would otherwise
have to guess or ask: which surfaces exist and how they join, roughly how big and how steep they are, which
marker sits on which surface and where, and how large each one was printed. Because of that, marker ids
carry **no meaning** — any id may sit on any surface (the old `segment*6+role` scheme is just one plan).

Companion of `wall-geometry.schema.md`: a plan is the *intent* (drawn by the owner), a geometry is the
*measurement* (solved from photos). A plan can also be built from a solved geometry
(`MarkerPlanFromGeometry.Build`) so walls that already carry markers get one without redrawing.

## Frames and units

- All lengths **mm**, angles **degrees**.
- **Segment frame** — identical to a solved facet's plane frame `(a, b)`: origin at the bottom-left of the
  segment's bounding box, `x` to the right as you face the surface, `y` **up the surface** (up the slope for
  an overhang), surface normal toward the climber.
- **Marker position** = the centre of its black square in its segment's frame. Squares are axis-aligned in
  that frame, printed with their `TOP` edge pointing up the surface.
- **Net** (the planner's drawing) — the surfaces unfolded flat, root at the origin, each child hinged onto
  its parent's edge. Only the attachments are stored; the net is recomputed (`NetLayout.Compute`).

## Shape

```jsonc
{
  "format": "blocwerk-marker-plan",      // optional on read; if present it must be this value
  "schemaVersion": 1,                     // readers refuse versions newer than theirs
  "dictionary": "DICT_4X4_50",            // the only dictionary the detector reads (ids 0..49)
  "photo": {
    "distanceMm": 2500,                   // usual camera-to-wall distance, 300..20000
    "cameraPreset": "phone-0.5x",         // "phone-1x" (≈69°, 4032 px) | "phone-0.5x" (≈104°, 4032 px) | "custom"
    "horizontalFovDeg": 104,              // 10..150
    "imageLongEdgePx": 4032               // 640..20000
  },
  "segments": [
    {
      "index": 0,                         // unique, stable; gaps allowed (The Attic uses 0,1,2,5)
      "name": "main wall",
      "shape": "rectangle",               // "rectangle" | "triangle" (right triangle)
      "widthMm": 5200,                    // rectangle width, or the triangle's horizontal leg
      "heightMm": 3400,                   // along the surface; or the triangle's vertical leg
      "rightAngle": "bottomLeft",         // triangles: bottomLeft|bottomRight|topLeft|topRight; ignored otherwise
      "overhangDeg": 45,                  // tilt from vertical: 0 vertical, + overhang, − slab
      "yawDeg": 0,                        // turn about the vertical axis relative to the ROOT segment
      "attachedTo": null                  // exactly one segment (the root) has null
    },
    {
      "index": 2, "name": "left triangle", "shape": "triangle", "widthMm": 2400, "heightMm": 2400,
      "rightAngle": "bottomLeft", "overhangDeg": 0, "yawDeg": 90,
      "attachedTo": {
        "parentIndex": 0,                 // the segment it touches
        "parentEdge": "left",             // top|right|bottom|left|hypotenuse (a triangle has its 2 legs + hypotenuse)
        "ownEdge": "hypotenuse",
        "offsetMm": 0                     // slide along the parent edge, from its lower (or left) end
      }
    }
  ],
  "markers": [
    { "id": 0, "segment": 0, "xMm": 85.1, "yMm": 3158.9, "sizeMm": 125, "role": "corner" },
    { "id": 24, "segment": 0, "xMm": 3581.6, "yMm": 3283.8, "sizeMm": 125, "role": "filler" }
  ],
  "print": {                              // optional (added in v1 without a bump; omitted when unset)
    "mountingHoles": {                    // optional
      "enabled": true,                    // print the holes
      "holeDiameterMm": 3,                // 1 | 2 | 2.5 | 3 | 3.5
      "screwHeadDiameterMm": 6,           // hole + 0.5 .. 15
      "gapToMarkerMm": 1,                 // optional, 0.5 .. 10 (default 1): head rim to black square
      "gapToEdgeMm": 1                    // optional, 0.5 .. 10 (default 1): head rim to cut line
    }
  }
}
```

### Mounting holes (`print.mountingHoles`)

Printing only — the solver ignores it. When enabled, the PDF prints one hole diagonally outward from each
corner of every black square: a light-grey circle at the true hole diameter with a crosshair (drill or
punch there) and a dashed light-grey circle at the screw-head diameter. The holes sit tight to the marker:
the per-axis offset of a hole centre from its corner is `d = (r + gapToMarkerMm)/√2` (r = head radius), so
the head's nearest point is the square's corner, exactly `gapToMarkerMm` away. The head reaches `d + r`
past the square on each axis, so the cut-out's white border is `d + r + gapToEdgeMm`, independent of the
marker size (no floor). Defaults 3 mm hole / 6 mm head / 1 mm / 1 mm: `d` = 2.83 mm, border 6.83 mm;
cut-out 138.7 mm for a 125 mm marker (was 182 mm), 113.7 mm at 100, 93.7 mm at 80, 63.7 mm at 50.
Plans without the two gap fields read as 1 mm each. Sizes are validated even while `enabled` is false.

Detection limits (measured by rendering the PDF and running the app's detector on it, dark screw heads
and a textured wall outside the cut line, 30–295 px per marker — `MountingHoleSafety`): the detector keeps
the black square rather than the paper's outline (ArUco's too-close filter off, nested same-id quads
collapsed to the inner one), so every marker decodes with the tight 1 / 1 mm default and any head gap
≥ 0.5 mm, on white, mid-grey and dark walls. What remains is corner accuracy: a white border under ~3 photo
px drifts the refined corners toward the wall (up to ~1.5 px). The planner warns (`mounting-holes-tight`)
with the photo px and the gaps that measure clean, e.g. 1 / 4 mm for 125 mm markers at 40 px (cut-out
145 mm); at the planned ~60 px the 1 / 1 mm default is clean (cut-out 139 mm).

### Attachments

The two edges are laid against each other like a cardboard net. Both outlines are counter-clockwise, so on
the shared edge they run in opposite directions — the child lands on the outside of the parent edge and the
physical corners meet with no mirroring flag. `offsetMm` moves the child's end on the parent-start side to
`start + offsetMm` along the parent edge (start = the lower end of the parent edge in the parent's own frame,
its left end when horizontal). Negative offsets are allowed.

### Roles

`corner` markers anchor a surface (sized for pose accuracy, ≥ 60 px on photos by default); `filler`
markers link photos along edges (≥ 40 px). Both are ordinary markers to the solver.

## With a photo dump (the in-app capture)

The capture upload takes the plan JSON next to the photos ("Marker plan (JSON, optional)"). The draft then
runs with that plan; it becomes the wall's saved plan when the wall has none, otherwise it applies to that
capture only (replacing a saved plan is done in the planner). Without an upload, the wall's saved plan is
used; without either, the legacy `segment*6+role` convention with the wall's one marker size. The capture
keeps a snapshot of the plan it started with (`WallCapture.PlanJson`).

Everything downstream reads the plan through `WallMarkerLayout` (`WallMarkerLayoutResolver`): detection
accepts only the plan's ids, the declarations table is pre-filled with the plan's surfaces, names and
angles (plumb surfaces become gravity references), and the solve request carries each marker's segment
and size (see `wall-geometry.schema.md`, `idScheme: "plan"`). After the solve, `MarkerPlacementChecker`
answers "did I place them right?": markers never seen or not placed, markers found on another surface
than planned, markers more than 100 mm from their planned spot (after a robust rigid fit of the plan onto
each solved facet, whose origin is its markers' bounding box), and surfaces more than 5° off the planned
angle. The list shows with the capture's result.

## Reading rules

- Unknown fields are ignored (add fields freely; bump `schemaVersion` only for breaking changes).
- Comments and trailing commas are tolerated. Enums are strings (case-insensitive on read).
- Numbers must be real JSON numbers (no strings, no NaN/Infinity) within the ranges above;
  segment sizes 1..100 000 mm, marker size 10..1000 mm, positions/offsets within ±100 000 mm.
- At most 1 MB, 200 segments, 1000 markers (the 50-id dictionary limit is a validation error, not a parse error).
- `print.mountingHoles` sizes are checked on read (hole one of 1/2/2.5/3/3.5 mm, head hole + 0.5 .. 15 mm,
  gaps 0.5 .. 10 mm; missing gaps default to 1 mm).
- Parse errors name the field, e.g. `markers[3].sizeMm must be between 10 and 1000 (was 5000).`

## Validation (`MarkerPlanValidator`)

Errors block saving; warnings (and `tip-*` codes) are advice.

| Code | Severity | Meaning |
|---|---|---|
| `schema-version`, `dictionary`, `no-segments` | error | unusable header |
| `photo-distance`, `photo-fov`, `photo-resolution` | error | implausible photo setup |
| `segment-duplicate-index`, `segment-size` | error | bad segment |
| `net-no-root`, `net-several-roots`, `attachment-edge`, `attachment-offset`, `attachment-self`, `attachment-missing-parent`, `attachment-cycle`, `net-overlap` | error | the net can't be laid out |
| `marker-duplicate-id`, `marker-id-range`, `marker-segment`, `marker-size`, `marker-outside`, `marker-overlap` | error | bad marker |
| `too-many-markers` | error | more than 50 markers |
| `segment-few-markers` | error | fewer than 3 markers on a surface |
| `segment-bunched` | warning | markers span < 35 % of the surface's diagonal |
| `marker-too-small` | warning | estimated px below the role's target (message names the size to print) |
| `grazing-surface` | warning | > 72° oblique to the standing camera: photograph it face-on |
| `shared-edge-uncovered` | warning | no marker within one photo height of a shared edge on both sides |
| `mounting-holes` | error | hole not 1/2/2.5/3/3.5 mm, head outside hole + 0.5 .. 15 mm, or a gap outside 0.5 .. 10 mm |
| `mounting-holes-tight` | warning | white border < 3 photo px (side/20 when the photo scale is unknown; names the gaps that measure clean) |
| `tip-full-frame`, `tip-corner-photos`, `tip-marker-large` | warning | capture-1 lessons |
| `tip-screw-bias` | warning | no mounting holes: screws near the black square shift detected corners |

## Sizing maths (`MarkerSizing`)

- `pxPerMm = imageLongEdgePx / (2 · distanceMm · tan(hfov/2))` (long edge horizontal).
- Foreshortening for a camera in front of the root, looking horizontally:
  `px = sizeMm · pxPerMm · min(cos overhang, cos yaw)`; beyond 72° the surface is flagged "shoot face-on"
  and sized for a face-on shot (factor 1).
- Photo footprint `2·d·tan(hfov/2)` × ¾ of that; markers are spaced ≤ half the footprint height.
- Example (The Attic, 2.5 m, phone 0.5×): 6.4 × 4.8 m per photo, 0.63 px/mm; a 125 mm marker is 79 px
  face-on but 56 px on the 45° main wall, so the generator suggests 150 mm corners (67 px) and 100 mm
  fillers (45 px) there, 100/80 mm on the vertical kickboard.
