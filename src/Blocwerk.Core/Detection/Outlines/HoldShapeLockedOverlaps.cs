using Blocwerk.Core.Entities;

namespace Blocwerk.Core.Detection.Outlines;

/// <summary>Finds overlapping pairs among holds the clean-up may not change (they need a hand edit).</summary>
internal static class HoldShapeLockedOverlaps
{
    /// <summary>Overlapping pairs, ordered by id, with the same tolerance as the resolver.</summary>
    /// <param name="locked">The locked holds of one panel.</param>
    /// <returns>The pairs (lower id first).</returns>
    public static List<(Guid A, Guid B)> Find(IReadOnlyList<Hold> locked)
    {
        var items = locked.OrderBy(h => h.Id).Select(Footprint).ToList();
        var pairs = new List<(Guid, Guid)>();
        double tol = HoldShapeOverlapResolver.Tolerance;
        for (int i = 0; i < items.Count; i++)
        {
            for (int j = i + 1; j < items.Count; j++)
            {
                var a = items[i];
                var b = items[j];
                if (a.MaxX + tol >= b.MinX && b.MaxX + tol >= a.MinX && a.MaxY + tol >= b.MinY && b.MaxY + tol >= a.MinY
                    && ShapeGeometry.Distance(a.Poly, b.Poly) < tol)
                {
                    pairs.Add((a.Id, b.Id));
                }
            }
        }

        return pairs;
    }

    private static (Guid Id, P2[] Poly, double MinX, double MinY, double MaxX, double MaxY) Footprint(Hold h)
    {
        P2[] poly = h.ShapePoints is { Count: >= 3 } shape
            ? shape.Select(s => new P2(h.X + s.Dx, h.Y + s.Dy)).ToArray()
            : ShapeObstacles.Circle(new P2(h.X, h.Y), h.Radius);
        return (h.Id, poly, poly.Min(p => p.X), poly.Min(p => p.Y), poly.Max(p => p.X), poly.Max(p => p.Y));
    }
}
