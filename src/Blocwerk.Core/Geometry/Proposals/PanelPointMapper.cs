// <copyright file="PanelPointMapper.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Geometry.View3D;
using Blocwerk.Core.Geometry.Volumes;

namespace Blocwerk.Core.Geometry.Proposals;

/// <summary>
/// Maps a proposal's 3D point into a panel photo, the same way a hold drawn there is mapped the other way: the
/// point is first taken flat along the panel camera's ray onto its facet (as the photo sees it), then through a
/// homography fitted to that panel's nearby placed holds on the same facet. A point with too few anchors nearby
/// or outside the photo is not on that panel ("3D only").
/// </summary>
public static class PanelPointMapper
{
    /// <summary>Fewest nearby placed holds for a local fit.</summary>
    public const int MinAnchors = 8;

    /// <summary>Anchors within this of the point are used, mm.</summary>
    public const double AnchorRadiusMm = 900;

    private const double Margin = 0.01;

    /// <summary>The best panel point, or null.</summary>
    /// <param name="world">The point, world mm.</param>
    /// <param name="sizeMm">Its size, mm.</param>
    /// <param name="facet">Its facet.</param>
    /// <param name="facetId">The facet id.</param>
    /// <param name="panels">Per panel: its camera centre (world mm, null when unknown), anchors and photo size.</param>
    /// <returns>The mapping.</returns>
    public static PanelPoint? Map(
        double[] world,
        double sizeMm,
        FacetFrame facet,
        string facetId,
        IEnumerable<(Guid PanelId, double[]? Camera, IReadOnlyList<PanelAnchor> Anchors, int Width, int Height)> panels)
    {
        (PanelPoint Point, int Anchors)? best = null;
        foreach (var (panelId, camera, anchors, width, height) in panels)
        {
            var (a, b) = Flat(world, facet, camera);
            var near = anchors.Where(x => x.FacetId == facetId && Math.Abs(x.A - a) < AnchorRadiusMm && Math.Abs(x.B - b) < AnchorRadiusMm).ToList();
            if (near.Count < MinAnchors || PlaneHomography.Fit(near.Select(x => new PointCorrespondence(x.A, x.B, x.X, x.Y)).ToList()) is not { } h)
            {
                continue;
            }

            var (x, y) = h.Apply(a, b);
            var (rx, ry) = h.Apply(a + (sizeMm / 2), b);
            var longSide = Math.Max(width, height);
            var radius = Math.Sqrt(Math.Pow((rx - x) * width, 2) + Math.Pow((ry - y) * height, 2)) / Math.Max(1, longSide);
            if (x < Margin || x > 1 - Margin || y < Margin || y > 1 - Margin || !double.IsFinite(radius))
            {
                continue;
            }

            if (best is null || near.Count > best.Value.Anchors)
            {
                best = (new PanelPoint(panelId, Math.Round(x, 4), Math.Round(y, 4), Math.Round(Math.Clamp(radius, 0.003, 0.08), 4)), near.Count);
            }
        }

        return best?.Point;
    }

    /// <summary>The point taken flat onto the facet along the ray from <paramref name="camera"/> (or straight down without one).</summary>
    private static (double A, double B) Flat(double[] world, FacetFrame facet, double[]? camera)
    {
        var p = FacetCloud.Local(facet, world[0], world[1], world[2]);
        if (camera is null)
        {
            return (p.A, p.B);
        }

        var c = FacetCloud.Local(facet, camera[0], camera[1], camera[2]);
        if (c.H <= p.H + 1)
        {
            return (p.A, p.B);
        }

        var t = c.H / (c.H - p.H);
        return (c.A + ((p.A - c.A) * t), c.B + ((p.B - c.B) * t));
    }
}
