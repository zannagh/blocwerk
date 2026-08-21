namespace Blocwerk.Core.Helpers;

/// <summary>
/// Hip-roof volume: a rectangular base that rises to a horizontal ridge edge, with
/// all four sides sloping inward. The two long sides are trapezoids (base edge
/// <c>L</c> down to ridge edge <c>R</c>); the two ends are triangular hips that fall
/// off from the base width <c>B</c> up to the ridge ends. Like a triangular prism
/// whose apex is a ridge of a length you choose rather than a single point.
///
/// Thickness compensation follows the pyramid/diamond convention: the base-seated
/// edges are pulled in for the wood thickness, and the mitred ridge/hip joints carry
/// a bevel (saw tilt) instead of a length change.
/// </summary>
public static partial class VolumeCalculator
{
    public static VolumeResult CalculateRoof(
        double baseLength,
        double baseWidth,
        double ridgeLength,
        double height,
        double thickness)
    {
        if (baseLength <= 0 || baseWidth <= 0 || height <= 0 || thickness <= 0)
        {
            throw new ArgumentException("All dimensions must be positive.");
        }

        if (ridgeLength <= 0 || ridgeLength > baseLength)
        {
            throw new ArgumentException("Ridge length must be greater than 0 and no longer than the base length.");
        }

        var l = baseLength;
        var b = baseWidth;
        var r = ridgeLength;
        var h = height;
        var t = thickness;

        var halfB = b / 2.0;
        var hipRun = (l - r) / 2.0; // horizontal run of the triangular hip ends

        // Face slope angles (from the base plane) and slant heights.
        var longAngleRad = Math.Atan2(h, halfB);
        var hipAngleRad = Math.Atan2(h, hipRun); // == 90° when the ridge spans the full length
        var slantLong = Math.Sqrt((h * h) + (halfB * halfB));
        var slantHip = Math.Sqrt((h * h) + (hipRun * hipRun));

        var longAngleDeg = Rad2Deg(longAngleRad);
        var hipAngleDeg = Rad2Deg(hipAngleRad);

        // 3D geometry (before thickness compensation) for the dihedral angles.
        var baseVertices = new[]
        {
            new Point3D(l / 2.0, 0, halfB),
            new Point3D(-l / 2.0, 0, halfB),
            new Point3D(-l / 2.0, 0, -halfB),
            new Point3D(l / 2.0, 0, -halfB),
        };
        var ridgePos = new Point3D(r / 2.0, h, 0);
        var ridgeNeg = new Point3D(-r / 2.0, h, 0);

        var frontLong = new[] { baseVertices[1], baseVertices[0], ridgePos, ridgeNeg };
        var rightHip = new[] { baseVertices[0], baseVertices[3], ridgePos };

        var ridgeDihedralDeg = Dihedral(frontLong, new[] { baseVertices[3], baseVertices[2], ridgeNeg, ridgePos }, ridgeNeg, ridgePos);
        var hipDihedralDeg = Dihedral(frontLong, rightHip, baseVertices[0], ridgePos);

        var ridgeMiter = 90.0 - (ridgeDihedralDeg / 2.0);
        var hipMiter = 90.0 - (hipDihedralDeg / 2.0);

        // Thickness compensation on the base-seated edges (mirrors the pyramid).
        var effBaseL = Math.Max(0, l - (2.0 * t / Math.Tan(longAngleRad)));
        var effBaseB = Math.Max(0, b - (2.0 * t / Math.Tan(hipAngleRad)));
        var effSlantLong = Math.Max(0, slantLong - (t / Math.Sin(longAngleRad)));
        var effSlantHip = Math.Max(0, slantHip - (t / Math.Sin(hipAngleRad)));

        var pieces = new List<VolumePiece>
        {
            LongFace(effBaseL, r, effSlantLong, longAngleDeg, hipMiter, ridgeMiter),
            LongFace(effBaseL, r, effSlantLong, longAngleDeg, hipMiter, ridgeMiter),
            HipEnd(effBaseB, effSlantHip, hipAngleDeg, hipMiter),
            HipEnd(effBaseB, effSlantHip, hipAngleDeg, hipMiter),
            RoofBase(effBaseL, effBaseB, longAngleDeg, hipAngleDeg),
        };

        return new VolumeResult(
            pieces,
            ridgeDihedralDeg,
            90.0 - longAngleDeg,
            hipMiter,
            slantLong,
            halfB,
            baseVertices,
            new Point3D(0, h, 0),
            null,
            new[] { ridgeNeg, ridgePos });
    }

    private static VolumePiece LongFace(double bottom, double top, double slant, double baseBevelDeg, double hipMiterDeg, double ridgeMiterDeg)
    {
        var verts = new Point2D[]
        {
            new(-bottom / 2.0, 0),
            new(bottom / 2.0, 0),
            new(top / 2.0, slant),
            new(-top / 2.0, slant),
        };

        var side = Dist(verts[1], verts[2]);
        var lengths = new[] { bottom, side, top, side };
        var bevels = new[] { 90.0 - baseBevelDeg, hipMiterDeg, ridgeMiterDeg, hipMiterDeg };
        return new VolumePiece("Long face", verts, bevels, lengths);
    }

    private static VolumePiece HipEnd(double baseEdge, double slant, double baseBevelDeg, double hipMiterDeg)
    {
        var verts = new Point2D[]
        {
            new(-baseEdge / 2.0, 0),
            new(baseEdge / 2.0, 0),
            new(0, slant),
        };

        var side = Dist(verts[1], verts[2]);
        var lengths = new[] { baseEdge, side, side };
        var bevels = new[] { 90.0 - baseBevelDeg, hipMiterDeg, hipMiterDeg };
        return new VolumePiece("Hip end", verts, bevels, lengths);
    }

    private static VolumePiece RoofBase(double lengthEdge, double widthEdge, double longAngleDeg, double hipAngleDeg)
    {
        var verts = new Point2D[]
        {
            new(-lengthEdge / 2.0, -widthEdge / 2.0),
            new(lengthEdge / 2.0, -widthEdge / 2.0),
            new(lengthEdge / 2.0, widthEdge / 2.0),
            new(-lengthEdge / 2.0, widthEdge / 2.0),
        };

        var lengths = new[] { lengthEdge, widthEdge, lengthEdge, widthEdge };
        var bevels = new[]
        {
            90.0 - longAngleDeg, // long edges seat against the long trapezoid faces
            90.0 - hipAngleDeg,  // short edges seat against the triangular hip ends
            90.0 - longAngleDeg,
            90.0 - hipAngleDeg,
        };
        return new VolumePiece("Base", verts, bevels, lengths);
    }

    private static double Dist(Point2D a, Point2D b) =>
        Math.Sqrt(((a.X - b.X) * (a.X - b.X)) + ((a.Y - b.Y) * (a.Y - b.Y)));

    private static double Dihedral(Point3D[] faceA, Point3D[] faceB, Point3D edge0, Point3D edge1)
    {
        var edge = Normalize(Sub(edge1, edge0));
        var mid = new Point3D((edge0.X + edge1.X) / 2.0, (edge0.Y + edge1.Y) / 2.0, (edge0.Z + edge1.Z) / 2.0);
        var va = PerpComponent(Sub(Centroid(faceA), mid), edge);
        var vb = PerpComponent(Sub(Centroid(faceB), mid), edge);
        return Rad2Deg(AngleBetween(va, vb));
    }

    private static Point3D Centroid(Point3D[] verts)
    {
        double x = 0, y = 0, z = 0;
        foreach (var v in verts)
        {
            x += v.X;
            y += v.Y;
            z += v.Z;
        }

        return new Point3D(x / verts.Length, y / verts.Length, z / verts.Length);
    }

    private static Point3D PerpComponent(Point3D v, Point3D unitAxis)
    {
        var d = Dot(v, unitAxis);
        return new Point3D(v.X - (d * unitAxis.X), v.Y - (d * unitAxis.Y), v.Z - (d * unitAxis.Z));
    }

    private static double AngleBetween(Point3D a, Point3D b)
    {
        var la = Math.Sqrt(Dot(a, a));
        var lb = Math.Sqrt(Dot(b, b));
        if (la == 0 || lb == 0)
        {
            return 0;
        }

        return Math.Acos(Math.Clamp(Dot(a, b) / (la * lb), -1.0, 1.0));
    }
}
