namespace Blocwerk.Core.Detection.Outlines;

/// <summary>How much two polygons overlap, measured by sampling a grid over the part where their bounding boxes meet.</summary>
internal static class ShapeOverlapArea
{
    private const int Grid = 40;

    /// <summary>The area of <c>a</c> and <c>b</c> together, and the area of the part they share (0 when apart).</summary>
    /// <param name="a">First polygon.</param>
    /// <param name="b">Second polygon.</param>
    /// <returns>Both areas; the shared one is an estimate (a few percent).</returns>
    public static (double AreaA, double AreaB, double Shared) Measure(IReadOnlyList<P2> a, IReadOnlyList<P2> b)
    {
        double areaA = ShapeGeometry.Area(a);
        double areaB = ShapeGeometry.Area(b);
        double minX = Math.Max(a.Min(p => p.X), b.Min(p => p.X));
        double maxX = Math.Min(a.Max(p => p.X), b.Max(p => p.X));
        double minY = Math.Max(a.Min(p => p.Y), b.Min(p => p.Y));
        double maxY = Math.Min(a.Max(p => p.Y), b.Max(p => p.Y));
        if (maxX <= minX || maxY <= minY)
        {
            return (areaA, areaB, 0);
        }

        int hits = 0;
        for (int i = 0; i < Grid; i++)
        {
            double x = minX + ((i + 0.5) / Grid * (maxX - minX));
            for (int j = 0; j < Grid; j++)
            {
                var p = new P2(x, minY + ((j + 0.5) / Grid * (maxY - minY)));
                if (ShapeGeometry.Contains(a, p) && ShapeGeometry.Contains(b, p))
                {
                    hits++;
                }
            }
        }

        double shared = (double)hits / (Grid * Grid) * (maxX - minX) * (maxY - minY);
        return (areaA, areaB, Math.Min(shared, Math.Min(areaA, areaB)));
    }
}
