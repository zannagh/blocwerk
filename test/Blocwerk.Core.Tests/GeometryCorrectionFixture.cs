// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Text.Json.Nodes;
using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Capture.Corrections;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Geometry.Corrections;
using Microsoft.Extensions.Logging.Abstractions;

namespace Blocwerk.Core.Tests;

/// <summary>
/// A wall whose active model came from a finished markerless capture: the model (the markerless fixture's feature
/// document by default), its capture with photos p01..p03 (2000 × 1500, matching the document's cameras), a texture per
/// facet and a photo-real view. Camera p01 stands 3 m in front of facet "0" at x = 500, z = 800: pixel (1000, 750) sees
/// the wall point (500, 0, 800) and pixel (1600, 750) the point (2300, 0, 800), 1800 mm apart.
/// </summary>
internal static class GeometryCorrectionFixture
{
    public static async Task<(Guid ModelId, Guid CaptureId)> SeedAsync(
        WallTestHarness h, string? json = null, WallCaptureStatus status = WallCaptureStatus.Succeeded, bool features = true)
    {
        await h.SeedWallAsync(holdCount: 0);
        await using var db = h.CreateContext();
        var model = new WallGeometryModel
        {
            WallId = h.WallId,
            Json = json ?? MarkerlessFixture.FeatureDoc(anchored: false),
            SchemaVersion = 1,
            Source = "capture test",
            IsActive = true,
            FrameSource = features ? WallGeometryFrameSource.Features : WallGeometryFrameSource.Markers,
        };
        var capture = new WallCapture
        {
            WallId = h.WallId, CreatedByUserId = h.Owner.Id, Status = status, GeometryModelId = model.Id, GeometryMode = WallCaptureGeometryMode.Features,
            FollowUpJson = "{\"steps\":[]}", CoverageJson = "{}",
        };
        db.WallGeometryModels.Add(model);
        db.WallCaptures.Add(capture);
        for (var i = 1; i <= 3; i++)
        {
            db.WallCapturePhotos.Add(new WallCapturePhoto
            {
                CaptureId = capture.Id, Index = i, StoredPath = $"p{i}.jpg", ContentHash = $"hash{i}", Width = 2000, Height = 1500,
            });
        }

        foreach (var facet in new[] { "0", "1" })
        {
            db.WallGeometryTextures.Add(new WallGeometryTexture
            {
                GeometryModelId = model.Id, FacetId = facet, StoredPath = $"tex{facet}.jpg", MaskStoredPath = $"mask{facet}.png",
                AMin = -50, AMax = 3000, BMin = -50, BMax = 2500, WidthPx = 1000, HeightPx = 800,
            });
        }

        db.WallGeometrySplats.Add(new WallGeometrySplat { GeometryModelId = model.Id, StoredPath = "wall.spz", FrameJson = Frame() });
        await db.SaveChangesAsync();
        return (model.Id, capture.Id);
    }

    public static WallGeometryCorrectionService Service(WallTestHarness h, CorrectionFollowUpQueue queue, IKioskContext? kiosk = null) =>
        new(h.DbContextFactory, h.CurrentUser, queue, NullLogger<WallGeometryCorrectionService>.Instance, kiosk);

    /// <summary>The feature document turned 3° about x with "up" left at z: facet "0" then leans 3° in the model.</summary>
    public static string LeaningDoc()
    {
        var turn = GeometrySimilarity.RotationBetween([0, 0, 1], [0, Math.Sin(Math.PI / 60), Math.Cos(Math.PI / 60)], [0, 0, 0]);
        var root = JsonNode.Parse(WallGeometryModelTransformer.TransformDocument(MarkerlessFixture.FeatureDoc(anchored: false), turn))!;
        root["world"]!["up"] = new JsonArray(0.0, 0.0, 1.0);
        root["world"]!["gravityKnown"] = false;
        root["world"]!["gravitySource"] = "cameras";
        return root.ToJsonString();
    }

    private static string Frame() => new JsonObject
    {
        ["aligned"] = true,
        ["toWorldMm"] = new JsonArray(
            new JsonArray(500.0, 0.0, 0.0, 10.0), new JsonArray(0.0, 500.0, 0.0, 20.0), new JsonArray(0.0, 0.0, 500.0, 30.0), new JsonArray(0.0, 0.0, 0.0, 1.0)),
        ["scaleMmPerUnit"] = 500.0,
    }.ToJsonString();
}
