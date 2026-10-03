using Blocwerk.Core.Entities;

namespace Blocwerk.Core.Detection.Outlines;

/// <summary>
/// Turns a traced hold outline into a smooth, plausible one - or rejects it. Pure geometry (no OpenCV).
/// </summary>
/// <remarks>
/// <para>Shapes are offsets from the hold centre, so the centre is the origin. Angles are judged in pixel
/// proportions: pass the photo's width/height as <c>aspect</c> (1 when unknown, which is good enough).</para>
/// <para>Pipeline: drop spikes and razor-thin incuts, clamp deep incuts toward the convex hull, Douglas-Peucker
/// simplify, Chaikin-smooth, then verify with <see cref="IsSmooth"/>. A polygon that already passes the
/// verification is returned untouched, so running the clean-up twice changes nothing. "Radius" in the depth
/// limit is the shape's own equivalent radius (sqrt(area / pi)), not <see cref="Hold.Radius"/>, which can be a
/// stale placeholder.</para>
/// </remarks>
public static class HoldShapeSmoother
{
    /// <summary>A vertex whose two edges enclose less than this is a spike (convex) or a razor incut (concave).</summary>
    public const double MinSpikeAngleDegrees = 30;

    /// <summary>Incuts are limited to this fraction of the equivalent radius (deeper ones are blended toward the hull).</summary>
    public const double MaxConcavityDepth = 0.12;

    /// <summary>Douglas-Peucker tolerance as a fraction of the equivalent radius.</summary>
    public const double SimplifyEpsilon = 0.04;

    /// <summary>Vertex budget after simplification (before smoothing).</summary>
    public const int MaxSimplifiedVertices = 16;

    /// <summary>Chaikin passes (each doubles the vertex count).</summary>
    public const int ChaikinPasses = 2;

    /// <summary>Hard cap on the final vertex count (<see cref="MaxSimplifiedVertices"/> * 2^<see cref="ChaikinPasses"/>).</summary>
    public const int MaxVertices = 64;

    /// <summary>A smooth outline never turns more than this between two consecutive edges.</summary>
    public const double MaxTurnDegrees = 75;

    /// <summary>Minimum area / convex-hull area; less means too many or too deep incuts.</summary>
    public const double MinSolidity = 0.85;

    /// <summary>Maximum perimeter / convex-hull perimeter; more means slits or a ragged edge.</summary>
    public const double MaxPerimeterRatio = 1.15;

    private const double MinSeparation = 1e-5;
    private const double MinEquivalentRadius = 1e-4;

    /// <summary>Smooths an outline, or returns null when it cannot be made plausible (use the plain circle).</summary>
    /// <param name="shape">Offsets from the hold centre.</param>
    /// <param name="aspect">Photo width / height (1 when unknown).</param>
    /// <returns>The smooth outline (rounded to 5 decimals), or null to fall back to the circle.</returns>
    public static List<ShapePoint>? Smooth(IReadOnlyList<ShapePoint> shape, double aspect = 1)
    {
        ArgumentNullException.ThrowIfNull(shape);
        var pts = Prepare(shape, aspect);
        if (pts is null)
        {
            return null;
        }

        if (Verify(pts))
        {
            // Already fine: hand back the input as it is (same order, same digits), so a clean shape never "changes".
            return pts.Count == shape.Count ? shape.Select(p => new ShapePoint { Dx = p.Dx, Dy = p.Dy }).ToList() : FromWork(pts, aspect);
        }

        pts = ShapeSimplify.RemoveSpikes(pts, MinSpikeAngleDegrees);
        if (pts.Count < 3 || RadiusOf(pts) < MinEquivalentRadius)
        {
            return null;
        }

        double radius = RadiusOf(pts);
        pts = ShapeSimplify.ClampConcavity(pts, MaxConcavityDepth * radius);
        pts = ShapeSimplify.Simplify(pts, SimplifyEpsilon * radius, MaxSimplifiedVertices);
        pts = ShapeSimplify.Chaikin(pts, ChaikinPasses);
        return VerifiedRounded(pts, aspect);
    }

    /// <summary>
    /// The 5-decimal outline that gets stored, re-verified AS STORED: a polygon that only passes before rounding
    /// would fail on the next run and be changed again, so it is rejected now.
    /// </summary>
    private static List<ShapePoint>? VerifiedRounded(List<P2> pts, double aspect)
    {
        var rounded = FromWork(pts, aspect);
        var again = Prepare(rounded, aspect);
        return again is not null && Verify(again) ? rounded : null;
    }

    /// <summary>True when the outline already meets every smoothness criterion.</summary>
    /// <param name="shape">Offsets from the hold centre.</param>
    /// <param name="aspect">Photo width / height (1 when unknown).</param>
    /// <returns>Whether it is smooth.</returns>
    public static bool IsSmooth(IReadOnlyList<ShapePoint> shape, double aspect = 1)
    {
        ArgumentNullException.ThrowIfNull(shape);
        var pts = Prepare(shape, aspect);
        return pts is not null && Verify(pts);
    }

    /// <summary>
    /// The criteria: 3..<see cref="MaxVertices"/> vertices, simple (no self-crossing), contains the centre,
    /// solidity and perimeter ratio against the convex hull, and a bounded turning angle.
    /// </summary>
    private static bool Verify(List<P2> pts)
    {
        if (pts.Count < 3 || pts.Count > MaxVertices || RadiusOf(pts) < MinEquivalentRadius)
        {
            return false;
        }

        var hull = ShapeGeometry.ConvexHull(pts);
        if (hull.Count < 3)
        {
            return false;
        }

        return ShapeGeometry.Area(pts) / ShapeGeometry.Area(hull) >= MinSolidity
               && ShapeGeometry.Perimeter(pts) / ShapeGeometry.Perimeter(hull) <= MaxPerimeterRatio
               && ShapeSimplify.MaxTurnDegrees(pts) <= MaxTurnDegrees
               && ShapeGeometry.IsSimple(pts)
               && ShapeGeometry.Contains(pts, new P2(0, 0));
    }

    private static List<P2>? Prepare(IReadOnlyList<ShapePoint> shape, double aspect)
    {
        double a = aspect > 0 ? aspect : 1;
        var pts = new List<P2>();
        foreach (var sp in shape)
        {
            var p = new P2(sp.Dx * a, sp.Dy);
            if (pts.Count == 0 || (p - pts[^1]).Length > MinSeparation)
            {
                pts.Add(p);
            }
        }

        if (pts.Count > 1 && (pts[0] - pts[^1]).Length <= MinSeparation)
        {
            pts.RemoveAt(pts.Count - 1);
        }

        if (pts.Count < 3 || ShapeGeometry.Area(pts) < 1e-12)
        {
            return null;
        }

        if (ShapeGeometry.SignedArea(pts) < 0)
        {
            pts.Reverse();
        }

        return pts;
    }

    private static double RadiusOf(IReadOnlyList<P2> pts) => Math.Sqrt(ShapeGeometry.Area(pts) / Math.PI);

    private static List<ShapePoint> FromWork(List<P2> pts, double aspect)
    {
        double a = aspect > 0 ? aspect : 1;
        return pts.Select(p => new ShapePoint { Dx = Math.Round(p.X / a, 5), Dy = Math.Round(p.Y, 5) }).ToList();
    }
}
