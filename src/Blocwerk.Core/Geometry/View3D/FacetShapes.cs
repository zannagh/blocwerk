// <copyright file="FacetShapes.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Geometry.View3D;

/// <summary>
/// Where a facet really is, for ray casts onto the model: the outline the 3D view draws (<see cref="Wall3DFacetOutlines"/>:
/// a triangle segment cut along its hypotenuse, edges trimmed at a neighbour's seam), else its extent rectangle. A ray
/// through the cut-away half of a triangle's rectangle passes on to whatever is behind it.
/// </summary>
public static class FacetShapes
{
    /// <summary>The outlines of the model's facets that are not their whole extent rectangle, by facet id.</summary>
    /// <param name="doc">The model.</param>
    /// <param name="triangles">The marker plan's triangle segments (<see cref="Wall3DFacetOutlines.PlanTriangles"/>), if any.</param>
    /// <returns>Per facet id, its convex outline, [a, b] mm.</returns>
    public static IReadOnlyDictionary<string, IReadOnlyList<double[]>> Outlines(
        WallGeometryDocument doc, IReadOnlyDictionary<int, PlanTriangle>? triangles)
    {
        var facets = new List<Wall3DFacet>();
        foreach (var segment in doc.Segments)
        {
            foreach (var facet in segment.Facets)
            {
                if (!string.IsNullOrEmpty(facet.Id) && FacetFrame.From(facet) is { } frame && facet.ExtentMm is { Width: > 0, Height: > 0 } e)
                {
                    facets.Add(new Wall3DFacet(
                        facet.Id, segment.Index, facet.Id, frame.Origin, frame.U, frame.V, frame.Normal, frame.Corners(e), e, null));
                }
            }
        }

        return Wall3DFacetOutlines.Apply(facets, doc, triangles)
            .Where(f => f.Outline is { Count: >= 3 })
            .GroupBy(f => f.Id, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Outline!, StringComparer.Ordinal);
    }

    /// <summary>Whether (a, b) lies on the facet, or within <paramref name="marginMm"/> of it.</summary>
    /// <param name="outline">The facet's outline, or null for its whole extent.</param>
    /// <param name="extent">Its extent rectangle.</param>
    /// <param name="a">Along u, mm.</param>
    /// <param name="b">Along v, mm.</param>
    /// <param name="marginMm">How far outside it still counts, mm.</param>
    /// <returns>True on (or near) the facet.</returns>
    public static bool Covers(IReadOnlyList<double[]>? outline, PlaneRectMm extent, double a, double b, double marginMm)
    {
        if (outline is not { Count: >= 3 })
        {
            return a >= extent.AMin - marginMm && a <= extent.AMax + marginMm && b >= extent.BMin - marginMm && b <= extent.BMax + marginMm;
        }

        var ring = outline.Select(p => (p[0], p[1])).ToList();
        return PlanePolygon.Contains(ring, (a, b)) || PlanePolygon.DistanceTo(ring, (a, b)) <= marginMm;
    }
}
