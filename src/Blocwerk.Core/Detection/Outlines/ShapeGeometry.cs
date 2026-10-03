namespace Blocwerk.Core.Detection.Outlines;

/// <summary>A 2D point/vector used by the pure outline geometry (no OpenCV, no entities).</summary>
/// <param name="X">X.</param>
/// <param name="Y">Y.</param>
internal readonly record struct P2(double X, double Y)
{
    public static P2 operator +(P2 a, P2 b) => new(a.X + b.X, a.Y + b.Y);

    public static P2 operator -(P2 a, P2 b) => new(a.X - b.X, a.Y - b.Y);

    public static P2 operator *(P2 a, double s) => new(a.X * s, a.Y * s);

    public double Length => Math.Sqrt((X * X) + (Y * Y));

    public static double Dot(P2 a, P2 b) => (a.X * b.X) + (a.Y * b.Y);

    public static double Cross(P2 a, P2 b) => (a.X * b.Y) - (a.Y * b.X);
}

/// <summary>Polygon helpers shared by the shape smoother and the overlap resolver.</summary>
internal static class ShapeGeometry
{
    public static double SignedArea(IReadOnlyList<P2> poly)
    {
        double sum = 0;
        for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
        {
            sum += P2.Cross(poly[j], poly[i]);
        }

        return sum / 2;
    }

    public static double Area(IReadOnlyList<P2> poly) => Math.Abs(SignedArea(poly));

    public static double Perimeter(IReadOnlyList<P2> poly)
    {
        double sum = 0;
        for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
        {
            sum += (poly[i] - poly[j]).Length;
        }

        return sum;
    }

    /// <summary>Convex hull (monotone chain), counter-clockwise.</summary>
    public static List<P2> ConvexHull(IEnumerable<P2> points)
    {
        var pts = points.Distinct().OrderBy(p => p.X).ThenBy(p => p.Y).ToList();
        if (pts.Count < 3)
        {
            return pts;
        }

        var hull = new List<P2>();
        foreach (var half in new[] { pts, Enumerable.Reverse(pts).ToList() })
        {
            int start = hull.Count;
            foreach (var p in half)
            {
                while (hull.Count >= start + 2 && P2.Cross(hull[^1] - hull[^2], p - hull[^2]) <= 0)
                {
                    hull.RemoveAt(hull.Count - 1);
                }

                hull.Add(p);
            }

            hull.RemoveAt(hull.Count - 1);
        }

        return hull;
    }

    /// <summary>Closest point of segment ab to p.</summary>
    public static P2 ClosestOnSegment(P2 p, P2 a, P2 b)
    {
        var ab = b - a;
        double len2 = P2.Dot(ab, ab);
        if (len2 < 1e-18)
        {
            return a;
        }

        double t = Math.Clamp(P2.Dot(p - a, ab) / len2, 0, 1);
        return a + (ab * t);
    }

    /// <summary>Closest point on the polygon's boundary.</summary>
    public static P2 ClosestOnBoundary(P2 p, IReadOnlyList<P2> poly)
    {
        P2 best = poly[0];
        double bestD = double.MaxValue;
        for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
        {
            var q = ClosestOnSegment(p, poly[j], poly[i]);
            double d = (q - p).Length;
            if (d < bestD)
            {
                bestD = d;
                best = q;
            }
        }

        return best;
    }

    /// <summary>Even-odd point-in-polygon.</summary>
    public static bool Contains(IReadOnlyList<P2> poly, P2 p)
    {
        bool inside = false;
        for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
        {
            var a = poly[i];
            var b = poly[j];
            if ((a.Y > p.Y) != (b.Y > p.Y) && p.X < ((b.X - a.X) * (p.Y - a.Y) / (b.Y - a.Y)) + a.X)
            {
                inside = !inside;
            }
        }

        return inside;
    }

    /// <summary>True when the closed segments ab and cd share a point.</summary>
    public static bool SegmentsIntersect(P2 a, P2 b, P2 c, P2 d)
    {
        double d1 = P2.Cross(b - a, c - a);
        double d2 = P2.Cross(b - a, d - a);
        double d3 = P2.Cross(d - c, a - c);
        double d4 = P2.Cross(d - c, b - c);
        return (((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) && ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0)))
               || (d1 == 0 && OnSegment(a, b, c))
               || (d2 == 0 && OnSegment(a, b, d))
               || (d3 == 0 && OnSegment(c, d, a))
               || (d4 == 0 && OnSegment(c, d, b));
    }

    /// <summary>True when no two non-adjacent edges cross.</summary>
    public static bool IsSimple(IReadOnlyList<P2> poly)
    {
        int n = poly.Count;
        for (int i = 0; i < n; i++)
        {
            for (int j = i + 2; j < n; j++)
            {
                if (i == 0 && j == n - 1)
                {
                    continue;
                }

                if (SegmentsIntersect(poly[i], poly[(i + 1) % n], poly[j], poly[(j + 1) % n]))
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>0 when the polygons intersect or nest, otherwise the gap between their boundaries.</summary>
    public static double Distance(IReadOnlyList<P2> a, IReadOnlyList<P2> b)
    {
        if (EdgesCross(a, b) || Contains(b, a[0]) || Contains(a, b[0]))
        {
            return 0;
        }

        return Math.Min(MinVertexToBoundary(a, b), MinVertexToBoundary(b, a));
    }

    /// <summary>0 when p lies in the polygon, otherwise its distance to the boundary.</summary>
    public static double Distance(P2 p, IReadOnlyList<P2> poly) =>
        Contains(poly, p) ? 0 : (ClosestOnBoundary(p, poly) - p).Length;

    private static bool OnSegment(P2 a, P2 b, P2 p) =>
        Math.Min(a.X, b.X) <= p.X && p.X <= Math.Max(a.X, b.X) && Math.Min(a.Y, b.Y) <= p.Y && p.Y <= Math.Max(a.Y, b.Y);

    private static bool EdgesCross(IReadOnlyList<P2> a, IReadOnlyList<P2> b)
    {
        for (int i = 0, j = a.Count - 1; i < a.Count; j = i++)
        {
            for (int k = 0, l = b.Count - 1; k < b.Count; l = k++)
            {
                if (SegmentsIntersect(a[j], a[i], b[l], b[k]))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static double MinVertexToBoundary(IReadOnlyList<P2> verts, IReadOnlyList<P2> poly) =>
        verts.Min(v => (ClosestOnBoundary(v, poly) - v).Length);
}
