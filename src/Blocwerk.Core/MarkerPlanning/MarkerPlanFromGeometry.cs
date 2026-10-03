// <copyright file="MarkerPlanFromGeometry.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Geometry;

namespace Blocwerk.Core.MarkerPlanning;

/// <summary>
/// Builds a marker plan from a solved <c>wall-geometry.json</c>, so a wall that already carries
/// markers gets a plan without being redrawn: one rectangle per facet (its solved extent), every
/// marker at its measured plane position and the document's printed size, overhang/yaw from the
/// solve, and the net stitched together from the facets' 3D frames (see
/// <see cref="GeometryAttachments"/>). Ids are kept verbatim — whatever scheme they followed.
/// </summary>
/// <remarks>
/// Facet extents are marker spans plus a margin, not the real outline, and every facet becomes a
/// rectangle (a triangular panel comes out as its bounding rectangle): the result is a starting
/// point the owner corrects in the planner, not a survey.
/// </remarks>
public static class MarkerPlanFromGeometry
{
    private const double FallbackMarginMm = 50;

    /// <summary>Converts <paramref name="document"/> into a plan photographed as <paramref name="photo"/>.</summary>
    public static MarkerPlan Build(WallGeometryDocument document, PhotoSetup photo)
    {
        var facets = CollectFacets(document);
        var attachments = GeometryAttachments.Solve(facets);
        var segments = facets
            .Select((f, i) => new PlanSegment(
                f.Index, f.Name, SegmentShape.Rectangle, Round(f.Extent.Width), Round(f.Extent.Height), TriangleCorner.BottomLeft,
                Round(f.OverhangDeg), Round(f.YawDeg), attachments[i]))
            .ToList();

        var markers = new List<PlanMarker>();
        foreach (var marker in document.Markers.OrderBy(m => m.Id))
        {
            var facet = facets.FirstOrDefault(f => f.FacetId == marker.Facet);
            if (facet is null || marker.CornersPlaneMm.Count == 0)
            {
                continue;
            }

            var cx = marker.CornersPlaneMm.Average(c => c[0]) - facet.Extent.AMin;
            var cy = marker.CornersPlaneMm.Average(c => c[1]) - facet.Extent.BMin;
            markers.Add(new PlanMarker(marker.Id, facet.Index, Round(cx), Round(cy), marker.SizeMm ?? document.MarkerSizeMm, RoleOf(marker.Role)));
        }

        return new MarkerPlan(MarkerPlan.CurrentSchemaVersion, document.Dictionary, photo, segments, markers);
    }

    private static List<GeometryFacet> CollectFacets(WallGeometryDocument document)
    {
        var facets = new List<GeometryFacet>();
        var nextIndex = document.Segments.Count == 0 ? 0 : document.Segments.Max(s => s.Index) + 1;
        foreach (var segment in document.Segments)
        {
            foreach (var facet in segment.Facets)
            {
                var index = segment.Facets.Count == 1 ? segment.Index : nextIndex++;
                var name = segment.Name ?? $"segment {segment.Index}";
                if (segment.Facets.Count > 1)
                {
                    name = $"{name} ({facet.Id})";
                }

                var extent = facet.ExtentMm ?? ExtentFromMarkers(document, facet.Id);
                var overhang = facet.MeasuredAngleDeg ?? segment.MeasuredAngleDeg ?? segment.DeclaredAngleDeg ?? 0;
                facets.Add(new GeometryFacet(index, name, facet.Id, extent, overhang, facet.YawDeg ?? 0, facet.Origin, facet.U, facet.V));
            }
        }

        return facets;
    }

    private static PlaneRectMm ExtentFromMarkers(WallGeometryDocument document, string facetId)
    {
        var corners = document.Markers.Where(m => m.Facet == facetId).SelectMany(m => m.CornersPlaneMm).Select(c => (c[0], c[1]));
        var bounds = PlaneRectMm.Bounds(corners) ?? new PlaneRectMm(0, 1000, 0, 1000);
        return new PlaneRectMm(
            bounds.AMin - FallbackMarginMm, bounds.AMax + FallbackMarginMm, bounds.BMin - FallbackMarginMm, bounds.BMax + FallbackMarginMm);
    }

    private static MarkerRole RoleOf(string? role) =>
        role is "TL" or "TR" or "BR" or "BL" ? MarkerRole.Corner : MarkerRole.Filler;

    private static double Round(double value) => Math.Round(value, 1);
}

/// <summary>A facet of a solved geometry, flattened for plan building.</summary>
internal sealed record GeometryFacet(
    int Index, string Name, string FacetId, PlaneRectMm Extent, double OverhangDeg, double YawDeg, double[]? Origin, double[]? U, double[]? V);
