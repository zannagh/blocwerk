namespace Blocwerk.Core.Detection.Outlines;

/// <summary>
/// Clips an outline to the room a hold has left: one straight cut per overlapping neighbour, through the
/// neighbour's point nearest to this hold's centre, square to the line between them. A straight cut keeps the
/// result convex where it matters, so it stays within the smoothness limits (following the neighbour's own
/// curve would leave a deep incut that the smoother has to fill in again).
/// </summary>
internal static class ShapeClip
{
    private const int MaxRounds = 8;

    /// <summary>Clips the outline; null when the centre itself is taken or nothing sensible is left.</summary>
    /// <param name="local">The outline as offsets from <paramref name="centre"/>.</param>
    /// <param name="centre">The hold centre.</param>
    /// <param name="obstacles">The claimed footprints.</param>
    /// <returns>The clipped outline as offsets, or null.</returns>
    public static List<P2>? ClipToClear(List<P2> local, P2 centre, ShapeObstacles obstacles)
    {
        var poly = local.Select(p => p + centre).ToList();
        for (int round = 0; round < MaxRounds; round++)
        {
            var blockers = obstacles.OverlappingWith(poly);
            if (blockers.Count == 0)
            {
                return poly.Select(p => p - centre).ToList();
            }

            foreach (var blocker in blockers)
            {
                var cut = ClipAgainst(poly, centre, blocker);
                if (cut is null)
                {
                    return null;
                }

                poly = cut;
            }
        }

        return poly.Select(p => p - centre).ToList();
    }

    private static List<P2>? ClipAgainst(List<P2> poly, P2 centre, P2[] blocker)
    {
        if (ShapeGeometry.Contains(blocker, centre))
        {
            return null;
        }

        var nearest = ShapeGeometry.ClosestOnBoundary(centre, blocker);
        double distance = (centre - nearest).Length;
        if (distance <= HoldShapeOverlapResolver.Tolerance * 2)
        {
            return null;
        }

        var normal = (centre - nearest) * (1 / distance);
        var clipped = ClipHalfPlane(poly, nearest + (normal * HoldShapeOverlapResolver.Tolerance), normal);
        return clipped.Count >= 3 ? clipped : null;
    }

    /// <summary>Sutherland-Hodgman against one half-plane: keeps the points with dot(p - origin, normal) >= 0.</summary>
    private static List<P2> ClipHalfPlane(List<P2> poly, P2 origin, P2 normal)
    {
        var result = new List<P2>();
        for (int i = 0; i < poly.Count; i++)
        {
            var a = poly[i];
            var b = poly[(i + 1) % poly.Count];
            double da = P2.Dot(a - origin, normal);
            double db = P2.Dot(b - origin, normal);
            if (da >= 0)
            {
                result.Add(a);
            }

            if ((da >= 0) != (db >= 0))
            {
                result.Add(a + ((b - a) * (da / (da - db))));
            }
        }

        return result;
    }
}
