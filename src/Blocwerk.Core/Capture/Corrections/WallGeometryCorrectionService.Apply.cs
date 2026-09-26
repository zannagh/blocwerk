// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Globalization;
using System.Text.Json.Nodes;
using Blocwerk.Core.Geometry.Corrections;
using Blocwerk.Core.Geometry.Footprints;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Capture.Corrections;

/// <summary>The three corrections: each computes the new document and hands it to the version writer.</summary>
public sealed partial class WallGeometryCorrectionService
{
    public async Task<GeometryCorrectionResult> MakeSizesExactAsync(Guid wallId, CaptureScaleReference reference)
    {
        var context = await OpenCorrectionAsync(wallId);
        await using (context.Db)
        {
            if (!context.Document.IsFeatureFrame)
            {
                throw new UserFacingException("This model's sizes come from the printed markers; there is nothing to make exact.");
            }

            var camera = await PhotoCameraAsync(context, reference.PhotoIndex);
            var (measurement, refusal) = GeometryCorrectionMath.MeasureScale(context.Document, camera, reference.A, reference.B, reference.Mm);
            if (measurement is null)
            {
                throw new UserFacingException(refusal!);
            }

            var t = GeometrySimilarity.Scaling(measurement.Scale);
            var summary = string.Create(
                CultureInfo.InvariantCulture,
                $"Sizes made exact: ×{measurement.Scale:0.0000} (the model had {measurement.ModelMm:0} mm where {reference.Mm:0} mm was measured)");
            var stamp = Stamp(GeometryCorrectionKind.Scale, context, t, summary);
            stamp["measured"] = new JsonObject
            {
                ["photo"] = CaptureComputeDocuments.PhotoName(reference.PhotoIndex),
                ["a"] = GeometryJson.Array(reference.A, 1),
                ["b"] = GeometryJson.Array(reference.B, 1),
                ["mm"] = reference.Mm,
                ["modelMm"] = Math.Round(measurement.ModelMm, 2),
                ["facets"] = new JsonArray(measurement.A.FacetId, measurement.B.FacetId),
            };
            var json = WallGeometryModelTransformer.Stamp(
                WallGeometryModelTransformer.TransformDocument(context.Model.Json, t), stamp, world =>
                {
                    world["scaleKnown"] = true;
                    world["scaleSource"] = "measured";
                });
            return await SaveVersionAsync(context, json, t, dropped: null, GeometryCorrectionKind.Scale, summary);
        }
    }

    public async Task<GeometryCorrectionResult> SetVerticalSurfaceAsync(Guid wallId, string facetId)
    {
        var context = await OpenCorrectionAsync(wallId);
        await using (context.Db)
        {
            var (rotation, tilt, refusal) = GeometryCorrectionMath.PlumbRotation(context.Document, facetId);
            if (rotation is null)
            {
                throw new UserFacingException(refusal!);
            }

            var summary = string.Create(
                CultureInfo.InvariantCulture, $"Surface {facetId} declared vertical: the model turned by {rotation.RotationDeg:0.0}° (it leaned {tilt:0.0}°)");
            var stamp = Stamp(GeometryCorrectionKind.Vertical, context, rotation, summary);
            stamp["facet"] = facetId;
            var json = WallGeometryModelTransformer.Stamp(
                WallGeometryModelTransformer.TransformDocument(context.Model.Json, rotation), stamp, world =>
                {
                    world["up"] = new JsonArray(0.0, 0.0, 1.0);
                    world["gravityKnown"] = true;
                    world["gravitySource"] = "declared";
                    world["verticalFacet"] = facetId;
                });
            json = MarkGravityReference(json, facetId);
            return await SaveVersionAsync(context, json, rotation, dropped: null, GeometryCorrectionKind.Vertical, summary);
        }
    }

    public async Task<GeometryCorrectionResult> DropSurfaceAsync(Guid wallId, string facetId)
    {
        var context = await OpenCorrectionAsync(wallId);
        await using (context.Db)
        {
            var facets = context.Document.Segments.SelectMany(s => s.Facets).Select(f => f.Id).ToList();
            if (!facets.Contains(facetId))
            {
                throw new UserFacingException("The model has no such surface.");
            }

            if (facets.Count == 1)
            {
                throw new UserFacingException("That is the model's only surface; it cannot be dropped.");
            }

            if (facetId == ReferenceFacet(context.Model.Json))
            {
                throw new UserFacingException("That surface defines the model's frame (the main wall); it cannot be dropped.");
            }

            var summary = $"Surface {facetId} dropped: not part of the wall";
            var stamp = Stamp(GeometryCorrectionKind.Drop, context, GeometrySimilarity.Identity, summary);
            stamp["facet"] = facetId;
            var json = WallGeometryModelTransformer.Stamp(WallGeometryModelTransformer.DropFacet(context.Model.Json, facetId)!, stamp);
            return await SaveVersionAsync(context, json, GeometrySimilarity.Identity, facetId, GeometryCorrectionKind.Drop, summary);
        }
    }

    private static JsonObject Stamp(GeometryCorrectionKind kind, CorrectionContext context, GeometrySimilarity t, string summary) => new()
    {
        ["kind"] = kind.ToString().ToLowerInvariant(),
        ["fromModelId"] = context.Model.Id.ToString(),
        ["scale"] = Math.Round(t.Scale, 6),
        ["rotationDeg"] = Math.Round(t.RotationDeg, 3),
        ["summary"] = summary,
        ["at"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
    };

    private static string ReferenceFacet(string json) =>
        GeometryJson.Text(JsonNode.Parse(json)?["world"], "referenceFacet") ?? "0";

    /// <summary>The declared surface's segment becomes the gravity reference (its angle is no measurement any more).</summary>
    private static string MarkGravityReference(string json, string facetId)
    {
        var root = JsonNode.Parse(json)!.AsObject();
        foreach (var segment in (root["segments"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var contains = (segment["facets"] as JsonArray ?? []).OfType<JsonObject>().Any(f => GeometryJson.Text(f, "id") == facetId);
            segment["angleIsGravityReference"] = contains;
        }

        return root.ToJsonString();
    }

    /// <summary>The solved camera of the capture photo <paramref name="index"/>, at the stored photo's resolution.</summary>
    private static async Task<SolvedCamera> PhotoCameraAsync(CorrectionContext context, int index)
    {
        var photo = context.CaptureId is { } captureId
            ? await context.Db.WallCapturePhotos.AsNoTracking().FirstOrDefaultAsync(p => p.CaptureId == captureId && p.Index == index)
            : null;
        if (photo is null)
        {
            throw new UserFacingException("That photo of the model's capture is no longer stored. Pick another one.");
        }

        var name = CaptureComputeDocuments.PhotoName(index);
        var camera = SolvedCamera.ParseAll(context.Model.Json).FirstOrDefault(c => c.Image == name)
                     ?? throw new UserFacingException("That photo was not placed in the 3D model. Pick another one.");
        return camera.ScaledTo(photo.Width, photo.Height);
    }
}
