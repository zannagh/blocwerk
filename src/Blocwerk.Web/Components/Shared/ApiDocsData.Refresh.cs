// <copyright file="ApiDocsData.Refresh.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Web.Components.Shared;

/// <summary>The reference for "Update panels + 3D" over the API (WallRefreshApiController).</summary>
internal static partial class ApiDocsData
{
    private const string RefreshBase = "/api/walls/{wallId}/refresh";

    private const string RefreshIntro =
        "Runs \"Update panels + 3D\" from a script, exactly as the page does: open a run, drop the photos and videos, sort, start "
        + "with the proposed photos (the quick review's defaults), read the confirm screen's summary, then apply with that summary's "
        + "decisionsVersion. Nothing goes live before Apply, and Apply promotes only what that version describes. Reads have no side "
        + "effects. A personal key with "
        + "write access whose owner is an admin of the wall; wall, kiosk and installation keys get 403. Every write is recorded in the "
        + "change journal (written before the write, removed again when it is refused).";

    private const string RefreshViewJson =
        "{\n  \"id\": \"<guid>\",\n  \"wallId\": \"<guid>\",\n  \"status\": 4,\n  \"photos\": [ { \"photoId\": \"<guid>\", \"index\": 0, "
        + "\"fileName\": \"IMG_0001.jpg\" } ],\n  \"videos\": [],\n  \"picks\": [ { \"col\": 0, \"row\": 0, \"photoId\": \"<guid>\", "
        + "\"confidence\": 2 } ],\n  \"steps\": [ { \"key\": \"match\", \"title\": \"…\", \"state\": 2, \"detail\": \"…\" } ],\n  "
        + "\"summary\": { … },\n  \"error\": null,\n  \"check3DPending\": false\n}";

    private const string RefreshStatusNote =
        "status: 0 uploading, 1 sorting, 2 ready to start, 3 running, 4 ready to apply, 5 applying, 6 done, 7 failed, 8 discarded.";

    private const string RefreshSummaryJson =
        "{\n  \"refreshId\": \"<guid>\",\n  \"status\": 4,\n  \"canApply\": true,\n  \"decisionsVersion\": \"9F2C41A07B3D5E61C8A4F0B2\",\n  "
        + "\"summary\": {\n    \"refound\": 812, \"keptInPlace\": 31, \"possiblyMoved\": 2, \"newHolds\": 14, \"droppedDetections\": 6,\n    "
        + "\"removed\": 0, \"overlapLinks\": 40, \"overlapsLeftOut\": 3, \"bouldersOnKeptHolds\": 5,\n    \"panels\": [ \"Centre panel\" ], "
        + "\"decisionsVersion\": \"9F2C41A07B3D5E61C8A4F0B2\"\n  },\n  \"picks\": [ … ],\n  \"steps\": [ … ],\n  \"error\": null,\n  "
        + "\"check3DPending\": false\n}";

    private const string RefreshApplyNote =
        "202 {\"refreshId\", \"decisionsVersion\"} when queued; poll GET /{refreshId} until status 6. 400 without decisionsVersion. "
        + "409 {\"error\", \"status\", \"currentDecisionsVersion\"} when the summary changed since (check it again), there is nothing to "
        + "apply, or the 3D check is running. If the decisions change between the call and the background apply, nothing is promoted and "
        + "the run is back at status 4 with a new decisionsVersion and an error saying why.";

    private static ApiParamDoc[] RefreshWall => [new("wallId", "path", "The wall.")];

    private static ApiParamDoc[] RefreshOnWall =>
    [
        new("wallId", "path", "The wall."),
        new("refreshId", "path", "The run (its id). Only the wall's current run is served."),
    ];

    private static ApiSurfaceDoc WallRefreshSurface => new(
        "Wall update: panels + 3D",
        RefreshIntro,
        "Personal key with write access (admin owner)",
        RefreshEndpoints());

    private static ApiEndpointDoc[] RefreshEndpoints() =>
    [
        new("POST", RefreshBase, "Opens a run, or returns the open one.", RefreshWall, null, RefreshViewJson, RefreshStatusNote),
        new("GET", RefreshBase, "The wall's current run, or 404.", RefreshWall, null, RefreshViewJson),
        new("POST", "/api/refreshes/{refreshId}/files?name={fileName}", "Adds one photo (HEIC is fine) or video: the file as the raw body.",
            [new("refreshId", "path", "The run."), new("name", "query", "The file name; .mov/.mp4 make it a video.")], "<file bytes>",
            "{\n  \"photoId\": \"<guid>\",\n  \"fileName\": \"IMG_0001.jpg\",\n  \"isVideo\": false,\n  \"problem\": null\n}",
            "A refused file answers 200 with its reason in problem, so the next file still goes up."),
        new("POST", RefreshBase + "/{refreshId}/sort", "Done uploading: the photos are sorted to the panels.", RefreshOnWall, null, null,
            "202; poll until status 2."),
        new("POST", RefreshBase + "/{refreshId}/start", "Accepts the quick defaults: starts with the proposed photos.", RefreshOnWall,
            "{\n  \"choices\": [ { \"col\": 0, \"row\": 0, \"photoId\": \"<guid>\" } ]\n}", null,
            "202; poll until status 4. The body is optional; choices override the proposal per panel (photoId null keeps the current photo)."),
        new("GET", RefreshBase + "/{refreshId}/summary", "The confirm screen (summary only, changes nothing).", RefreshOnWall, null,
            RefreshSummaryJson, "canApply is true when Apply would be taken now. Send decisionsVersion to Apply. Reads never start anything."),
        new("POST", RefreshBase + "/{refreshId}/recheck", "Starts the check against this visit's new 3D model when it is due.",
            RefreshOnWall, null, "{\n  \"pending\": true\n}",
            "202. While check3DPending is true Apply waits; poll the summary until canApply."),
        new("POST", RefreshBase + "/{refreshId}/apply", "Applies exactly the summary with this version.", RefreshOnWall,
            "{\n  \"decisionsVersion\": \"9F2C41A07B3D5E61C8A4F0B2\"\n}", null, RefreshApplyNote),
        new("GET", RefreshBase + "/{refreshId}", "The run, as the page shows it.", RefreshOnWall, null, RefreshViewJson, RefreshStatusNote),
        new("DELETE", RefreshBase + "/{refreshId}", "Discards the run and its prepared panel update.", RefreshOnWall, null, null,
            "204; 409 while a step is running."),
    ];
}
