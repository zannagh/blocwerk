// <copyright file="ApiDocsData.Markers.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Web.Components.Shared;

/// <summary>The reference for preparing a marker wall (WallMarkersController, WallMarkerPlanRevisionsController).</summary>
internal static partial class ApiDocsData
{
    private const string MarkersBase = "/api/walls/{wallId}";

    private const string MarkersIntro =
        "Prepares a wall for marker captures, as the marker settings and the marker planner do: switch the printed markers on or off "
        + "and set their size, upload the marker plan (a new revision), and record when a revision's markers went up on the wall. A "
        + "Wall-scoped key for the wall or a personal key, created with write access, whose owner is an admin of the wall. Every write "
        + "is recorded in the change journal.";

    private const string MarkersStateJson =
        "{\n  \"enabled\": true,\n  \"markerSizeMm\": 125,\n  \"currentRevision\": 3,\n  \"onWallRevision\": 2,\n  \"revisions\": [\n    "
        + "{ \"revision\": 3, \"createdAt\": \"2026-10-02T09:00:00+00:00\", \"markers\": 48, \"isCurrent\": true, \"usedByActiveModel\": false,"
        + "\n      \"effectiveFrom\": null, \"isOnWall\": false }\n  ]\n}";

    private const string MarkerPlanSaveJson =
        "{\n  \"saved\": true,\n  \"revision\": 3,\n  \"unchanged\": false,\n  \"issues\": [ { \"severity\": 1, \"code\": \"…\", "
        + "\"message\": \"…\", \"segment\": 0, \"markerId\": 12 } ]\n}";

    private static ApiParamDoc[] MarkersWall => [new("wallId", "path", "The wall.")];

    private static ApiSurfaceDoc WallMarkersSurface => new(
        "Wall preparation: markers",
        MarkersIntro,
        "Wall or personal key with write access (admin owner)",
        MarkersEndpoints());

    private static ApiEndpointDoc[] MarkersEndpoints() =>
    [
        new("GET", MarkersBase + "/markers", "Markers on or off, their size, and the plan's revisions.", MarkersWall, null, MarkersStateJson),
        new("PUT", MarkersBase + "/markers", "Switches the markers on or off and sets their size.", MarkersWall,
            "{\n  \"enabled\": true,\n  \"markerSizeMm\": 125\n}", "{\n  \"enabled\": true,\n  \"markerSizeMm\": 125\n}",
            "markerSizeMm is the black square's side in mm, (0, 1000]; omit it to keep the stored size. 400 outside that range."),
        new("GET", MarkersBase + "/marker-plan", "The current marker plan (marker-plan.json), or 404.", MarkersWall, null,
            "{ … marker-plan.json … }"),
        new("PUT", MarkersBase + "/marker-plan", "Saves a marker-plan.json as the wall's next plan revision.", MarkersWall,
            "{ … marker-plan.json … }", MarkerPlanSaveJson,
            "At most 2 MB (413). 409 with unchanged: true when it is identical to the current revision (none is added). "
                + "422 with the issues when it cannot be read or has errors."),
        new("GET", MarkersBase + "/marker-plan/revisions", "The plan's revisions, newest first.", MarkersWall, null,
            "[ … same shape as revisions above … ]"),
        new("PUT", MarkersBase + "/marker-plan/revisions/{revision}/effective", "Records that the revision's markers are on the wall.",
            [new("wallId", "path", "The wall."), new("revision", "path", "The plan revision.")],
            "{\n  \"effectiveFrom\": \"2026-10-03T08:00:00+00:00\",\n  \"clear\": false\n}", null,
            "204. effectiveFrom defaults to now; clear: true marks it as planned again. 404 for an unknown revision."),
    ];
}
