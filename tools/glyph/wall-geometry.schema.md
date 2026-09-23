# `wall-geometry.json` — contract between the glyph solver and the app (v1)

Produced by the solver in `docker/wall-geometry/wallgeometry/` (the `wall-geometry` service, job kind
`solve`; `tools/glyph/geometry/solve.py` is its CLI). The service adds extra fields, documented in
`docker/wall-geometry/README.md`. Consumed by the app (C#) to map any photo
that sees markers onto the wall in millimetres. **Additive and experimental** — nothing in the existing
normalized 0..1 pipeline is reinterpreted.

## Frames and units

- All lengths **mm**. Angles **degrees**.
- **World frame**: `z` = up (opposite gravity). `x` = along the main wall's (segment 0) bottom edge,
  pointing right as you face the wall. `y = z × x` (points into/away from the wall, right-handed).
  Origin: segment 0's bottom-left marker corner projected onto the floor-parallel plane through it
  (solver may choose another fixed point — it must say which in `world.origin`).
- **Facet (plane) frame**: every planar piece of a segment is a *facet*. A facet has origin `O`, unit
  vectors `u` (across the surface, as horizontal as the facet allows, pointing right) and `v`
  (up the surface), normal `n = u × v` pointing **out of the wall, toward the climber**.
  A point with plane coords `(a, b)` sits at `O + a·u + b·v`.
- **Marker corners** are always listed in ArUco order **TL, TR, BR, BL** of the marker's own printed
  orientation (the order `detectMarkers` returns).

## Shape

```jsonc
{
  "version": 1,
  "units": "mm",
  "dictionary": "DICT_4X4_50",
  "idScheme": "segment*6+role",           // roles 0=TL 1=TR 2=BR 3=BL 4=H 5=V; or "plan" (ids carry no meaning)
  "markerSizeMm": 125.0,                    // black-square side (the most common one with a plan)
  "markerSizeOverridesMm": { "40": 80 },    // optional: markers printed at another size
  "markerSegments": { "44": 0 },            // "plan" scheme: the marker plan's segment per id (echo of the request)
  "world": { "origin": "text description", "up": [0,0,1] },
  "segments": [
    {
      "index": 0,                           // == markerId / 6, or the marker plan's segment index
      "name": "main wall",
      "declaredAngleDeg": 45.0,             // tilt from vertical, as the admin states it (null if none): + overhang, − slab
      "measuredAngleDeg": 44.6,             // from the solve; for multi-facet segments, per facet below
      "facets": [
        {
          "id": "0",                        // "0", or "5a"/"5b" when a segment folds
          "origin": [x, y, z],
          "u": [..], "v": [..], "normal": [..],
          "measuredAngleDeg": 44.6,         // angle between normal and horizontal plane, as tilt-from-vertical (+ overhang, − slab)
          "yawDeg": 0.0,                    // rotation of the facet about z relative to segment 0
          "extentMm": { "aMin": .., "aMax": .., "bMin": .., "bMax": .. }  // bbox of its markers + margin; NOT an outline
        }
      ]
    }
  ],
  "markers": [
    {
      "id": 0, "segment": 0, "role": "TL", "facet": "0",   // role null for "plan" ids
      "sizeMm": 125.0,                       // this marker's printed size
      "cornersPlaneMm": [[a,b],[a,b],[a,b],[a,b]],   // TL,TR,BR,BL in the facet frame
      "cornersWorldMm": [[x,y,z], ...],
      "observations": 2,                     // photos it was solved from
      "reprojRmsPx": 0.8,
      "synthetic": false                     // true if any corner came from reconstruction (e.g. id 1)
    }
  ],
  "cameras": [                               // optional; for 3D view / splatting, not needed at runtime
    { "image": "IMG_2770", "K": [9], "dist": [..], "R": [9], "t": [3], "reprojRmsPx": 0.9 }
  ],
  "quality": {
    "reprojRmsPx": 0.9,
    "gravity": "from seg1 x seg2 normals",
    "checks": { "seg0DeclaredVsMeasuredDeg": 0.4, "markerSideRmsErrMm": 0.6 }
  }
}
```

## Marker plans (`idScheme: "plan"`)

When the wall has a marker plan (`marker-plan.schema.md`) the app sends the solve request with
`idScheme: "plan"`, every planned id's segment in `markerSegments` (any DICT_4X4_50 id 0..49), per-marker
sizes in `markerSizeOverridesMm`, and one declared segment per planned surface (angle = the plan's
overhang, `verticalReference` = |overhang| < 2°, the same tolerance under which the app calls a surface "vertical"). The solver then starts every marker on its PLANNED
segment instead of `id // 6`; the facet split/move logic is unchanged, so a marker glued to another
surface than planned is still moved to the facet its normal fits (and the app reports it). Requests
without these fields are read exactly as before.

## How the app uses it

For a photo that sees ≥1 marker of facet `F`: pair each detected corner (pixels) with its
`cornersPlaneMm`, fit a homography `H_F` (RANSAC when ≥2 markers). Then any pixel on facet `F` maps to
plane mm and back — metric hold sizes, rectified per-facet images, and exact photo-to-photo alignment
all fall out of `H_F`. With **one** marker only, `H_F` is still exact at that marker and degrades with
distance from it; consumers must report which case they are in.

Markers mark the **plane**, not the corner: never use marker positions as a segment's outline.
