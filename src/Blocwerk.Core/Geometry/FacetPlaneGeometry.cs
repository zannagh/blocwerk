// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Text.Json.Nodes;
using Blocwerk.Core.Geometry.Corrections;
using Blocwerk.Core.Geometry.View3D;

namespace Blocwerk.Core.Geometry;

/// <summary>
/// A facet's in-plane geometry in a geometry document: its <c>extentMm</c> and, for models solved from photo features, its
/// fold-clipped <c>outlineMm</c> (<see cref="WallGeometryFacet.OutlineMm"/>). Whatever rewrites a facet's plane frame or
/// extent (a scale correction, a registration rebase, an extent grown over carried markers) goes through
/// <see cref="Rewrite"/>, so the outline never stays behind in the old frame (<c>docs/geometry-kernel.md</c>, rule 7).
/// </summary>
public static class FacetPlaneGeometry
{
    /// <summary>
    /// Sets the facet's extent to <paramref name="extent"/> and carries its outline along: the outline's fold clips
    /// (<see cref="GeometryKernel.OutlineHalfPlanes"/> against the OLD extent) are mapped by <paramref name="map"/> (old
    /// plane (a, b) → new plane (a, b)) and cut the new extent rectangle, so the outline's other sides are the new
    /// extent's sides again. An outline that no fold clip cuts any more is dropped, and so is one whose old extent is
    /// missing or empty (its sides could not be told from its folds).
    /// </summary>
    /// <param name="facet">The facet's JSON object.</param>
    /// <param name="map">Old plane coordinates → new plane coordinates (identity when only the extent changes).</param>
    /// <param name="extent">The new extent.</param>
    public static void Rewrite(JsonObject facet, Func<double[], double[]> map, PlaneRectMm extent)
    {
        var old = facet["extentMm"] is JsonObject e
            ? new PlaneRectMm(Num(e, "aMin"), Num(e, "aMax"), Num(e, "bMin"), Num(e, "bMax"))
            : (PlaneRectMm?)null;
        facet["extentMm"] = new JsonObject
        {
            ["aMin"] = Math.Round(extent.AMin, 1) + 0.0,
            ["aMax"] = Math.Round(extent.AMax, 1) + 0.0,
            ["bMin"] = Math.Round(extent.BMin, 1) + 0.0,
            ["bMax"] = Math.Round(extent.BMax, 1) + 0.0,
        };
        if (facet["outlineMm"] is not JsonArray outlineNode)
        {
            return;
        }

        if (old is not { Area: > 0 })
        {
            // Without the old extent the outline's sides cannot be told from its fold clips: drop it rather than cut
            // the new extent along the old rectangle's sides.
            facet.Remove("outlineMm");
            return;
        }

        var outline = outlineNode.Select(GeometryJson.Numbers).OfType<double[]>().Where(p => p.Length >= 2).ToList();
        IReadOnlyList<double[]>? shape = Wall3DFacetOutlines.RectCorners(extent);
        foreach (var fold in GeometryKernel.OutlineHalfPlanes(outline, old))
        {
            var side = Mapped(fold, map);
            shape = shape is null ? null : Wall3DFacetOutlines.ClipHalf(shape, side);
        }

        var rect = Wall3DFacetOutlines.RectCorners(extent);
        if (shape is null || (shape.Count == 4 && shape.All(p => rect.Any(c => Math.Abs(c[0] - p[0]) < 1 && Math.Abs(c[1] - p[1]) < 1))))
        {
            facet.Remove("outlineMm");
            return;
        }

        facet["outlineMm"] = new JsonArray(shape.Select(p => (JsonNode?)GeometryJson.Array([p[0], p[1]], 1)).ToArray());
    }

    /// <summary>The fold line moved by <paramref name="map"/>, as a signed distance (≥ 0 on the kept side).</summary>
    private static Func<double, double, double> Mapped((double Alpha, double Beta, double Gamma) fold, Func<double[], double[]> map)
    {
        var (al, be, ga) = fold;
        double[] p0 = [al * ga, be * ga];
        var m0 = map(p0);
        var m1 = map([p0[0] - (1000 * be), p0[1] + (1000 * al)]);
        var inside = map([p0[0] + (1000 * al), p0[1] + (1000 * be)]);
        double da = m1[0] - m0[0], db = m1[1] - m0[1];
        var length = Math.Sqrt((da * da) + (db * db));
        double na = -db / length, nb = da / length;
        var sign = (na * (inside[0] - m0[0])) + (nb * (inside[1] - m0[1])) >= 0 ? 1 : -1;
        return (a, b) => sign * ((na * (a - m0[0])) + (nb * (b - m0[1])));
    }

    private static double Num(JsonObject node, string key) => GeometryJson.Number(node, key) ?? 0;
}
