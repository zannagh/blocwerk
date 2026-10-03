# Geometry kernel: one set of rules for C# and Python

Where a facet really is, which side of a seam it keeps, and when a line of sight counts as blocked are
implemented twice:

- **C#**: the coverage report and the 3D view.
- **Python**: the wall textures.

The far-facet occlusion bug happened on both sides. These rules are therefore written down once (here), the constants
live in one place per language, and one set of golden cases checks that both languages give the same answers.

| | C# | Python |
|---|---|---|
| Constants and shared helpers | `src/Blocwerk.Core/Geometry/GeometryKernel.cs` | `docker/wall-geometry/wallgeometry/kernel.py` |
| Facet shape (occluder region) | `Capture/Coverage/CoverageOccluderSeams.cs`, `CoverageOccluder.cs` | `wallgeometry/occlusion.py` |
| Line of sight | `CoverageOccluder.Blocks` | `occlusion.hidden` |
| Golden test runner | `test/Blocwerk.Core.Tests/GeometryGoldenTests.cs` | `docker/wall-geometry/tests/test_geometry_golden.py` |

The golden cases are in `test/geometry-golden/*.json`. Each case is a small wall-geometry document plus:

- `expect.cuts`: per facet, its seam cuts and fold clips as unit half-planes `[alpha, beta, gamma]`. A point is kept
  where `alpha·a + beta·b ≥ gamma`.
- `queries`, of four kinds:
  - `inShape`: a point is in a facet's shape, optionally with an inset.
  - `blockedBy`: one facet blocks a camera's line of sight to a target.
  - `blocked`: any other facet blocks it.
  - `visible`: the camera faces the target and nothing blocks it.

There are ten walls: a flat wall, a triangle side wall, the same triangle with a stray marker, coplanar facets that
overlap, a 7° shallow fold, an arete, an overhang with a lip, a volume built from facets, a far facet behind the wall,
and an SfM model without markers.

CI runs both runners:

- `main.yml` runs `dotnet test`. The test project links the JSON files into `GeometryGolden/`.
- `wall-geometry.yml` runs the Dockerfile's `test` stage. That stage copies `test/geometry-golden/` to
  `tests/geometry-golden/`, and `Dockerfile.dockerignore` lets the folder into the build context. The workflow also
  triggers on changes under `test/geometry-golden/**`.

To change a rule, change the doc, both constant files and the golden cases together.

## Rules

### 1. Facet shape

A facet's shape is its region rectangle, cut by two kinds of half-planes:

- **Seam cuts.** The facet is cut along the seam with each neighbour where all of the facet's *voting* markers lie on
  one side (rule 3), and only when the cut removes some of the rectangle.
- **Fold clips.** These come from `outlineMm` (rule 7).

A facet blocks lines of sight only within this shape, never with its infinite plane. The coverage report also rates
cells only inside it.

### 2. Seams

The seam between facet g and facet h is the line in g's plane where it meets h's plane.

- **Angle threshold.** If the planes are closer to parallel than `MinPlaneAngleSin` = 0.17 (about 9.8°), there is no
  reliable seam line and no cut.
- **The neighbour must really be there.** A seam is used only when the seam line's part inside g's rectangle runs, on
  average, within `MaxSeamGapMm` = 800 of h's rectangle. This is measured with 11 samples, in h's plane.
- **Marker tolerance.** A marker corner within `MarkerSideTolMm` = 20 of the seam counts as being on either side of it.

### 3. Voting markers

Only markers whose centre lies inside the facet's `extentMm`, grown by `MarkerVoteMarginMm` = 50, take part in two
decisions:

- the seam-side test (rule 1);
- the marker centroid used by the 3D view's `TriangleKeptSide` and `FacetSeamTrim`.

A marker that the solver left out of the extent is a stray stuck on a coplanar neighbour (`extents.py`; The Attic's
marker 39). It stays a member of its facet for the solve, but it says nothing about the facet's shape. A facet without
an extent lets all of its markers vote.

### 4. Line of sight

A camera C's view of a target T is blocked by an occluder when all of these hold:

- The segment from T to C crosses the occluder's plane.
- T lies more than `NearPlaneMm` = 30 off that plane. A target this close to the plane is on the occluder's seam.
- C lies more than `OnPlaneMm` = 1e-6 off that plane. A camera on the plane crosses nothing.
- The crossing point lies in the occluder's shape (rule 1), shrunk by the consumer's inset (see "Consumer
  policies").

The target's own facet never blocks it.

### 5. Facing

A view counts only from the front of the surface: `cos(ray, outward normal) > MinFacingCos` = 0.05, which is about 87°.

### 6. Shallow folds

When two facets' normals are between `mergeDeg` (5°) and the seam threshold (about 9.8°) apart, rule 2 gives them no
seam. If their extents overlap in-plane, the solver clips both at the midline between their marker clusters, as it
already does for coplanar facets (`overlaps.py`). It does so only when the planes really meet there: both ends of the
cut must lie within `mergeMm` of the other plane. Otherwise the planes form a step, and nothing is clipped.

Together, the coplanar clip, the shallow-fold clip and the seam cut leave no gap in angle between them.

### 7. SfM outlines

A model solved from photo features has no markers. Its facet extents are the bounding box of the 1–99 % inlier box after
the fold clip.

- **Export.** When a fold clip cut a corner off, `sfm/world.py` exports the clipped convex polygon as the facet's
  `outlineMm`.
- **Occlusion and coverage.** Both turn the outline's edges into half-planes, skipping edges that lie along the
  extent's sides (`OutlineEdgeTolMm` = 1).
- **3D view and ray casts.** Facets that have no plan-triangle outline use `outlineMm` as their outline
  (`Wall3DFacetOutlines.Apply`, `FacetShapes`).

## Consumer policies (deliberately not shared)

These choices depend on what each consumer knows or needs:

| Policy | Coverage (C#) | Textures (Python) | Why |
|---|---|---|---|
| Region | Extent, widened by the placed holds' bounds + 50. Without an extent: the markers' bounds + 100 | Extent | Only C# knows the holds and their model frame. |
| Inset of the shape for blocking | `EdgeMarginMm` = 60 | 0 | Coverage regions overreach by the hold margins, and coverage errs towards "seen". Textures must not paint an occluder. |
| View acceptance (besides facing) | In frame with a 1 % image-width margin; camera depth > 1e-6 | 16 photo-px margin (`imageMarginPx`); depth > 1e-3 | Texture sampling needs interpolation room. |
| Volumes as occluders | Ray-marched (`CoverageScene`) | None: the document has no volumes | Volumes live in the C# database. |
| 3D outline | Plan triangle, right-triangle completion, seam trim | — | A drawing, derived from the plan; the occluder shape stays the kernel's rule 1. |
| Ray-cast surfaces | 3D outline + 20 mm (proposals), + 150 mm (scale taps) | Texture grid: extent + 100 mm | Each consumer sets its own tolerance. |

## The review's 14 divergences

These are the divergences listed in `.me/review/geometry-textures.md`. For each one: the decision, and which side
changed.

1. **Occluder region.** Kept as a consumer policy (see the table above). The kernel takes the region as input.
2. **Edge treatment.** Kept as a consumer policy, but it is now an explicit `inset` parameter in both kernels:
   `CoverageOccluder.Blocks(.., inset)` and `occlusion.hidden(.., inset=)`. Python's `contains(pad=)`, which grew the
   shape, became `inset=`, which shrinks it, as in C#.
3. **Near-target exclusion.** Unified on Python's rule: the target is within 30 mm of the occluder's *plane*. C#
   changed: its rule was "crossing within 80 mm along the ray". The along-ray rule let a nearly coplanar neighbour 13 mm
   off the plane hide the wall for grazing rays, and it ignored real blocks right at inside folds.
4. **Camera on the occluder plane.** Unified on Python's guard. C# changed.
5. **Volumes.** No change: C# only (consumer policy).
6. **View acceptance.** The facing threshold is unified at cos > 0.05; C# changed from 0.035. Image margins and depth
   stay consumer policy.
7. **Lens model.** Forward projection was already unified in 3e3e0ab (textures use p1, p2 and skew). The inverse
   iteration counts differ, but every one converges, and inverse projection is not part of the kernel. No change.
8. **Fold guard.** Equivalent in practice. No change.
9. **Triangle kept side.** The occluder rule (rule 1) is the kernel's shape on both sides. The 3D view's plan-driven
   outline is a drawing (consumer policy). It now uses the same voting markers (rule 3) and constants.
   `CoverageOccluderSeams` and `occlusion.py` no longer claim to be "the same rule as Wall3DFacetOutlines".
10. **Seam-gap measurement.** The occluder version (rule 2) is the kernel. The 3D view's and `FacetSeamTrim`'s gap
    measurements stay, as drawing policy.
11. **Coplanarity thresholds.** The two ranges are now contiguous (rule 6; Python solver changed). C#'s volume-seam
    `SeamMaxAngleDeg` = 10 overlaps the seam cut by about 0.2°. That is harmless, so it was left alone.
12. **Ray-cast surfaces.** Consumer policy. No change.
13. **Seam harmonisation bands.** Python changed: `seams.py` measures texture pixels against the facet's shape (rule 1).
    The cut-away half no longer feeds or forms a seam band.
14. **Stray markers.** Unified as rule 3. Both sides changed: occlusion, coverage, `TriangleKeptSide` and
    `FacetSeamTrim`.

## Findings 3, 10 and 11

- **F3 (SfM: no seam cuts, and the extent undid the fold clip).** Fixed by rule 7. The fix applies to SfM models solved
  (or re-exported) after this change. Older SfM documents have no `outlineMm` and keep the old behaviour until they are
  re-solved.
- **F10 (planes 5–10° apart are neither clipped nor cut).** Fixed by rule 6 in the solver. It applies to marker models
  solved after this change. On existing models, such pairs keep their overlap until a re-solve.
- **F11 (stray markers decide seam sides).** Fixed by rule 3 in both languages. It applies to existing documents
  immediately.

## What users see

### On The Attic's active model (`test/Blocwerk.Core.Tests/View3D/attic-active-model.json`)

- **Occluder regions.** Every facet's seam cuts are identical before and after. In this model, marker 39 already sits
  on the main wall (the solver's nearest-host fix), so every marker votes.
- **No shallow folds.** No facet pair is 5–9.8° apart, so a re-solve clips nothing new.
- **Coverage.** A simulation with 29 synthetic cameras over 814 cells, using the old and new C# blocking and facing
  rules, gave these results:
  - No cell changed status.
  - About 20 % of cells gained or lost one or two counted views. These are views at 87–88° that no longer count, and
    grazing lines of sight past the 13 mm-off "leftover bit" that are no longer counted as blocked.
  - Coverage percentages can move only where a cell sits at a threshold.
- **Textures.**
  - Photo choice is unchanged. Python's rules were already the unified ones, apart from stray voting and SfM outlines.
  - Seam harmonisation leaves out pixels beyond a facet's seam cut. Colour corrections within about 100 mm of folds
    may differ slightly.
- **3D view.** Unchanged. No stray marker moves a centroid, and the model has no `outlineMm`.

### On other walls

- **Walls with a stray marker on the far side of a seam.** The triangle cut comes back. Its empty half no longer
  hides walls in the textures or in coverage, and its cells are no longer rated.
- **SfM walls with triangle panels, once re-solved.** The same effect. Their 3D outline also becomes the clipped
  polygon.
- **Marker walls with folds of 5–9.8°, once re-solved.** The overlap is clipped at the midline. There is no more
  z-fighting, and coverage no longer counts the overlap twice. The clip is recorded in `quality.checks.overlapClipped`.
