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
    /// The shared hinge edge between two polygons, or null when they share no edge. The two
    /// returned points are taken from <paramref name="a"/> so callers have one canonical edge.
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

    private static bool Close(ShapePoint p, ShapePoint q, double tolSq)
    {
        var dx = p.Dx - q.Dx;
        var dy = p.Dy - q.Dy;
        return ((dx * dx) + (dy * dy)) <= tolSq;
    }
}
