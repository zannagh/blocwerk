// <copyright file="HoldFootprintEstimator.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Geometry.View3D;

/// <summary>
/// Turns photo silhouettes of a hold into its contact footprint on the facet. A hold standing h mm off
/// the wall projects onto the facet smeared AWAY from the camera by about h·tan θ (θ = view angle to the
/// facet normal), so one steep view stretches it along the fall line. Silhouettes from several
/// well-separated views are smeared in different directions: intersecting them (a visual hull on the
/// plane) keeps what every view agrees on — the footprint. With a single usable view the silhouette is
/// shortened on its far side by an estimated protrusion instead (approximate).
/// </summary>
public static class HoldFootprintEstimator
{
    /// <summary>Views need at least this angle between their rays to the hold to be intersected.</summary>
    public const double MinSpreadDeg = 15;

    /// <summary>Protrusion estimate for the single-view correction, as a fraction of the across-view width.</summary>
    public const double HeightPerWidth = 0.35;

    /// <summary>A view whose silhouette area differs from the primary by more than this factor is a failed trace.</summary>
    public const double MaxAreaRatio = 3;

    /// <summary>An intersection keeping less than this share of the smallest view is a misregistration.</summary>
    public const double MinKeptShare = 0.3;

    /// <summary>Share of the views that must cover a plane cell for it to stay in the footprint.</summary>
    public const double Quorum = 0.7;

    /// <summary>Grid resolution of the consensus, in cells across the primary silhouette.</summary>
    public const int GridCells = 40;

    /// <summary>Steepest view angle the single-view correction trusts (tan grows without bound).</summary>
    public const double MaxCorrectedThetaDeg = 75;

    /// <summary>Estimates the footprint of a hold.</summary>
    /// <param name="frame">The hold's facet.</param>
    /// <param name="centre">The hold's stored plane centre; the result is relative to it.</param>
    /// <param name="primary">The silhouette the hold was traced in (its panel photo).</param>
    /// <param name="others">Silhouettes from other photos that see the hold.</param>
    /// <param name="outlineKey">The outline key to stamp.</param>
    /// <returns>The footprint, or null when even the correction cannot be applied.</returns>
    public static HoldFootprint? Estimate(
        FacetFrame frame,
        (double A, double B) centre,
        FootprintView primary,
        IReadOnlyList<FootprintView> others,
        string outlineKey)
    {
        var primaryArea = PlanePolygon.Area(primary.Silhouette);
        if (primary.Silhouette.Count < 3 || primaryArea < 1)
        {
            return null;
        }

        var usable = others.Where(v => Agrees(v, primary, primaryArea)).ToList();
        var views = new List<FootprintView> { primary };
        views.AddRange(usable);
        var spread = Spread(frame, centre, views);
        if (views.Count >= 2 && spread >= MinSpreadDeg && Intersect(views) is { } ring)
        {
            return Finish(HoldFootprintSource.MultiView, views.Count, spread, null, outlineKey, ring, centre);
        }

        return Corrected(frame, centre, primary, outlineKey);
    }

    /// <summary>The single-view correction of <paramref name="view"/>, or null when it cannot be applied.</summary>
    /// <param name="frame">The facet.</param>
    /// <param name="centre">The hold's plane centre.</param>
    /// <param name="view">The only view.</param>
    /// <param name="outlineKey">The outline key to stamp.</param>
    /// <returns>The approximate footprint.</returns>
    public static HoldFootprint? Corrected(FacetFrame frame, (double A, double B) centre, FootprintView view, string outlineKey)
    {
        if (ViewOf(frame, centre, view.CameraMm) is not { } v || view.Silhouette.Count < 3)
        {
            return null;
        }

        var (dx, dy) = v.Dir;
        var (near, far) = PlanePolygon.Extent(view.Silhouette, dx, dy);
        var (acrossMin, acrossMax) = PlanePolygon.Extent(view.Silhouette, -dy, dx);
        var length = far - near;
        var height = HeightPerWidth * (acrossMax - acrossMin);
        var theta = Math.Min(v.ThetaDeg, MaxCorrectedThetaDeg) * Math.PI / 180;
        var smear = Math.Min(height * Math.Tan(theta), 0.5 * length);
        if (length < 1e-6)
        {
            return null;
        }

        // Shorten along the view direction, anchored on the camera-facing (near) side.
        var k = (length - smear) / length;
        var ring = view.Silhouette
            .Select(p =>
            {
                var s = (p.A * dx) + (p.B * dy) - near;
                var shift = s * (k - 1);
                return (p.A + (shift * dx), p.B + (shift * dy));
            })
            .ToList();
        return Finish(HoldFootprintSource.SingleViewCorrected, 1, 0, Math.Round(height, 1), outlineKey, ring, centre);
    }

    /// <summary>
    /// The view angle θ (to the facet normal, degrees) and the unit in-plane direction the protrusion
    /// smears toward (away from the camera), or null when the camera is behind the facet.
    /// </summary>
    /// <param name="frame">The facet.</param>
    /// <param name="at">The plane point looked at.</param>
    /// <param name="camera">The camera centre in world mm.</param>
    /// <returns>θ and direction.</returns>
    public static (double ThetaDeg, (double A, double B) Dir)? ViewOf(FacetFrame frame, (double A, double B) at, double[] camera)
    {
        var ray = Ray(frame, at, camera);
        var cos = -Dot(ray, frame.Normal);
        var da = Dot(ray, frame.U);
        var db = Dot(ray, frame.V);
        var len = Math.Sqrt((da * da) + (db * db));
        if (cos <= 0.02 || len < 1e-9)
        {
            return null;
        }

        return (Math.Acos(Math.Min(1, cos)) * 180 / Math.PI, (da / len, db / len));
    }

    private static bool Agrees(FootprintView view, FootprintView primary, double primaryArea)
    {
        var area = PlanePolygon.Area(view.Silhouette);
        if (view.Silhouette.Count < 3 || area * MaxAreaRatio < primaryArea || area > primaryArea * MaxAreaRatio)
        {
            return false;
        }

        var overlap = PlanePolygon.ClipConvex(primary.Silhouette, PlanePolygon.ConvexHull(view.Silhouette));
        return overlap.Count >= 3 && PlanePolygon.Area(overlap) >= MinKeptShare * Math.Min(area, primaryArea);
    }

    /// <summary>
    /// A consensus visual hull: the primary silhouette clipped to the hull of the plane cells that at least
    /// <see cref="Quorum"/> of the views cover. One mis-traced or slightly misregistered view then trims
    /// nothing on its own, while the smear, which each view casts in its own direction, is still cut.
    /// Null when too little survives.
    /// </summary>
    private static List<(double A, double B)>? Intersect(List<FootprintView> views)
    {
        var hulls = views.Select(v => PlanePolygon.ConvexHull(v.Silhouette)).ToList();
        var need = Math.Max(2, (int)Math.Ceiling(Quorum * views.Count));
        var primary = views[0].Silhouette;
        var (aMin, aMax) = PlanePolygon.Extent(primary, 1, 0);
        var (bMin, bMax) = PlanePolygon.Extent(primary, 0, 1);
        var step = Math.Max(0.5, Math.Max(aMax - aMin, bMax - bMin) / GridCells);
        var kept = new List<(double A, double B)>();
        for (var a = aMin + (step / 2); a < aMax; a += step)
        {
            for (var b = bMin + (step / 2); b < bMax; b += step)
            {
                if (PlanePolygon.Contains(hulls[0], (a, b)) && hulls.Count(h => PlanePolygon.Contains(h, (a, b))) >= need)
                {
                    kept.Add((a, b));
                }
            }
        }

        if (kept.Count < 3)
        {
            return null;
        }

        var half = step / 2;
        var cells = kept.SelectMany(p => new[] { (p.A - half, p.B - half), (p.A + half, p.B - half), (p.A + half, p.B + half), (p.A - half, p.B + half) });
        var ring = PlanePolygon.ClipConvex(primary, PlanePolygon.ConvexHull(cells));
        var smallest = views.Min(v => PlanePolygon.Area(v.Silhouette));
        return ring.Count >= 3 && PlanePolygon.Area(ring) >= MinKeptShare * smallest ? ring : null;
    }

    private static double Spread(FacetFrame frame, (double A, double B) centre, List<FootprintView> views)
    {
        var rays = views.Select(v => Ray(frame, centre, v.CameraMm)).ToList();
        var best = 0.0;
        for (var i = 0; i < rays.Count; i++)
        {
            for (var j = i + 1; j < rays.Count; j++)
            {
                best = Math.Max(best, Math.Acos(Math.Clamp(Dot(rays[i], rays[j]), -1, 1)) * 180 / Math.PI);
            }
        }

        return best;
    }

    /// <summary>Unit ray from the camera to the plane point.</summary>
    private static double[] Ray(FacetFrame frame, (double A, double B) at, double[] camera)
    {
        var p = frame.ToWorld(at.A, at.B);
        var r = new[] { p[0] - camera[0], p[1] - camera[1], p[2] - camera[2] };
        var len = Math.Sqrt(Dot(r, r));
        return len < 1e-9 ? [0, 0, 0] : [r[0] / len, r[1] / len, r[2] / len];
    }

    private static double Dot(double[] a, double[] b) => (a[0] * b[0]) + (a[1] * b[1]) + (a[2] * b[2]);

    private static HoldFootprint Finish(
        HoldFootprintSource source, int views, double spread, double? height, string key, List<(double A, double B)> ring, (double A, double B) centre)
    {
        var rel = ring.Select(p => (p.A - centre.A, p.B - centre.B)).ToList();
        var capped = PolygonSimplifier.Cap(rel, HoldShapeProjector.MaxOutlineVertices)
            .Select(p => new[] { Math.Round(p.A, 1), Math.Round(p.B, 1) })
            .ToList();
        return new HoldFootprint(source, views, Math.Round(spread, 1), height, key, capped);
    }
}
