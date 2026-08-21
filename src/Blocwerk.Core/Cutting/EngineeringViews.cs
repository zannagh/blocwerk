namespace Blocwerk.Core.Helpers;

/// <summary>Geometry of one profile edge, used to place dimensions and bevel callouts.</summary>
public sealed record EdgeInfo(
    Point2D Start,
    Point2D End,
    Point2D Mid,
    Point2D OutwardNormal,
    double Length,
    double BevelAngle);

/// <summary>Axis-aligned bounds of a 2D outline.</summary>
public readonly record struct Bounds(double MinX, double MinY, double MaxX, double MaxY)
{
    public double Width => MaxX - MinX;

    public double Height => MaxY - MinY;
}

/// <summary>
/// A cut piece as a real beveled solid: the top (outer) profile at z = 0 and the
/// bottom (back) profile at z = thickness, offset per edge by the bevel. Projecting
/// this solid gives engineering views that actually show the angled faces.
/// </summary>
public sealed record BeveledPanel(Point2D[] Top, Point2D[] Bottom, double Thickness);

/// <summary>
/// Derives the geometry for a single <see cref="CutPiece"/> engineering sheet: the
/// dimensioned front edges plus the beveled solid and its orthographic/isometric
/// projections. Pure geometry, no Skia.
/// </summary>
public static class EngineeringViews
{
    public static Bounds ProfileBounds(Point2D[] profile) => new(
        profile.Min(p => p.X),
        profile.Min(p => p.Y),
        profile.Max(p => p.X),
        profile.Max(p => p.Y));

    public static EdgeInfo[] Edges(CutPiece piece)
    {
        var verts = piece.Profile;
        var n = verts.Length;
        var cx = verts.Average(p => p.X);
        var cy = verts.Average(p => p.Y);

        var result = new EdgeInfo[n];
        for (var i = 0; i < n; i++)
        {
            var a = verts[i];
            var b = verts[(i + 1) % n];
            var mid = new Point2D((a.X + b.X) / 2.0, (a.Y + b.Y) / 2.0);
            var dx = b.X - a.X;
            var dy = b.Y - a.Y;
            var len = Math.Sqrt((dx * dx) + (dy * dy));
            var nx = len > 0 ? -dy / len : 0;
            var ny = len > 0 ? dx / len : 0;

            if (((mid.X - cx) * nx) + ((mid.Y - cy) * ny) < 0)
            {
                nx = -nx;
                ny = -ny;
            }

            var length = i < piece.EdgeLengths.Length ? piece.EdgeLengths[i] : len;
            var bevel = i < piece.EdgeBevelAngles.Length ? piece.EdgeBevelAngles[i] : 0;
            result[i] = new EdgeInfo(a, b, mid, new Point2D(nx, ny), length, bevel);
        }

        return result;
    }

    public static BeveledPanel Beveled(CutPiece piece) =>
        new(piece.Profile, OffsetPolygon(piece.Profile, piece.EdgeInsetMm), piece.Thickness);

    /// <summary>Every edge of the beveled solid: the top loop (z=0), bottom loop (z=t), and the connecting side edges.</summary>
    public static IEnumerable<(Point3D A, Point3D B)> SolidEdges(BeveledPanel panel)
    {
        var n = panel.Top.Length;
        var t = panel.Thickness;
        for (var i = 0; i < n; i++)
        {
            var a = panel.Top[i];
            var b = panel.Top[(i + 1) % n];
            yield return (new Point3D(a.X, a.Y, 0), new Point3D(b.X, b.Y, 0));
        }

        for (var i = 0; i < n; i++)
        {
            var a = panel.Bottom[i];
            var b = panel.Bottom[(i + 1) % n];
            yield return (new Point3D(a.X, a.Y, t), new Point3D(b.X, b.Y, t));
        }

        for (var i = 0; i < n; i++)
        {
            yield return (new Point3D(panel.Top[i].X, panel.Top[i].Y, 0), new Point3D(panel.Bottom[i].X, panel.Bottom[i].Y, t));
        }
    }

    // View projections (3D solid -> 2D). Front looks down -Z; the four orthographic
    // views look along an axis so the wood thickness (z) reads as the shallow dimension.
    public static Point2D Front(Point3D p) => new(p.X, p.Y);

    public static Point2D Iso(Point3D p) => VolumeCalculator.ProjectIsometric(p);

    public static Point2D Top(Point3D p) => new(p.X, -p.Z);

    public static Point2D Bottom(Point3D p) => new(p.X, p.Z);

    public static Point2D Left(Point3D p) => new(-p.Z, p.Y);

    public static Point2D Right(Point3D p) => new(p.Z, p.Y);

    private static Point2D[] OffsetPolygon(Point2D[] verts, double[] insets)
    {
        var n = verts.Length;
        var cx = verts.Average(p => p.X);
        var cy = verts.Average(p => p.Y);

        var linePoint = new Point2D[n];
        var lineDir = new Point2D[n];
        for (var i = 0; i < n; i++)
        {
            var a = verts[i];
            var b = verts[(i + 1) % n];
            var dx = b.X - a.X;
            var dy = b.Y - a.Y;
            var len = Math.Sqrt((dx * dx) + (dy * dy));
            if (len < 1e-9)
            {
                len = 1;
            }

            var ux = dx / len;
            var uy = dy / len;
            var nx = -uy;
            var ny = ux;
            var mx = (a.X + b.X) / 2.0;
            var my = (a.Y + b.Y) / 2.0;
            if (((mx - cx) * nx) + ((my - cy) * ny) > 0)
            {
                nx = -nx; // point inward, toward the centroid
                ny = -ny;
            }

            var inset = i < insets.Length ? insets[i] : 0;
            linePoint[i] = new Point2D(a.X + (nx * inset), a.Y + (ny * inset));
            lineDir[i] = new Point2D(ux, uy);
        }

        var result = new Point2D[n];
        for (var j = 0; j < n; j++)
        {
            var prev = (j - 1 + n) % n;
            result[j] = Intersect(linePoint[prev], lineDir[prev], linePoint[j], lineDir[j])
                        ?? verts[j];
        }

        return result;
    }

    private static Point2D? Intersect(Point2D p, Point2D d, Point2D q, Point2D e)
    {
        var denom = (d.X * e.Y) - (d.Y * e.X);
        if (Math.Abs(denom) < 1e-9)
        {
            return null;
        }

        var tParam = (((q.X - p.X) * e.Y) - ((q.Y - p.Y) * e.X)) / denom;
        return new Point2D(p.X + (d.X * tParam), p.Y + (d.Y * tParam));
    }
}
