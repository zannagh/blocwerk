// <copyright file="ApiDocsData.Placement.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Web.Components.Shared;

/// <summary>The reference for "place existing holds on the 3D model" (WallGeometryPlacementController).</summary>
internal static partial class ApiDocsData
{
    private const string PlacementBase = "/api/walls/{wallId}/geometry/place-holds";

    private const string PlacementIntro =
        "Places a wall's existing holds on its active 3D model: every live panel photo is registered onto the "
        + "model's flattened facet textures by feature matching, and each hold gets its facet, plane position and "
        + "millimetre size. For walls whose photos were taken before the printed markers went up. Hold positions "
        + "in the photos, outlines, panels and boulders are never changed; holds placed by markers or by an edit "
        + "are left alone, holds this action placed before are placed again. A Wall-scoped key for the wall, or a "
        + "personal key with write access, whose owner is an admin of the wall (a member's key gets 403, kiosk "
        + "tablets are refused).";

    private const string PlacementResultJson =
        "{\n  \"runId\": \"<guid>\",\n  \"placed\": 861,\n  \"skipped\": 0,\n  \"failed\": 19,\n  \"panels\": [\n    {"
        + "\n      \"panelId\": \"<guid>\",\n      \"col\": 0,\n      \"row\": 0,\n      \"label\": \"c0\","
        + "\n      \"placed\": 580,\n      \"skipped\": 0,\n      \"failed\": 7,\n      \"problem\": null,"
        + "\n      \"facets\": [ { \"facetId\": \"0\", \"accepted\": true, \"matches\": 2140, \"inliers\": 1312,"
        + "\n        \"coverage\": 0.71, \"rmsMm\": 2.4, \"reason\": null } ]\n    }\n  ]\n}";

    private const string PlacementNote =
        "Takes a few seconds per photo × facet. A facet is used when at least 40 matches agree on one mapping "
        + "and they span at least 20 % of the facet's view in the photo. failed counts holds no accepted facet "
        + "contains. 409 when the wall has no active model with textures, or a run is already in progress.";

    private const string PlacementStatusJson =
        "{\n  \"enabled\": true,\n  \"hasTextures\": true,\n  \"latestRun\": {\n    \"id\": \"<guid>\",\n    \"createdAt\": \"...\","
        + "\n    \"trigger\": \"api\",\n    \"placed\": 861,\n    \"skipped\": 0,\n    \"failed\": 19,\n    \"panels\": [ ],"
        + "\n    \"revertedAt\": null\n  }\n}";

    private const string PlacementRevertJson = "{\n  \"reverted\": 861,\n  \"skippedEdited\": [ ],\n  \"missing\": 0\n}";

    private static ApiParamDoc[] PlacementWall => [new("wallId", "path", "The wall.")];

    private static ApiParamDoc[] WallAndRun =>
    [
        new("wallId", "path", "The wall."),
        new("runId", "path", "The run to revert (runId of the POST, or latestRun.id of the GET)."),
    ];

    private static ApiSurfaceDoc WallGeometryPlacementSurface => new(
        "Wall 3D model: place existing holds",
        PlacementIntro,
        "Wall key or personal key with write access (admin owner)",
        PlacementEndpoints());

    private static ApiEndpointDoc[] PlacementEndpoints() =>
    [
        new("POST", PlacementBase, "Registers the photos onto the model's textures and places the holds.", PlacementWall, null, PlacementResultJson, PlacementNote),
        new("GET", PlacementBase, "Whether the action is available, and the latest run.", PlacementWall, null, PlacementStatusJson),
        new("POST", PlacementBase + "/{runId}/revert", "Reverts a run; holds moved or re-placed since are left as they are.", WallAndRun, null, PlacementRevertJson),
    ];
}
