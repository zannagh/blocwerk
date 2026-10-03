namespace Blocwerk.Core.Detection.Outlines;

/// <summary>The footprints already claimed on a panel; answers "is there room here" with a bounding-box prefilter.</summary>
internal sealed class ShapeObstacles(double tolerance)
{
    private const int CircleSegments = 48;

    private readonly List<(P2[] Poly, double MinX, double MinY, double MaxX, double MaxY)> items = [];

    /// <summary>A circle as a polygon whose edges are tangent (circumscribed), so it never under-reports an overlap.</summary>
    public static P2[] Circle(P2 centre, double radius)
    {
        double r = radius / Math.Cos(Math.PI / CircleSegments);
        var poly = new P2[CircleSegments];
        for (int i = 0; i < CircleSegments; i++)
        {
            double a = 2 * Math.PI * i / CircleSegments;
            poly[i] = new P2(centre.X + (r * Math.Cos(a)), centre.Y + (r * Math.Sin(a)));
        }

        return poly;
    }

    public void Add(IReadOnlyList<P2> poly)
    {
        items.Add((poly.ToArray(), poly.Min(p => p.X), poly.Min(p => p.Y), poly.Max(p => p.X), poly.Max(p => p.Y)));
    }

    /// <summary>True when the polygon is closer than the tolerance to (or inside) any claimed footprint.</summary>
    public bool Overlaps(IReadOnlyList<P2> poly) => Near(poly).Any(i => ShapeGeometry.Distance(poly, i) < tolerance);

    /// <summary>The claimed footprints that the polygon overlaps (closer than the tolerance, or nested).</summary>
    public List<P2[]> OverlappingWith(IReadOnlyList<P2> poly) =>
        Near(poly).Where(i => ShapeGeometry.Distance(poly, i) < tolerance).ToList();

    /// <summary>
    /// The footprints whose bounding box (inflated by the tolerance) meets the polygon's: the cheap prefilter in
    /// front of every exact distance, which keeps a 900-hold panel interactive.
    /// </summary>
    private IEnumerable<P2[]> Near(IReadOnlyList<P2> poly)
    {
        double minX = poly.Min(p => p.X) - tolerance;
        double minY = poly.Min(p => p.Y) - tolerance;
        double maxX = poly.Max(p => p.X) + tolerance;
        double maxY = poly.Max(p => p.Y) + tolerance;
        foreach (var item in items)
        {
            if (item.MaxX >= minX && item.MinX <= maxX && item.MaxY >= minY && item.MinY <= maxY)
            {
                yield return item.Poly;
            }
        }
    }
}
