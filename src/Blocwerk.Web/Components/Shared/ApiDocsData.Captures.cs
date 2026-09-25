// <copyright file="ApiDocsData.Captures.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Web.Components.Shared;

/// <summary>The reference for the capture API (WallCapturesController).</summary>
internal static partial class ApiDocsData
{
    private const string CaptureBase = "/api/walls/{wallId}/captures";

    private const string CaptureIntro =
        "Feeds new photos of a marker wall into its 3D model, exactly as the capture panel does: open a draft, stream "
        + "the photos in, start it, poll it. Once the model is live the server places the wall's existing holds on it, "
        + "refines their 3D shapes and (with a photo-real view) measures them, with no further call; panel holds and "
        + "boulders are never changed. A Wall-scoped key for the wall, or a personal key with write access, whose "
        + "owner is an admin of the wall (a member's key gets 403, kiosk tablets are refused). The optional walk-along "
        + "video uses POST /api/captures/{captureId}/video (personal key), and only when a photo-real worker is set up.";

    private const string CaptureDraftJson =
        "{\n  \"captureId\": \"<guid>\",\n  \"notes\": null,\n  \"photos\": [\n    { \"photoId\": \"<guid>\", \"index\": 0, "
        + "\"fileName\": \"IMG_0001.jpg\", \"width\": 4032, \"height\": 3024,\n      \"focal35mm\": 26, \"markerIds\": [0, 1, 6], "
        + "\"warnings\": [] }\n  ],\n  \"plan\": null,\n  \"video\": null\n}";

    private const string CaptureUploadJson =
        "{\n  \"stored\": 2,\n  \"refused\": 1,\n  \"items\": [\n    { \"fileName\": \"IMG_0001.jpg\", \"photo\": { \"photoId\": \"<guid>\", "
        + "\"markerIds\": [0, 1, 6] }, \"error\": null },\n    { \"fileName\": \"IMG_0001.jpg\", \"photo\": null,\n      "
        + "\"error\": \"IMG_0001.jpg was already uploaded to this capture.\" }\n  ]\n}";

    private const string CaptureUploadNote =
        "Each part is analysed like an upload in the panel; a refused photo is reported and the next one still goes in.";

    private const string CapturePlanJson =
        "{\n  \"accepted\": true,\n  \"errors\": [],\n  \"notes\": [ \"Saved as the wall's marker plan (revision 3).\" ]\n}";

    private const string CaptureDeclarationsJson =
        "{\n  \"declarations\": { \"segments\": [ … ], \"levelPairs\": [ [14, 15] ] },\n  \"warnings\": [ \"Segment 5 (“Cave”) has no angle, "
        + "so its markers will be merged into the nearest wall face. …\" ]\n}";

    private const string CaptureStartBody =
        "{\n  \"segments\": [ { \"index\": 0, \"name\": \"main wall\", \"declaredAngleDeg\": 30, \"verticalReference\": false } ],"
        + "\n  \"levelPairs\": [ [14, 15] ],\n  \"notes\": \"after the reset\",\n  \"quality\": \"High\"\n}";

    private const string CaptureStartNote =
        "Every field is optional (omitted: the suggested declarations, quality High). 202 when queued; 422 with {\"problems\": [...]} otherwise.";

    private const string CaptureStatusJson =
        "{\n  \"id\": \"<guid>\",\n  \"status\": 5,\n  \"progress\": 1,\n  \"stage\": \"Done\",\n  \"error\": null,"
        + "\n  \"photoCount\": 24,\n  \"geometryModelId\": \"<guid>\",\n  \"followUp\": \"856 holds placed on the 3D model, "
        + "653 hold shapes refined from several photos.\",\n  \"followUpNote\": null,\n  \"wallId\": \"<guid>\","
        + "\n  \"modelChecks\": [\n    { \"kind\": \"segment-angle\", \"level\": \"info\", \"message\": \"Main wall 45.2° overhang (declared 45°)\" }\n  ]\n}";

    private const string CaptureStatusNote =
        "status: 1 queued … 5 done, 6 done without textures, 7 failed, 8 photo-real view training, 9 done without the photo-real view. "
        + "followUp says what the new model did for the existing holds; followUpNote what it could not do (or a skipped photo-real view). "
        + "modelChecks is what the solver said about the model (measured angles, warnings, ignored detections); level is info or warning.";

    private const string CaptureCoverageJson =
        "{\n  \"version\": 1,\n  \"captureId\": \"<guid>\",\n  \"photoViews\": 53,\n  \"videoViews\": 0,\n  \"advice\": [\n    { \"kind\": \"volume\", "
        + "\"text\": \"Shoot the undersides of the 3 volumes on the right of the main wall (volumes 4, 5 and 6) from below\", \"facetId\": \"0\" }\n  ],"
        + "\n  \"facets\": [ { \"facetId\": \"0\", \"name\": \"Main wall\", \"cols\": 33, \"rows\": 17, \"cells\": \"ggdd…\", \"counts\": { … }, "
        + "\"markers\": { … } } ],\n  \"volumes\": [ { \"index\": 4, \"faces\": [ { \"face\": \"underside\", \"status\": \"never\" } ] } ],"
        + "\n  \"video\": { \"hasVideo\": true, \"framesExtracted\": 300, \"framesRegistered\": 300, \"posesFrom\": \"photos\", \"passes\": [ … ] }\n}";

    private const string CaptureCoverageNote =
        "cells: one letter per 200 mm cell, row by row from the bottom: g good, d seen from fewer than 3 directions, a only at a steep angle, "
        + "r only from far away (coarser than 2 mm per pixel), n never seen, . not surface (under a volume or inside the wall). 404 until the capture is done.";

    private static ApiParamDoc[] CaptureWall => [new("wallId", "path", "The wall.")];

    private static ApiParamDoc[] CaptureOnWall =>
    [
        new("wallId", "path", "The wall."),
        new("captureId", "path", "The capture (the draft's captureId)."),
    ];

    private static ApiSurfaceDoc WallCaptureSurface => new(
        "Wall 3D model: captures",
        CaptureIntro,
        "Wall key or personal key with write access (admin owner)",
        CaptureEndpoints());

    private static ApiEndpointDoc[] CaptureEndpoints() =>
    [
        new("POST", CaptureBase + "/draft", "Opens a draft, or returns your open one.", CaptureWall, null, CaptureDraftJson,
            "409 when the wall has no printed markers switched on."),
        new("GET", CaptureBase + "/draft", "Your open draft with its photos, or 404.", CaptureWall, null, CaptureDraftJson),
        new("POST", CaptureBase + "/{captureId}/photos", "Streams photos into the draft (multipart/form-data, any number of file parts).",
            CaptureOnWall, null, CaptureUploadJson, CaptureUploadNote),
        new("PUT", CaptureBase + "/{captureId}/plan", "Uses this marker-plan.json for the draft (optional).", CaptureOnWall,
            "{ … marker-plan.json … }", CapturePlanJson, "422 with the errors when it is not a usable plan."),
        new("GET", CaptureBase + "/{captureId}/declarations", "The declarations the server would use, with their warnings.", CaptureOnWall,
            null, CaptureDeclarationsJson),
        new("POST", CaptureBase + "/{captureId}/start", "Starts the draft.", CaptureOnWall, CaptureStartBody,
            "{\n  \"captureId\": \"<guid>\",\n  \"warnings\": []\n}", CaptureStartNote),
        new("GET", CaptureBase + "/{captureId}", "One capture's status; poll it after starting.", CaptureOnWall, null, CaptureStatusJson,
            CaptureStatusNote),
        new("GET", CaptureBase + "/{captureId}/coverage", "What the capture saw and what the next capture should add.", CaptureOnWall, null,
            CaptureCoverageJson, CaptureCoverageNote),
        new("GET", CaptureBase, "The wall's captures, newest first.", CaptureWall, null, "[ … same shape as GET /{captureId} … ]"),
        new("DELETE", CaptureBase + "/{captureId}", "Deletes an open draft and its photos.", CaptureOnWall, null, null, "204 No Content."),
    ];
}
