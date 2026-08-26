"""Reading a finished pipeline run into the job result.

The fixture is a real `manifest.json` from a 46-frame run, trimmed only in the frame
list and the matrices. Nothing here runs the pipeline: the point is that the *contract*
between the manifest and the wire result holds, so a change to either side is caught
without a two-minute stitch.
"""
from __future__ import annotations

import json
import os
import sys

import pytest
from PIL import Image

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

from app import results  # noqa: E402
from app.models import JobResult  # noqa: E402

HERE = os.path.dirname(os.path.abspath(__file__))
PHOTOS = ["/in/2526.jpg", "/in/2527.jpg", "/in/2528.jpg"]


@pytest.fixture()
def manifest() -> dict:
    with open(os.path.join(HERE, "manifest-sample.json"), "r", encoding="utf-8") as handle:
        return json.load(handle)


@pytest.fixture()
def work_dir(tmp_path, manifest):
    """A work directory holding every artifact the sample manifest names."""
    work = tmp_path / "work"
    work.mkdir()
    artifacts = manifest["artifacts"]
    for key in ("flat_base", "natural_default"):
        Image.new("RGB", (40, 26), (30, 60, 90)).save(work / artifacts[key])
    for name in artifacts["naturals"]:
        Image.new("RGB", (36, 26), (30, 60, 90)).save(work / name)
    (work / artifacts["cameras"]).write_text('{"schema": "blocwerk.wall-cameras/1"}')
    (work / artifacts["holds_flat"]).write_text(json.dumps({"holds": [
        {"id": 0, "x": 0.5, "y": 0.4, "w": 0.02, "h": 0.03, "confidence": 0.81}]}))
    (work / artifacts["holds_natural"]).write_text(json.dumps({"holds": []}))
    return work


def assemble(manifest, work_dir, tmp_path, settings):
    publisher = results.Publisher(str(work_dir), str(tmp_path / "artifacts"), settings)
    return results.assemble(manifest, publisher, PHOTOS)


def test_the_result_validates_against_the_wire_model(manifest, work_dir, tmp_path, settings):
    # The .NET client is generated from these names; a manifest key the models do not
    # cover must fail here rather than silently vanish from the response.
    result = JobResult.model_validate(assemble(manifest, work_dir, tmp_path, settings))
    assert result.flat_master.artifact == "flat.jpg"
    assert result.natural_master.artifact == "natural.jpg"
    assert result.cameras_json == "cameras.json"
    assert result.wall_width_m == pytest.approx(5.5)
    assert result.coordinate_convention.startswith("Hold geometry is normalised")


def test_every_named_artifact_is_published_under_its_served_name(
        manifest, work_dir, tmp_path, settings):
    assemble(manifest, work_dir, tmp_path, settings)
    served = set(os.listdir(tmp_path / "artifacts"))
    assert {"flat.jpg", "natural.jpg", "cameras.json",
            "display-flat.jpg", "display-natural.jpg"} <= served
    assert {f"natural-{n}.jpg" for n in ("gentle", "medium", "strong")} <= served


def test_the_curvatures_are_ordered_and_the_natural_is_narrower_than_the_flat(
        manifest, work_dir, tmp_path, settings):
    result = assemble(manifest, work_dir, tmp_path, settings)
    curvature = result["curvature"]
    assert curvature["default"] == "gentle"
    assert [c["name"] for c in curvature["curves"]] == ["gentle", "medium", "strong"]
    # The cylinder normalises on the centre derivative, so the ends foreshorten: every
    # emitted natural is narrower than the flat base, and the stronger the curve the
    # narrower it gets. A natural at least as wide means the anamorphic widening is back.
    flat_width = manifest["flat_base"]["width"]
    widths = [c["width"] for c in curvature["curves"]]
    assert all(w < flat_width for w in widths)
    assert widths == sorted(widths, reverse=True)


def test_detections_become_centre_and_radius(manifest, work_dir, tmp_path, settings):
    result = assemble(manifest, work_dir, tmp_path, settings)
    assert result["holds"] == [{"id": "0", "x": 0.5, "y": 0.4, "radius": 0.015,
                                "confidence": 0.81}]
    assert result["holdsNatural"] == []


def test_a_missing_master_is_reported_rather_than_half_published(
        manifest, work_dir, tmp_path, settings):
    os.remove(work_dir / manifest["artifacts"]["flat_base"])
    assert assemble(manifest, work_dir, tmp_path, settings) is None


def test_diagnostics_survive_a_manifest_that_measured_no_verticals(
        manifest, work_dir, tmp_path, settings):
    # On a horizontally-planked wall the vertical line family can be empty, and the
    # pipeline writes null rather than a number. That must not blow up the response.
    manifest["plane"]["straightness"]["vertical_rms_deg"] = None
    diagnostics = assemble(manifest, work_dir, tmp_path, settings)["diagnostics"]
    assert diagnostics["straightness"] == 0.0
    assert diagnostics["referenceFrame"] == "2534.jpg"
    assert diagnostics["elapsedSeconds"] > 0


def test_a_photo_the_pipeline_could_not_chain_is_reported_as_rejected(
        manifest, work_dir, tmp_path, settings):
    manifest["inputs"]["frames"] = ["2526.jpg", "2527.jpg"]
    diagnostics = assemble(manifest, work_dir, tmp_path, settings)["diagnostics"]
    assert [r["name"] for r in diagnostics["imagesRejected"]] == ["2528.jpg"]


def test_a_sweep_with_no_square_on_photo_becomes_a_coverage_warning(
        manifest, work_dir, tmp_path, settings):
    # The composite is still recognisable at this point, so it must not fail the job -
    # but it is visibly smeared and only a reshoot fixes it, so the operator is told.
    manifest["registration"]["reference_area_stretch"] = 9.4
    warnings = assemble(manifest, work_dir, tmp_path, settings)["diagnostics"]["coverageWarnings"]
    assert any("squarely" in w for w in warnings)


def test_a_square_on_sweep_produces_no_stretch_warning(
        manifest, work_dir, tmp_path, settings):
    manifest["registration"]["reference_area_stretch"] = 2.39
    warnings = assemble(manifest, work_dir, tmp_path, settings)["diagnostics"]["coverageWarnings"]
    assert not any("squarely" in w for w in warnings)


def test_a_flat_projection_emits_a_view_with_no_curvature_numbers(
        manifest, work_dir, tmp_path, settings):
    # The display projection is not settled, so a projection that has no theta/k/radius
    # must still round-trip: the curvature fields are optional, not zero.
    from PIL import Image as _Image
    _Image.new("RGB", (40, 26), (30, 60, 90)).save(work_dir / "natural-flat.jpg")
    manifest["curvature"] = {"projection": "flat", "default": "flat", "emitted": {
        "flat": {"image": "natural-flat.jpg", "width": 40, "height": 26,
                 "projection": "flat"}}}
    manifest["artifacts"]["natural_default"] = "natural-flat.jpg"
    manifest["artifacts"]["naturals"] = ["natural-flat.jpg"]

    result = JobResult.model_validate(assemble(manifest, work_dir, tmp_path, settings))
    assert result.curvature.projection == "flat"
    assert [c.name for c in result.curvature.curves] == ["flat"]
    assert result.curvature.curves[0].theta_max_deg is None
    assert result.curvature.curves[0].radius_m is None


def test_a_gap_in_the_sweep_becomes_a_coverage_warning(
        manifest, work_dir, tmp_path, settings):
    flat = manifest["flat_base"]
    manifest["plane"]["finish"]["inpainted_px"] = int(flat["width"] * flat["height"] * 0.05)
    warnings = assemble(manifest, work_dir, tmp_path, settings)["diagnostics"]["coverageWarnings"]
    assert any("gap" in w for w in warnings)


def test_carryover_is_absent_when_the_pipeline_skipped_it(
        manifest, work_dir, tmp_path, settings):
    assert "carryover" not in manifest["artifacts"]
    assert assemble(manifest, work_dir, tmp_path, settings)["carryover"] is None


def test_carryover_is_split_into_carried_missing_and_new(
        manifest, work_dir, tmp_path, settings):
    manifest["artifacts"]["carryover"] = "carryover.json"
    manifest["carryover"] = {"generation": 4}
    (work_dir / "carryover.json").write_text(json.dumps({
        "_counts": {"CARRIED_OVER": 1, "MISSING": 1, "NEW": 1},
        "_quality": {"estimated_precision": 0.86},
        "_blocker": "DO NOT APPLY UNATTENDED.",
        "holds": [
            {"Id": "a", "classification": "CARRIED_OVER", "BoulderLinkCount": 2,
             "matched_detection_id": 7, "match_distance_px": 31.5, "colour_agrees": True,
             "in_frame": True, "reason": "",
             "new": {"X": 0.4, "Y": 0.6, "Radius": 0.01,
                     "ShapePoints": [{"Dx": 0.01, "Dy": -0.02}]}},
            {"Id": "b", "classification": "MISSING", "BoulderLinkCount": 0,
             "matched_detection_id": None, "match_distance_px": None,
             "in_frame": True, "reason": "likely removed",
             "new": {"X": 0.7, "Y": 0.2, "Radius": 0.01}},
        ],
        "new_detections": [
            {"detection_id": 9, "confidence": 0.77, "likely_duplicate_of_carried_hold": False,
             "new": {"X": 0.1, "Y": 0.1, "Radius": 0.02}}],
    }))
    carryover = assemble(manifest, work_dir, tmp_path, settings)["carryover"]

    assert carryover["generation"] == 4
    assert [h["id"] for h in carryover["carried"]] == ["a"]
    assert carryover["carried"][0]["matchedDetectionId"] == "7"
    assert carryover["carried"][0]["shapePoints"] == [{"dx": 0.01, "dy": -0.02}]
    assert [h["id"] for h in carryover["missing"]] == ["b"]
    # A MISSING hold still carries its transferred position, so the app can show the
    # operator where the hold used to be rather than just dropping it.
    assert carryover["missing"][0]["x"] == pytest.approx(0.7)
    assert [h["detectionId"] for h in carryover["new"]] == ["9"]
    # The pipeline's caveat must reach the app: it is what stops an unattended apply.
    assert carryover["blocker"]
