namespace Blocwerk.Core.Detection.Outlines;

/// <summary>The individual clean-up passes of <see cref="HoldShapeSmoother"/>, on counter-clockwise polygons.</summary>
internal static class ShapeSimplify
{
    /// <summary>Removes vertices whose two edges enclose less than <paramref name="minAngleDegrees"/> (spikes and sharp incuts).</summary>
    public static List<P2> RemoveSpikes(List<P2> poly, double minAngleDegrees)
    {
        var pts = new List<P2>(poly);
        while (pts.Count > 3)
        {
            int worst = -1;
            double worstAngle = minAngleDegrees;
            for (int i = 0; i < pts.Count; i++)
            {
                double angle = AngleBetween(pts[(i + pts.Count - 1) % pts.Count] - pts[i], pts[(i + 1) % pts.Count] - pts[i]);
                if (angle < worstAngle)
                {
                    worstAngle = angle;
                    worst = i;
                }
            }

            if (worst < 0)
            {
                break;
            }

            pts.RemoveAt(worst);
        }

        return pts;
    }

    /// <summary>
    /// Pulls every vertex that lies deeper than <paramref name="maxDepth"/> inside the convex hull back to
    /// exactly that depth, i.e. blends deep incuts toward the hull.
    /// </summary>
    public static List<P2> ClampConcavity(List<P2> poly, double maxDepth)
    {
        var hull = ShapeGeometry.ConvexHull(poly);
        if (hull.Count < 3)
        {
            return poly;
        }

        return poly.Select(p =>
        {
            var q = ShapeGeometry.ClosestOnBoundary(p, hull);
            double depth = (p - q).Length;
            return depth > maxDepth ? q + ((p - q) * (maxDepth / depth)) : p;
        }).ToList();
    }

    /// <summary>Largest direction change (degrees) between two consecutive edges.</summary>
    public static double MaxTurnDegrees(IReadOnlyList<P2> poly)
    {
        double max = 0;
        int n = poly.Count;
        for (int i = 0; i < n; i++)
        {
            var e1 = poly[i] - poly[(i + n - 1) % n];
            var e2 = poly[(i + 1) % n] - poly[i];
            max = Math.Max(max, AngleBetween(e1, e2));
        }

        return max;
    }

    /// <summary>
    /// Closed Douglas-Peucker: splits the ring at vertex 0 and the vertex farthest from it, simplifies both
    /// chains with <paramref name="epsilon"/>, and keeps raising epsilon until at most <paramref name="maxVertices"/> remain.
    /// </summary>
    public static List<P2> Simplify(List<P2> poly, double epsilon, int maxVertices)
    {
        if (poly.Count <= 3)
        {
            return poly;
        }

        int far = Enumerable.Range(1, poly.Count - 1).MaxBy(i => (poly[i] - poly[0]).Length);
        var first = poly.Take(far + 1).ToList();
        var second = poly.Skip(far).Append(poly[0]).ToList();
        for (int attempt = 0; attempt < 24; attempt++)
        {
            var keep = new List<P2>();
            DouglasPeucker(first, epsilon, keep);
            DouglasPeucker(second, epsilon, keep);
            if (keep.Count <= maxVertices)
            {
                return keep.Count >= 3 ? keep : poly;
            }

            epsilon *= 1.5;
        }

        return poly;
    }

    /// <summary>Chaikin corner cutting on a closed ring: every vertex pair becomes the points at 1/4 and 3/4.</summary>
    public static List<P2> Chaikin(List<P2> poly, int passes)
    {
        var pts = poly;
        for (int pass = 0; pass < passes; pass++)
        {
            var next = new List<P2>(pts.Count * 2);
            for (int i = 0; i < pts.Count; i++)
            {
                var a = pts[i];
                var b = pts[(i + 1) % pts.Count];
                next.Add((a * 0.75) + (b * 0.25));
                next.Add((a * 0.25) + (b * 0.75));
            }

            pts = next;
        }

        return pts;
    }

    private static void DouglasPeucker(List<P2> chain, double epsilon, List<P2> output)
    {
        var keep = new bool[chain.Count];
        keep[0] = true;
        keep[^1] = true;
        Mark(chain, 0, chain.Count - 1, epsilon, keep);
        for (int i = 0; i < chain.Count - 1; i++)
        {
            if (keep[i])
            {
                output.Add(chain[i]);
            }
        }
    }

    private static void Mark(List<P2> chain, int from, int to, double epsilon, bool[] keep)
    {
        double max = 0;
        int index = -1;
        for (int i = from + 1; i < to; i++)
        {
            double d = (ShapeGeometry.ClosestOnSegment(chain[i], chain[from], chain[to]) - chain[i]).Length;
            if (d > max)
            {
                max = d;
                index = i;
            }
        }

        if (index >= 0 && max > epsilon)
        {
            keep[index] = true;
            Mark(chain, from, index, epsilon, keep);
            Mark(chain, index, to, epsilon, keep);
        }
    }

    private static double AngleBetween(P2 a, P2 b)
    {
        double la = a.Length;
        double lb = b.Length;
        if (la < 1e-12 || lb < 1e-12)
        {
            return 0;
        }

        return Math.Acos(Math.Clamp(P2.Dot(a, b) / (la * lb), -1, 1)) * 180 / Math.PI;
    }
}
