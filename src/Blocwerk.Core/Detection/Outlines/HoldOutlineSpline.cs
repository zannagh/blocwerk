using Blocwerk.Core.Entities;

namespace Blocwerk.Core.Detection.Outlines;

/// <summary>
/// A smooth hold outline: a closed centripetal Catmull-Rom curve through a few control points
/// (<see cref="Hold.ShapeControlPoints"/>). This is the ONE place that turns control points into the polygon
/// stored in <see cref="Hold.ShapePoints"/>, so every reader of a hold's outline (overlays, hit tests, area,
/// colour mask, 3D footprint, the detection service) sees the same smooth shape without knowing about splines.
/// </summary>
/// <remarks>
/// The curve is computed on the stored offsets as they are (fractions of the photo's width and height), so the
/// same control points always give the same polygon, whatever the photo. Centripetal spacing keeps the curve
/// free of loops and cusps between unevenly spaced points.
/// </remarks>
public static class HoldOutlineSpline
{
    /// <summary>A detected outline is reduced to at most this many control points when smoothing is turned on.</summary>
    public const int MaxControlPoints = 16;

    /// <summary>More control points than this are not offered: the polygon would stop getting smoother.</summary>
    public const int MaxEditableControlPoints = 32;

    /// <summary>The smooth polygon has at most this many vertices for up to <see cref="MaxEditableControlPoints"/> control points.</summary>
    public const int MaxOutlinePoints = 96;

    private const int MinSamplesPerSegment = 3;
    private const int MaxSamplesPerSegment = 12;
    private const double Tolerance = 5e-5;
    private const double MinSpacing = 1e-9;

    /// <summary>The smooth closed polygon through <paramref name="control"/>, in the same offset convention.</summary>
    /// <param name="control">The control points, in order around the outline.</param>
    /// <returns>The polygon; it passes through every control point. Fewer than three distinct points come back as they are.</returns>
    public static List<ShapePoint> Densify(IReadOnlyList<ShapePoint> control)
    {
        var points = Distinct(control);
        if (points.Count < 3)
        {
            return [.. control.Select(p => new ShapePoint { Dx = p.Dx, Dy = p.Dy })];
        }

        int samples = Math.Clamp(MaxOutlinePoints / points.Count, MinSamplesPerSegment, MaxSamplesPerSegment);
        var outline = new List<ShapePoint>(points.Count * samples);
        for (int i = 0; i < points.Count; i++)
        {
            var p0 = points[(i + points.Count - 1) % points.Count];
            var p1 = points[i];
            var p2 = points[(i + 1) % points.Count];
            var p3 = points[(i + 2) % points.Count];
            for (int s = 0; s < samples; s++)
            {
                var (x, y) = s == 0 ? p1 : Segment(p0, p1, p2, p3, (double)s / samples);
                outline.Add(new ShapePoint { Dx = Math.Round(x, 5), Dy = Math.Round(y, 5) });
            }
        }

        return outline;
    }

    /// <summary>
    /// Control points for an existing outline: the outline itself when it is small enough, else its
    /// Ramer-Douglas-Peucker simplification to at most <paramref name="maxPoints"/> points.
    /// </summary>
    /// <param name="outline">The outline to take control points from.</param>
    /// <param name="maxPoints">The most control points to keep.</param>
    /// <returns>A detached list of control points.</returns>
    public static List<ShapePoint> ControlPointsFor(IReadOnlyList<ShapePoint> outline, int maxPoints = MaxControlPoints)
    {
        var points = Distinct(outline);
        if (points.Count > maxPoints)
        {
            // Start at 1% of the outline's size, so only real corners are kept; the pass raises it until few enough remain.
            double size = Math.Max(points.Max(p => p.X) - points.Min(p => p.X), points.Max(p => p.Y) - points.Min(p => p.Y));
            points = ShapeSimplify.Simplify(points, size * 0.01, maxPoints);
        }

        return [.. points.Select(p => new ShapePoint { Dx = p.X, Dy = p.Y })];
    }

    /// <summary>
    /// The hold's control points when its stored outline still is their curve, else null. Anything that
    /// rewrote <see cref="Hold.ShapePoints"/> without knowing about control points (a crop, a clean-up, a tilt,
    /// an older version of the app) leaves them behind; they must then not be used to redraw the outline.
    /// </summary>
    /// <param name="hold">The hold.</param>
    /// <returns>The control points to edit, or null when the outline is not (or no longer) a smooth one.</returns>
    public static List<ShapePoint>? CurrentControlPoints(Hold hold)
    {
        if (hold.ShapeControlPoints is not { Count: >= 3 } control || hold.ShapePoints is not { } stored)
        {
            return null;
        }

        bool same = HoldOutlineSplineCache.IsCurve(hold, control, stored, () => IsCurveOf(control, stored));
        return same ? control : null;
    }

    /// <summary>Whether the hold's outline is a smooth one that can still be edited by its control points.</summary>
    /// <param name="hold">The hold.</param>
    /// <returns>True when it is.</returns>
    public static bool IsSmooth(Hold hold) => CurrentControlPoints(hold) is not null;

    /// <summary>Redraws the stored outline from the control points, after they were edited. No-op without control points.</summary>
    /// <param name="hold">The hold.</param>
    public static void Refresh(Hold hold)
    {
        if (hold.ShapeControlPoints is { Count: >= 3 } control)
        {
            hold.ShapePoints = Densify(control);
        }
    }

    /// <summary>
    /// Turns the smooth outline on or off. On: the outline (simplified when it has very many points) becomes
    /// the control points and the stored outline their curve. Off: the control points become the outline again.
    /// </summary>
    /// <param name="hold">The hold; it needs an outline of at least three points.</param>
    /// <param name="smooth">The wanted state.</param>
    /// <returns>False when nothing changed.</returns>
    public static bool SetSmooth(Hold hold, bool smooth)
    {
        var current = CurrentControlPoints(hold);
        if (smooth == (current is not null) || hold.ShapePoints is not { Count: >= 3 } outline)
        {
            return false;
        }

        if (smooth)
        {
            hold.ShapeControlPoints = ControlPointsFor(outline);
            Refresh(hold);
        }
        else
        {
            hold.ShapePoints = current;
            hold.ShapeControlPoints = null;
        }

        return true;
    }

    /// <summary>
    /// What a save stores: the outline as sent, or, when control points come with it, their curve. Control
    /// points never outlive the outline they belong to.
    /// </summary>
    /// <param name="hold">The hold being saved.</param>
    /// <param name="outline">The outline sent, or null for the plain circle.</param>
    /// <param name="control">The control points sent with it, or null for a plain polygon.</param>
    public static void Store(Hold hold, List<ShapePoint>? outline, List<ShapePoint>? control)
    {
        hold.ShapePoints = Outline(outline, control);
        hold.ShapeControlPoints = outline is not null && control is { Count: >= 3 } ? control : null;
    }

    /// <summary>The outline <see cref="Store"/> would store for these two.</summary>
    /// <param name="outline">The outline sent, or null for the plain circle.</param>
    /// <param name="control">The control points sent with it, or null for a plain polygon.</param>
    /// <returns>The curve through the control points when there are any, else the outline as sent.</returns>
    public static List<ShapePoint>? Outline(List<ShapePoint>? outline, List<ShapePoint>? control) =>
        outline is not null && control is { Count: >= 3 } ? Densify(control) : outline;

    private static bool IsCurveOf(IReadOnlyList<ShapePoint> control, IReadOnlyList<ShapePoint> stored)
    {
        var expected = Densify(control);
        return expected.Count == stored.Count
            && expected.Zip(stored).All(p => Math.Abs(p.First.Dx - p.Second.Dx) <= Tolerance && Math.Abs(p.First.Dy - p.Second.Dy) <= Tolerance);
    }

    private static List<P2> Distinct(IReadOnlyList<ShapePoint> points)
    {
        var distinct = new List<P2>(points.Count);
        foreach (var point in points)
        {
            var p = new P2(point.Dx, point.Dy);
            if (distinct.Count == 0 || Distance(distinct[^1], p) > MinSpacing)
            {
                distinct.Add(p);
            }
        }

        if (distinct.Count > 1 && Distance(distinct[0], distinct[^1]) <= MinSpacing)
        {
            distinct.RemoveAt(distinct.Count - 1);
        }

        return distinct;
    }

    /// <summary>The point at <paramref name="u"/> (0..1) between p1 and p2, by the Barry-Goldman pyramid with centripetal knots.</summary>
    private static P2 Segment(P2 p0, P2 p1, P2 p2, P2 p3, double u)
    {
        double t1 = Math.Sqrt(Distance(p0, p1));
        double t2 = t1 + Math.Sqrt(Distance(p1, p2));
        double t3 = t2 + Math.Sqrt(Distance(p2, p3));
        double t = t1 + ((t2 - t1) * u);
        var a1 = Blend(p0, p1, 0, t1, t);
        var a2 = Blend(p1, p2, t1, t2, t);
        var a3 = Blend(p2, p3, t2, t3, t);
        var b1 = Blend(a1, a2, 0, t2, t);
        var b2 = Blend(a2, a3, t1, t3, t);
        return Blend(b1, b2, t1, t2, t);
    }

    private static P2 Blend(P2 a, P2 b, double ta, double tb, double t)
    {
        double w = (t - ta) / (tb - ta);
        return new P2(a.X + ((b.X - a.X) * w), a.Y + ((b.Y - a.Y) * w));
    }

    private static double Distance(P2 a, P2 b) => Math.Sqrt(((a.X - b.X) * (a.X - b.X)) + ((a.Y - b.Y) * (a.Y - b.Y)));
}
