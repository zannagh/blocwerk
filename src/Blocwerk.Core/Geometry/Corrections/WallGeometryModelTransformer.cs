// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Text.Json.Nodes;

namespace Blocwerk.Core.Geometry.Corrections;

/// <summary>A texture's facet-plane bounds (mm), as stored on <see cref="Entities.WallGeometryTexture"/>.</summary>
/// <param name="AMin">Lower a.</param>
/// <param name="AMax">Upper a.</param>
/// <param name="BMin">Lower b.</param>
/// <param name="BMax">Upper b.</param>
public readonly record struct TextureBounds(double AMin, double AMax, double BMin, double BMax);

/// <summary>
/// Applies a <see cref="GeometrySimilarity"/> to a stored model so a correction can become a new model version that
/// REUSES the old one's texture and photo-real files: the geometry document (facet frames and extents, marker corners,
/// cameras, "up"), the textures' plane bounds and the splat's <c>frame.json</c> (<c>toWorldMm</c>, <c>matrix</c>).
/// A facet frame moves rigidly with the world (u, v, normal rotate; plane coordinates scale by <c>s</c>), so a texture
/// image and a hold's (facet, a, b) keep showing the same spot once scaled. Also drops a facet and re-derives the
/// angles from "up". Pure functions on JSON: every field it does not touch is kept.
/// </summary>
public static partial class WallGeometryModelTransformer
{
    /// <summary>The document mapped by <paramref name="t"/>; the angles are re-derived from the rotated "up".</summary>
    /// <param name="json">The model's <c>wall-geometry.json</c>.</param>
    /// <param name="t">The similarity.</param>
    /// <returns>The mapped document.</returns>
    public static string TransformDocument(string json, GeometrySimilarity t)
    {
        var root = JsonNode.Parse(json)!.AsObject();
        Transform(root, t);
        return root.ToJsonString();
    }

    /// <summary>A texture's bounds after <paramref name="t"/>: plane coordinates scale by <c>s</c> (the frame moves rigidly).</summary>
    /// <param name="bounds">The stored bounds.</param>
    /// <param name="t">The similarity.</param>
    /// <returns>The new bounds.</returns>
    public static TextureBounds TransformTexture(TextureBounds bounds, GeometrySimilarity t) =>
        new(bounds.AMin * t.Scale, bounds.AMax * t.Scale, bounds.BMin * t.Scale, bounds.BMax * t.Scale);

    /// <summary>The document without facet <paramref name="facetId"/> (an empty segment goes too, and the facet's markers).</summary>
    /// <param name="json">The document.</param>
    /// <param name="facetId">The facet to drop.</param>
    /// <returns>The document, or null when it has no such facet.</returns>
    public static string? DropFacet(string json, string facetId)
    {
        var root = JsonNode.Parse(json)!.AsObject();
        var hit = GeometryJson.SegmentFacets(root).FirstOrDefault(sf => GeometryJson.Text(sf.Facet, "id") == facetId);
        if (hit.Facet is null)
        {
            return null;
        }

        var facets = (JsonArray)hit.Segment["facets"]!;
        facets.Remove(hit.Facet);
        if (facets.Count == 0)
        {
            ((JsonArray)root["segments"]!).Remove(hit.Segment);
        }

        if (root["markers"] is JsonArray markers)
        {
            foreach (var marker in markers.OfType<JsonObject>().Where(m => GeometryJson.Text(m, "facet") == facetId).ToList())
            {
                markers.Remove(marker);
            }
        }

        RecomputeAngles(root);
        return root.ToJsonString();
    }

    /// <summary>Records what a correction did in <c>quality.correction</c> (replacing an earlier one).</summary>
    /// <param name="json">The document.</param>
    /// <param name="correction">The record.</param>
    /// <param name="world">Changes to the <c>world</c> block (e.g. the new scale or gravity source), or null.</param>
    /// <returns>The stamped document.</returns>
    public static string Stamp(string json, JsonObject correction, Action<JsonObject>? world = null)
    {
        var root = JsonNode.Parse(json)!.AsObject();
        GeometryJson.Child(root, "quality")["correction"] = correction.Parent is null ? correction : correction.DeepClone();
        world?.Invoke(GeometryJson.Child(root, "world"));
        RecomputeAngles(root);
        return root.ToJsonString();
    }

    private static void Transform(JsonObject root, GeometrySimilarity t)
    {
        foreach (var (_, facet) in GeometryJson.SegmentFacets(root))
        {
            GeometryJson.Set(facet, "origin", GeometryJson.Vec(facet["origin"]) is { } o ? t.Apply(o) : null, 2);
            GeometryJson.Set(facet, "u", GeometryJson.Vec(facet["u"]) is { } u ? t.Rotate(u) : null, 6);
            GeometryJson.Set(facet, "v", GeometryJson.Vec(facet["v"]) is { } v ? t.Rotate(v) : null, 6);
            GeometryJson.Set(facet, "normal", GeometryJson.Vec(facet["normal"]) is { } n ? t.Rotate(n) : null, 6);
            if (facet["extentMm"] is JsonObject extent)
            {
                foreach (var key in new[] { "aMin", "aMax", "bMin", "bMax" })
                {
                    if (GeometryJson.Number(extent, key) is { } value)
                    {
                        extent[key] = Math.Round(value * t.Scale, 1) + 0.0;
                    }
                }
            }
        }

        TransformMarkers(root, t);
        TransformCameras(root, t);
        if (root["world"] is JsonObject world && GeometryJson.Vec(world["up"]) is { } up)
        {
            world["up"] = GeometryJson.Array(GeometrySimilarity.Unit(t.Rotate(up)), 6);
        }

        RecomputeAngles(root);
    }

    private static void TransformMarkers(JsonObject root, GeometrySimilarity t)
    {
        foreach (var marker in (root["markers"] as JsonArray ?? []).OfType<JsonObject>())
        {
            if (marker["cornersPlaneMm"] is JsonArray plane)
            {
                marker["cornersPlaneMm"] = new JsonArray(plane
                    .Select(c => GeometryJson.Numbers(c) is { Length: 2 } ab ? (JsonNode?)GeometryJson.Array([ab[0] * t.Scale, ab[1] * t.Scale], 2) : c?.DeepClone())
                    .ToArray());
            }

            if (marker["cornersWorldMm"] is JsonArray world)
            {
                marker["cornersWorldMm"] = new JsonArray(world
                    .Select(c => GeometryJson.Vec(c) is { } p ? (JsonNode?)GeometryJson.Array(t.Apply(p), 2) : c?.DeepClone())
                    .ToArray());
            }

            if (GeometryJson.Number(marker, "measuredSideMm") is { } side)
            {
                marker["measuredSideMm"] = Math.Round(side * t.Scale, 2);
            }
        }
    }

    /// <summary>
    /// A camera maps world → camera as <c>x = R·X + t</c>. With <c>X = Qᵀ(X' − T)/s</c> and the camera frame scaled by s
    /// (so it stays metric): <c>R' = R·Qᵀ</c>, <c>t' = s·t − R'·T</c>. The projection is unchanged.
    /// </summary>
    private static void TransformCameras(JsonObject root, GeometrySimilarity t)
    {
        var q = t.Rotation;
        foreach (var camera in (root["cameras"] as JsonArray ?? []).OfType<JsonObject>())
        {
            if (GeometryJson.Numbers(camera["R"]) is not { Length: 9 } r || GeometryJson.Vec(camera["t"]) is not { } tc)
            {
                continue;
            }

            var rotated = new double[9];
            for (var i = 0; i < 3; i++)
            {
                for (var j = 0; j < 3; j++)
                {
                    // (R · Qᵀ)[i, j] = Σ_k R[i, k] · Q[j, k]
                    rotated[(3 * i) + j] = Enumerable.Range(0, 3).Sum(k => r[(3 * i) + k] * q[(3 * j) + k]);
                }
            }

            var shift = GeometrySimilarity.Multiply(rotated, t.Translation);
            camera["R"] = GeometryJson.Array(rotated, 6);
            camera["t"] = GeometryJson.Array([(t.Scale * tc[0]) - shift[0], (t.Scale * tc[1]) - shift[1], (t.Scale * tc[2]) - shift[2]], 2);
        }
    }
}
