using System.Globalization;

namespace Blocwerk.Core.Helpers;

public static class VolumeCalculator
{
    public static VolumeResult CalculatePyramid(int sideCount, double baseEdge, double height, double thickness)
    {
        if (sideCount < 3 || sideCount > 8)
            throw new ArgumentOutOfRangeException(nameof(sideCount), "Side count must be between 3 and 8.");
        if (baseEdge <= 0 || height <= 0 || thickness <= 0)
            throw new ArgumentException("All dimensions must be positive.");

        var n = sideCount;
        var a = baseEdge;
        var h = height;
        var t = thickness;

        var apothem = a / (2.0 * Math.Tan(Math.PI / n));
        var slantHeight = Math.Sqrt(h * h + apothem * apothem);
        var baseBevelRad = Math.Atan2(h, apothem);
        var baseBevelDeg = Rad2Deg(baseBevelRad);
        var lateralDihedralRad = 2.0 * Math.Atan(Math.Tan(baseBevelRad) * Math.Cos(Math.PI / n));
        var lateralDihedralDeg = Rad2Deg(lateralDihedralRad);
        var miterDeg = (180.0 - lateralDihedralDeg) / 2.0;

        var baseReduction = 2.0 * t / Math.Tan(baseBevelRad);
        var heightReduction = t / Math.Sin(baseBevelRad);
        var effectiveBase = Math.Max(0, a - baseReduction);
        var effectiveSlant = Math.Max(0, slantHeight - heightReduction);

        var baseVertices = GenerateBasePolygon(n, a);
        var apex = new Point3D(0, h, 0);

        var pieces = new List<VolumePiece>();

        var faceTriangle = new Point2D[]
        {
            new(-effectiveBase / 2.0, 0),
            new(effectiveBase / 2.0, 0),
            new(0, effectiveSlant),
        };

        var faceEdgeLengths = new[]
        {
            effectiveBase,
            Math.Sqrt(effectiveBase / 2.0 * (effectiveBase / 2.0) + effectiveSlant * effectiveSlant),
            Math.Sqrt(effectiveBase / 2.0 * (effectiveBase / 2.0) + effectiveSlant * effectiveSlant),
        };

        var faceBevels = new[]
        {
            90.0 - baseBevelDeg,
            miterDeg,
            miterDeg,
        };

        for (var i = 0; i < n; i++)
        {
            pieces.Add(new VolumePiece(
                $"Face {i + 1}",
                faceTriangle,
                faceBevels,
                faceEdgeLengths));
        }

        var basePoly = GenerateBasePolygon2D(n, effectiveBase);
        var baseEdgeLengths = Enumerable.Repeat(effectiveBase, n).ToArray();
        var baseBevels = Enumerable.Repeat(90.0 - baseBevelDeg, n).ToArray();

        pieces.Add(new VolumePiece("Base", basePoly, baseBevels, baseEdgeLengths));

        return new VolumeResult(
            pieces,
            lateralDihedralDeg,
            baseBevelDeg,
            miterDeg,
            slantHeight,
            apothem,
            baseVertices,
            apex,
            null);
    }

    public static VolumeResult CalculatePyramidByAngle(int sideCount, double faceAngleDeg, double edgeLength, double thickness)
    {
        if (sideCount < 3 || sideCount > 8)
            throw new ArgumentOutOfRangeException(nameof(sideCount), "Side count must be between 3 and 8.");
        if (faceAngleDeg <= 0 || faceAngleDeg >= 90 || edgeLength <= 0 || thickness <= 0)
            throw new ArgumentException("Invalid dimensions.");

        var faceAngleRad = Deg2Rad(faceAngleDeg);
        var height = edgeLength * Math.Sin(faceAngleRad);
        var halfBase = edgeLength * Math.Cos(faceAngleRad);
        var baseEdge = 2.0 * halfBase * Math.Tan(Math.PI / sideCount);

        return CalculatePyramid(sideCount, baseEdge, height, thickness);
    }

    public static VolumeResult CalculateDiamond(double width, double length, double tipHeight, double thickness)
    {
        if (width <= 0 || length <= 0 || tipHeight <= 0 || thickness <= 0)
            throw new ArgumentException("All dimensions must be positive.");

        var w = width;
        var l = length;
        var h = tipHeight;
        var t = thickness;

        var halfW = w / 2.0;
        var halfL = l / 2.0;

        var slantW = Math.Sqrt(h * h + halfL * halfL);
        var slantL = Math.Sqrt(h * h + halfW * halfW);

        var bevelW = Rad2Deg(Math.Atan2(h, halfL));
        var bevelL = Rad2Deg(Math.Atan2(h, halfW));

        var diagonalHalf = Math.Sqrt(halfW * halfW + halfL * halfL);
        var edgeSlant = Math.Sqrt(h * h + diagonalHalf * diagonalHalf);

        var faceNormalW = CrossProduct(
            Sub(new Point3D(halfW, 0, 0), new Point3D(-halfW, 0, 0)),
            Sub(new Point3D(0, h, 0), new Point3D(-halfW, 0, 0)));
        var faceNormalL = CrossProduct(
            Sub(new Point3D(0, 0, halfL), new Point3D(halfW, 0, 0)),
            Sub(new Point3D(0, h, 0), new Point3D(halfW, 0, 0)));

        var dihedralRad = Math.Acos(
            Math.Abs(Dot(Normalize(faceNormalW), Normalize(faceNormalL))));
        var dihedralDeg = Rad2Deg(dihedralRad);
        var miterDeg = (180.0 - dihedralDeg) / 2.0;

        var baseReductionW = t / Math.Tan(Deg2Rad(bevelW));
        var baseReductionL = t / Math.Tan(Deg2Rad(bevelL));

        var effW = Math.Max(0, w - 2.0 * baseReductionL);
        var effL = Math.Max(0, l - 2.0 * baseReductionW);
        var effSlantW = Math.Max(0, slantW - t / Math.Sin(Deg2Rad(bevelW)));
        var effSlantL = Math.Max(0, slantL - t / Math.Sin(Deg2Rad(bevelL)));

        var pieces = new List<VolumePiece>();

        var widthFace = new Point2D[]
        {
            new(-effW / 2.0, 0),
            new(effW / 2.0, 0),
            new(0, effSlantW),
        };

        var widthEdgeLengths = new[]
        {
            effW,
            Math.Sqrt(effW / 2.0 * (effW / 2.0) + effSlantW * effSlantW),
            Math.Sqrt(effW / 2.0 * (effW / 2.0) + effSlantW * effSlantW),
        };

        var widthBevels = new[]
        {
            90.0 - bevelW,
            miterDeg,
            miterDeg,
        };

        var lengthFace = new Point2D[]
        {
            new(-effL / 2.0, 0),
            new(effL / 2.0, 0),
            new(0, effSlantL),
        };

        var lengthEdgeLengths = new[]
        {
            effL,
            Math.Sqrt(effL / 2.0 * (effL / 2.0) + effSlantL * effSlantL),
            Math.Sqrt(effL / 2.0 * (effL / 2.0) + effSlantL * effSlantL),
        };

        var lengthBevels = new[]
        {
            90.0 - bevelL,
            miterDeg,
            miterDeg,
        };

        pieces.Add(new VolumePiece("Width Face 1 (top)", widthFace, widthBevels, widthEdgeLengths));
        pieces.Add(new VolumePiece("Width Face 2 (top)", widthFace, widthBevels, widthEdgeLengths));
        pieces.Add(new VolumePiece("Length Face 1 (top)", lengthFace, lengthBevels, lengthEdgeLengths));
        pieces.Add(new VolumePiece("Length Face 2 (top)", lengthFace, lengthBevels, lengthEdgeLengths));
        pieces.Add(new VolumePiece("Width Face 1 (bottom)", widthFace, widthBevels, widthEdgeLengths));
        pieces.Add(new VolumePiece("Width Face 2 (bottom)", widthFace, widthBevels, widthEdgeLengths));
        pieces.Add(new VolumePiece("Length Face 1 (bottom)", lengthFace, lengthBevels, lengthEdgeLengths));
        pieces.Add(new VolumePiece("Length Face 2 (bottom)", lengthFace, lengthBevels, lengthEdgeLengths));

        var baseVertices = new Point3D[]
        {
            new(halfW, 0, 0),
            new(0, 0, halfL),
            new(-halfW, 0, 0),
            new(0, 0, -halfL),
        };

        return new VolumeResult(
            pieces,
            dihedralDeg,
            (bevelW + bevelL) / 2.0,
            miterDeg,
            (slantW + slantL) / 2.0,
            0,
            baseVertices,
            new Point3D(0, h, 0),
            new Point3D(0, -h, 0));
    }

    public static Point2D ProjectIsometric(Point3D p)
    {
        var cos30 = Math.Cos(Math.PI / 6.0);
        var sin30 = Math.Sin(Math.PI / 6.0);
        return new Point2D(
            (p.X - p.Z) * cos30,
            -(p.Y - (p.X + p.Z) * sin30));
    }

    public static string F(double v) => v.ToString("F1", CultureInfo.InvariantCulture);

    public static string F2(double v) => v.ToString("F2", CultureInfo.InvariantCulture);

    private static Point3D[] GenerateBasePolygon(int n, double edgeLength)
    {
        var circumradius = edgeLength / (2.0 * Math.Sin(Math.PI / n));
        var vertices = new Point3D[n];
        for (var i = 0; i < n; i++)
        {
            var angle = 2.0 * Math.PI * i / n - Math.PI / 2.0;
            vertices[i] = new Point3D(
                circumradius * Math.Cos(angle),
                0,
                circumradius * Math.Sin(angle));
        }

        return vertices;
    }

    private static Point2D[] GenerateBasePolygon2D(int n, double edgeLength)
    {
        var circumradius = edgeLength / (2.0 * Math.Sin(Math.PI / n));
        var vertices = new Point2D[n];
        for (var i = 0; i < n; i++)
        {
            var angle = 2.0 * Math.PI * i / n - Math.PI / 2.0;
            vertices[i] = new Point2D(
                circumradius * Math.Cos(angle),
                circumradius * Math.Sin(angle));
        }

        return vertices;
    }

    private static double Rad2Deg(double rad) => rad * 180.0 / Math.PI;

    private static double Deg2Rad(double deg) => deg * Math.PI / 180.0;

    private static Point3D CrossProduct(Point3D a, Point3D b) =>
        new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);

    private static double Dot(Point3D a, Point3D b) =>
        a.X * b.X + a.Y * b.Y + a.Z * b.Z;

    private static Point3D Normalize(Point3D p)
    {
        var len = Math.Sqrt(p.X * p.X + p.Y * p.Y + p.Z * p.Z);
        return len > 0 ? new Point3D(p.X / len, p.Y / len, p.Z / len) : p;
    }

    private static Point3D Sub(Point3D a, Point3D b) =>
        new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
}
