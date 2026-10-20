// <copyright file="CloseUpHoles.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Geometry.Footprints;
using Blocwerk.Core.Geometry.View3D;

namespace Blocwerk.Core.Geometry.HybridZoom;

/// <summary>
/// Where a planar close-up must stay transparent: the volumes in its view. The close-up is warped through the facet
/// plane, so a volume standing off it lands displaced against the panel photo's outline; cutting it out lets the
/// panel photo (and the overlay drawn for it) show there while the flat wall around it gets the sharper photo. A hole
/// is the convex hull, in view coordinates, of the footprint and of the footprint lifted to the volume's height as the
/// close-up camera sees it, grown by <see cref="Grow"/> for what the panel camera sees of its sides. Pure: no I/O.
/// </summary>
public static class CloseUpHoles
{
    /// <summary>Growth of each hole about its centre (the panel photo's view of the volume's sides).</summary>
    public const double Grow = 0.15;

    /// <summary>The holes, as polygons in view coordinates (u, v; may reach outside 0..1).</summary>
    /// <param name="volumes">The volumes in the view.</param>
    /// <param name="frame">Their facet.</param>
    /// <param name="camera">The close-up's camera.</param>
    /// <param name="viewToPhoto">The close-up's warp, view → photo px.</param>
    /// <returns>One polygon per volume that projects.</returns>
    public static IReadOnlyList<IReadOnlyList<(double U, double V)>> For(
        IReadOnlyList<VolumeOutline> volumes, FacetFrame frame, SolvedCamera camera, PlaneHomography viewToPhoto)
    {
        if (volumes.Count == 0 || viewToPhoto.Inverse() is not { } photoToView)
        {
            return [];
        }

        var result = new List<IReadOnlyList<(double U, double V)>>();
        foreach (var volume in volumes)
        {
            var points = new List<(double U, double V)>();
            foreach (var (a, b) in volume.Outline)
            {
                foreach (var lift in new[] { 0, Math.Max(0, volume.HeightMm) })
                {
                    if (camera.Project(frame.ToWorld(a, b, lift)) is { } px && photoToView.Apply(px.X, px.Y) is var (u, v)
                        && double.IsFinite(u) && double.IsFinite(v))
                    {
                        points.Add((u, v));
                    }
                }
            }

            var hull = ConvexHull(points);
            if (hull.Count >= 3)
            {
                result.Add(Grown(hull, Grow));
            }
        }

        return result;
    }

    /// <summary>Andrew's monotone chain, counter-clockwise, without repeated end point.</summary>
    /// <param name="points">The points.</param>
    /// <returns>The hull.</returns>
    public static List<(double U, double V)> ConvexHull(IReadOnlyList<(double U, double V)> points)
    {
        var p = points.Distinct().OrderBy(q => q.U).ThenBy(q => q.V).ToList();
        if (p.Count < 3)
        {
            return p;
        }

        var hull = new List<(double U, double V)>();
        foreach (var pass in new[] { p, Enumerable.Reverse(p).ToList() })
        {
            var start = hull.Count;
            foreach (var q in pass)
            {
                while (hull.Count >= start + 2 && Cross(hull[^2], hull[^1], q) <= 0)
                {
                    hull.RemoveAt(hull.Count - 1);
                }

                hull.Add(q);
            }

            hull.RemoveAt(hull.Count - 1);
        }

        return hull;
    }

    private static List<(double U, double V)> Grown(List<(double U, double V)> hull, double grow)
    {
        var cu = hull.Average(q => q.U);
        var cv = hull.Average(q => q.V);
        return [.. hull.Select(q => (cu + ((q.U - cu) * (1 + grow)), cv + ((q.V - cv) * (1 + grow))))];
    }

    private static double Cross((double U, double V) o, (double U, double V) a, (double U, double V) b) =>
        ((a.U - o.U) * (b.V - o.V)) - ((a.V - o.V) * (b.U - o.U));
}
