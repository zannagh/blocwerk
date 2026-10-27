// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Entities;
using Blocwerk.Core.Geometry.Footprints;
using Blocwerk.Core.Geometry.TextureRegistration;
using Blocwerk.Core.Geometry.View3D;

namespace Blocwerk.Core.Geometry.Surface;

/// <summary>
/// Places a hold from its registered panel photo onto the photo-real surface (owner D47-D49, "S⊥"): rays through the
/// hold's centre and through its outline shrunk to 30 % and 60 % meet the surface (<see cref="SplatSurface"/>) near the
/// first facet they cross; each hit is dropped onto the nearest facet along its normal, and the median of the hits on the
/// majority facet is the hold's base. Dropping along the normal removes the view-dependent parallax a plane cast has.
/// </summary>
public static class SurfaceCaster
{
    /// <summary>The surface is searched from this far in front of the first facet crossed, mm (holds and volumes stand out).</summary>
    public const double InFrontMm = 300;

    /// <summary>... to this far behind it, mm (the splat sits a few mm off the measured planes).</summary>
    public const double BehindMm = 60;

    /// <summary>Outline vertices used per scale.</summary>
    public const int VerticesPerScale = 8;

    /// <summary>Outline scales the interior rays go through.</summary>
    public static readonly double[] Scales = [0.3, 0.6];

    /// <summary>Casts <paramref name="hold"/> from <paramref name="camera"/>; null when too few rays hit the surface.</summary>
    public static SurfaceCast? Cast(
        Hold hold, SolvedCamera camera, IReadOnlyDictionary<string, (FacetFrame Frame, PlaneRectMm Extent)> facets, SplatSurface surface)
    {
        var rays = Rays(hold);
        var hits = new List<(string Facet, double A, double B)>();
        foreach (var (x, y) in rays)
        {
            if (Hit(camera, x * camera.Width, y * camera.Height, facets, surface) is { } hit)
            {
                hits.Add(hit);
            }
        }

        var top = hits.GroupBy(h => h.Facet).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).FirstOrDefault();
        if (top is null || top.Count() < Math.Max(1, rays.Count / 3))
        {
            return null;
        }

        var frame = facets[top.Key].Frame;
        var dir = camera.RayDirection(hold.X * camera.Width, hold.Y * camera.Height);
        var theta = Math.Acos(Math.Min(1, Math.Abs(Dot(dir, frame.Normal)))) * 180 / Math.PI;
        return new SurfaceCast(top.Key, Median(top.Select(h => h.A)), Median(top.Select(h => h.B)), theta, top.Count());
    }

    /// <summary>The hold's outline seen from <paramref name="camera"/>, on <paramref name="frame"/>'s plane (mm), or null.</summary>
    public static List<(double A, double B)>? Silhouette(Hold hold, SolvedCamera camera, FacetFrame frame)
    {
        var ring = new List<(double A, double B)>();
        foreach (var p in hold.ShapePoints ?? [])
        {
            if (camera.PixelToPlane(frame, (hold.X + p.Dx) * camera.Width, (hold.Y + p.Dy) * camera.Height) is not { } ab)
            {
                return null;
            }

            ring.Add(ab);
        }

        return ring.Count >= 3 ? ring : null;
    }

    /// <summary>The normalised photo points the rays go through: the centre, then the shrunk outline.</summary>
    internal static List<(double X, double Y)> Rays(Hold hold)
    {
        var rays = new List<(double X, double Y)> { (hold.X, hold.Y) };
        var outline = hold.ShapePoints ?? [];
        var step = Math.Max(1, outline.Count / VerticesPerScale);
        foreach (var scale in Scales)
        {
            for (var i = 0; i < outline.Count; i += step)
            {
                rays.Add((hold.X + (scale * outline[i].Dx), hold.Y + (scale * outline[i].Dy)));
            }
        }

        return rays;
    }

    private static (string Facet, double A, double B)? Hit(
        SolvedCamera camera, double px, double py, IReadOnlyDictionary<string, (FacetFrame Frame, PlaneRectMm Extent)> facets, SplatSurface surface)
    {
        var centre = camera.Centre;
        var dir = camera.RayDirection(px, py);
        var first = double.PositiveInfinity;
        foreach (var (frame, extent) in facets.Values)
        {
            var denom = Dot(dir, frame.Normal);
            var s = Math.Abs(denom) < 1e-9 ? double.NaN : Dot(Sub(frame.Origin, centre), frame.Normal) / denom;
            if (s > 0 && s < first && Inside(frame, extent, Add(centre, dir, s)))
            {
                first = s;
            }
        }

        if (double.IsInfinity(first) || surface.Hit(centre, dir, first - InFrontMm, first + BehindMm) is not { } p)
        {
            return null;
        }

        return Drop(p, facets);
    }

    /// <summary>The point dropped along the normal onto the nearest facet whose extent holds it.</summary>
    private static (string Facet, double A, double B)? Drop(double[] p, IReadOnlyDictionary<string, (FacetFrame Frame, PlaneRectMm Extent)> facets)
    {
        (string Facet, double A, double B)? best = null;
        var bestDistance = double.PositiveInfinity;
        foreach (var (id, (frame, extent)) in facets)
        {
            var rel = Sub(p, frame.Origin);
            var distance = Math.Abs(Dot(rel, frame.Normal));
            if (distance < bestDistance && Inside(frame, extent, p))
            {
                (best, bestDistance) = ((id, Dot(rel, frame.U), Dot(rel, frame.V)), distance);
            }
        }

        return best;
    }

    private static bool Inside(FacetFrame frame, PlaneRectMm e, double[] p)
    {
        var rel = Sub(p, frame.Origin);
        double a = Dot(rel, frame.U), b = Dot(rel, frame.V), m = HoldTexturePlacer.ExtentMarginMm;
        return a >= e.AMin - m && a <= e.AMax + m && b >= e.BMin - m && b <= e.BMax + m;
    }

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        var mid = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
    }

    private static double Dot(double[] a, double[] b) => (a[0] * b[0]) + (a[1] * b[1]) + (a[2] * b[2]);

    private static double[] Sub(double[] a, double[] b) => [a[0] - b[0], a[1] - b[1], a[2] - b[2]];

    private static double[] Add(double[] a, double[] d, double s) => [a[0] + (s * d[0]), a[1] + (s * d[1]), a[2] + (s * d[2])];
}
