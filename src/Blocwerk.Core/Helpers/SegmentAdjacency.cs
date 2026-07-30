using Blocwerk.Core.Entities;

namespace Blocwerk.Core.Helpers;

/// <summary>
/// Finds the hinge two panels fold along. Two segments are adjacent when one edge of each
/// polygon is (nearly) the same segment in photo space; that shared edge is the hinge the
/// schematic keeps coincident when it lays the panels out.
/// </summary>
public static class SegmentAdjacency
{
    /// <summary>
    /// Default endpoint tolerance in normalized photo units. Editors rarely place a shared edge
    /// bit-exactly, so a small slack (0.5% of the wall) still counts two edges as one hinge.
    /// </summary>
    public const double DefaultTolerance = 5e-3;

    /// <summary>
    /// Perpendicular slack (2% of the wall) for treating two edges as lying on one line. Adjacent
    /// segments are usually drawn as independent many-point polygons whose vertices never coincide,
    /// so the two edges along their common boundary are collinear and overlapping rather than
    /// vertex-identical; without this they never fold and the panels drift apart.
    /// </summary>
    public const double CollinearDistance = 2e-2;

    /// <summary>Shared run, as a fraction of the wall, below which a collinear overlap is ignored.</summary>
    public const double MinOverlap = 3e-2;

    /// <summary>
    /// The shared hinge edge between two polygons, or null when they share no edge. The two
    /// returned points are taken from <paramref name="a"/> so callers have one canonical edge.
    /// An exact vertex match wins; failing that, a collinear overlapping run counts as the hinge.
    /// </summary>
    public static (ShapePoint E0, ShapePoint E1)? SharedEdge(
        IReadOnlyList<ShapePoint>? a,
        IReadOnlyList<ShapePoint>? b,
        double tolerance = DefaultTolerance)
    {
        if (a == null || b == null || a.Count < 2 || b.Count < 2)
        {
            return null;
        }

        return ExactSharedEdge(a, b, tolerance) ?? CollinearSharedEdge(a, b);
    }

    private static (ShapePoint E0, ShapePoint E1)? ExactSharedEdge(
        IReadOnlyList<ShapePoint> a, IReadOnlyList<ShapePoint> b, double tolerance)
    {
        var tolSq = tolerance * tolerance;
        for (var i = 0; i < a.Count; i++)
        {
            var a0 = a[i];
            var a1 = a[(i + 1) % a.Count];
            for (var j = 0; j < b.Count; j++)
            {
                var b0 = b[j];
                var b1 = b[(j + 1) % b.Count];
                if ((Close(a0, b0, tolSq) && Close(a1, b1, tolSq)) ||
                    (Close(a0, b1, tolSq) && Close(a1, b0, tolSq)))
                {
                    return (a0, a1);
                }
            }
        }

        return null;
    }

    /// <summary>
    /// The overlapping run of the first pair of a- and b-edges that lie on a common line. The
    /// returned points sit on <paramref name="a"/>'s edge so the hinge stays in one canonical frame.
    /// </summary>
    private static (ShapePoint E0, ShapePoint E1)? CollinearSharedEdge(
        IReadOnlyList<ShapePoint> a, IReadOnlyList<ShapePoint> b)
    {
        for (var i = 0; i < a.Count; i++)
        {
            var a0 = a[i];
            var a1 = a[(i + 1) % a.Count];
            var dx = a1.Dx - a0.Dx;
            var dy = a1.Dy - a0.Dy;
            var lenSq = (dx * dx) + (dy * dy);
            if (lenSq < 1e-12)
            {
                continue;
            }

            var len = Math.Sqrt(lenSq);
            for (var j = 0; j < b.Count; j++)
            {
                var overlap = OverlapOnEdge(a0, dx, dy, lenSq, len, b[j], b[(j + 1) % b.Count]);
                if (overlap != null)
                {
                    var (lo, hi) = overlap.Value;
                    return (
                        new ShapePoint { Dx = a0.Dx + (dx * lo), Dy = a0.Dy + (dy * lo) },
                        new ShapePoint { Dx = a0.Dx + (dx * hi), Dy = a0.Dy + (dy * hi) });
                }
            }
        }

        return null;
    }

    /// <summary>
    /// The [lo, hi] sub-interval of edge a0..a0+(dx,dy) that edge b0..b1 covers, when b lies on
    /// a's line and their overlap is long enough; null otherwise.
    /// </summary>
    private static (double Lo, double Hi)? OverlapOnEdge(
        ShapePoint a0, double dx, double dy, double lenSq, double len, ShapePoint b0, ShapePoint b1)
    {
        if (PerpDistance(a0, dx, dy, len, b0) > CollinearDistance ||
            PerpDistance(a0, dx, dy, len, b1) > CollinearDistance)
        {
            return null;
        }

        var t0 = (((b0.Dx - a0.Dx) * dx) + ((b0.Dy - a0.Dy) * dy)) / lenSq;
        var t1 = (((b1.Dx - a0.Dx) * dx) + ((b1.Dy - a0.Dy) * dy)) / lenSq;
        var lo = Math.Max(0.0, Math.Min(t0, t1));
        var hi = Math.Min(1.0, Math.Max(t0, t1));
        if ((hi - lo) * len < MinOverlap)
        {
            return null;
        }

        return (lo, hi);
    }

    private static double PerpDistance(ShapePoint a0, double dx, double dy, double len, ShapePoint p) =>
        Math.Abs((dx * (p.Dy - a0.Dy)) - (dy * (p.Dx - a0.Dx))) / len;

    private static bool Close(ShapePoint p, ShapePoint q, double tolSq)
    {
        var dx = p.Dx - q.Dx;
        var dy = p.Dy - q.Dy;
        return ((dx * dx) + (dy * dy)) <= tolSq;
    }
}
