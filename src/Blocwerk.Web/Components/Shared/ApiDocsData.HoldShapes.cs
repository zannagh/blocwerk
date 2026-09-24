// <copyright file="ApiDocsData.HoldShapes.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Web.Components.Shared;

/// <summary>The reference for the hold-shape actions (WallHoldShapesController).</summary>
internal static partial class ApiDocsData
{
    private const string HoldShapesBase = "/api/walls/{wallId}/holds";

    private const string HoldShapesIntro =
        "The hold-shape buttons of the wall settings. The outline upgrade turns circle holds into outlines traced on "
        + "the current panel photos: it changes panel hold shapes, so preview it first; every run can be reverted. "
        + "Positions, radii and boulders are never changed. \"Refine 3D hold shapes\" recomputes the holds' contact "
        + "footprints on the active 3D model (derived data only). Same keys as the capture API.";

    private const string OutlineBody = "{\n  \"includeManual\": false\n}";

    private static ApiParamDoc[] OutlineRun =>
    [
        new("wallId", "path", "The wall."),
        new("runId", "path", "The outline upgrade run (runId of the apply, or latestRun.id of the GET)."),
    ];

    private static ApiSurfaceDoc WallHoldShapesSurface => new(
        "Wall holds: outlines and 3D shapes",
        HoldShapesIntro,
        "Wall key or personal key with write access (admin owner)",
        HoldShapesEndpoints());

    private static ApiEndpointDoc[] HoldShapesEndpoints() =>
    [
        new("GET", HoldShapesBase + "/outline-upgrade", "Whether the outline upgrade is available, and the latest run.", CaptureWall, null,
            "{\n  \"enabled\": true,\n  \"latestRun\": { \"id\": \"<guid>\", \"outlined\": 412, \"revertedAt\": null }\n}"),
        new("POST", HoldShapesBase + "/outline-upgrade/preview", "What an upgrade would do; writes nothing.", CaptureWall, OutlineBody,
            "{\n  \"photos\": 12,\n  \"eligible\": 540,\n  \"wouldOutline\": 412,\n  \"wouldKeepCircle\": 128\n}"),
        new("POST", HoldShapesBase + "/outline-upgrade", "Writes the outlines and records the run.", CaptureWall, OutlineBody,
            "{\n  \"runId\": \"<guid>\",\n  \"outlined\": 412\n}", "Idempotent: holds that already have an outline are never touched."),
        new("POST", HoldShapesBase + "/outline-upgrade/{runId}/revert", "Reverts a run; holds edited since are left alone.", OutlineRun, null,
            "{\n  \"reverted\": 412,\n  \"skippedEdited\": [ ],\n  \"missing\": 0\n}"),
        new("POST", HoldShapesBase + "/refine-shapes", "Refines the 3D hold shapes from the capture photos.", CaptureWall, null,
            "{\n  \"multiView\": 653,\n  \"singleView\": 12,\n  \"skipped\": 3,\n  \"capturePhotos\": 24\n}",
            "409 when the wall has no active 3D model or outline detection is off."),
    ];
}
