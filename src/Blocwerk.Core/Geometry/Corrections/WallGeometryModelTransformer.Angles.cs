// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Text.Json.Nodes;

namespace Blocwerk.Core.Geometry.Corrections;

/// <summary>The measured angles re-derived from the document's "up" (the solver's <c>frame.py</c> conventions).</summary>
public static partial class WallGeometryModelTransformer
{
    /// <summary>Tilt from vertical: positive = overhang (the normal points down).</summary>
    /// <param name="normal">The facet normal (out of the wall).</param>
    /// <param name="up">Unit "up".</param>
    /// <returns>Degrees.</returns>
    public static double TiltDeg(double[] normal, double[] up) =>
        Math.Asin(Math.Clamp(-GeometrySimilarity.Dot(GeometrySimilarity.Unit(normal), up), -1, 1)) * 180 / Math.PI;

    /// <summary>
    /// Re-derives every facet's <c>measuredAngleDeg</c> (tilt from vertical, + overhang) and <c>yawDeg</c> (about "up",
    /// relative to the reference facet, counter-clockwise from above) and the segments' angles, from <c>world.up</c>.
    /// Without a known "up" (<c>gravityKnown: false</c>) the absolute angles stay unmeasured (null).
    /// </summary>
    /// <param name="root">The document.</param>
    internal static void RecomputeAngles(JsonObject root)
    {
        var world = root["world"] as JsonObject;
        var up = GeometryJson.Vec(world?["up"]) is { } u ? GeometrySimilarity.Unit(u) : [0, 0, 1];
        var known = world?["gravityKnown"] is not JsonValue g || !g.TryGetValue<bool>(out var k) || k;
        var facets = GeometryJson.SegmentFacets(root);
        var reference = ReferenceNormal(facets, GeometryJson.Text(world, "referenceFacet"));
        foreach (var (_, facet) in facets)
        {
            if (GeometryJson.Vec(facet["normal"]) is not { } n)
            {
                continue;
            }

            facet["measuredAngleDeg"] = known ? Round(TiltDeg(n, up)) : null;
            facet["yawDeg"] = known && reference is not null && YawDeg(n, reference, up) is { } yaw ? Round(yaw) : null;
        }

        foreach (var segment in (root["segments"] as JsonArray ?? []).OfType<JsonObject>())
        {
            // A folded segment without a segment-level angle keeps none (its first facet stands in for it on read).
            var own = (segment["facets"] as JsonArray)?.OfType<JsonObject>().ToList() ?? [];
            if (own.Count != 1 && GeometryJson.Number(segment, "measuredAngleDeg") is null)
            {
                continue;
            }

            var measured = own.Count == 0 ? null : GeometryJson.Number(own[0], "measuredAngleDeg");
            segment["measuredAngleDeg"] = measured;
            var declared = GeometryJson.Number(segment, "declaredAngleDeg");
            segment["declaredVsMeasuredDeg"] = measured is { } m && declared is { } d ? Math.Round(m - d, 2) : null;
        }
    }

    private static double[]? ReferenceNormal(List<(JsonObject Segment, JsonObject Facet)> facets, string? referenceId)
    {
        var reference = facets.FirstOrDefault(sf => GeometryJson.Text(sf.Facet, "id") == (referenceId ?? "0")).Facet
                        ?? facets.FirstOrDefault().Facet;
        return reference is null ? null : GeometryJson.Vec(reference["normal"]);
    }

    private static double? YawDeg(double[] n, double[] reference, double[] up)
    {
        var a = Horizontal(reference, up);
        var b = Horizontal(n, up);
        if (a is null || b is null)
        {
            return null;
        }

        var cross = GeometrySimilarity.Cross(a, b);
        return Math.Atan2(GeometrySimilarity.Dot(cross, up), GeometrySimilarity.Dot(a, b)) * 180 / Math.PI;
    }

    private static double[]? Horizontal(double[] v, double[] up)
    {
        var along = GeometrySimilarity.Dot(v, up);
        double[] h = [v[0] - (along * up[0]), v[1] - (along * up[1]), v[2] - (along * up[2])];
        return Math.Sqrt(GeometrySimilarity.Dot(h, h)) < 1e-9 ? null : GeometrySimilarity.Unit(h);
    }

    private static double Round(double degrees) => Math.Round(degrees, 3) + 0.0;
}
