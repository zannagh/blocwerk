// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Web.Components.Shared;

/// <summary>The reference for the 3D model corrections (WallGeometryCorrectionsController).</summary>
internal static partial class ApiDocsData
{
    private const string CorrectionBase = "/api/walls/{wallId}/geometry/corrections";

    private const string CorrectionIntro =
        "Corrects the wall's active 3D model, exactly as the geometry panel does. Each correction creates a NEW active model "
        + "version derived from the current one (textures and the photo-real view are reused, their placement mapped); the old "
        + "version stays in the history and can be activated again. The existing holds are then placed on the new version with "
        + "no further call. A Wall-scoped key for the wall, or a personal key with write access, whose owner is an admin of the "
        + "wall (a member's key gets 403, kiosk tablets are refused).";

    private const string CorrectionStateJson =
        "{\n  \"modelId\": \"<guid>\",\n  \"fromFeatures\": true,\n  \"scale\": \"estimated (±10 %)\",\n  \"scaleIsEstimate\": true,"
        + "\n  \"gravity\": \"from the floor\",\n  \"gravityKnown\": true,\n  \"captureId\": \"<guid>\","
        + "\n  \"photos\": [ { \"photoId\": \"<guid>\", \"index\": 3, \"width\": 3024, \"height\": 4032 } ],"
        + "\n  \"facets\": [ { \"id\": \"0\", \"name\": \"Main wall\", \"angleDeg\": 45.1, \"isReference\": true, \"isVertical\": false } ],"
        + "\n  \"lastCorrection\": null\n}";

    private const string CorrectionResultJson =
        "{\n  \"kind\": 0,\n  \"modelId\": \"<guid>\",\n  \"previousModelId\": \"<guid>\",\n  \"scale\": 1.0123,\n  \"rotationDeg\": 0,"
        + "\n  \"summary\": \"Sizes made exact: ×1.0123 (the model had 1185 mm where 1200 mm was measured)\"\n}";

    private const string CorrectionNote = "409 with the reason when it cannot be done (nothing changes then).";

    private static ApiParamDoc[] CorrectionWall => [new("wallId", "path", "The wall.")];

    private static ApiSurfaceDoc WallGeometryCorrectionSurface => new(
        "Wall 3D model: corrections",
        CorrectionIntro,
        "Wall key or personal key with write access (admin owner)",
        [
            new("GET", CorrectionBase, "Where the sizes and angles come from, the surfaces, and the photos a distance can be measured on.",
                CorrectionWall, null, CorrectionStateJson, "404 when the wall has no model."),
            new("POST", CorrectionBase + "/scale", "Make sizes exact: two points on a photo of the model's capture and the mm between them.",
                CorrectionWall, "{\n  \"photoIndex\": 3,\n  \"a\": [1210.5, 1802],\n  \"b\": [2311, 1795.5],\n  \"mm\": 1200\n}",
                CorrectionResultJson, "Points in stored-photo pixels. " + CorrectionNote),
            new("POST", CorrectionBase + "/vertical", "This surface is vertical: the model turns so it is plumb.", CorrectionWall,
                "{ \"facetId\": \"2\" }", CorrectionResultJson, CorrectionNote),
            new("POST", CorrectionBase + "/drop", "Not part of the wall: drops the surface.", CorrectionWall,
                "{ \"facetId\": \"3\" }", CorrectionResultJson, CorrectionNote),
        ]);
}
