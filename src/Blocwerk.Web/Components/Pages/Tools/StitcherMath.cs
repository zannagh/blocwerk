using System.Globalization;

namespace Blocwerk.Web.Components.Pages.Tools;

/// <summary>
/// Projective (homography) helpers for the image stitcher. All matrices are
/// row-major 3x3 arrays of length 9. Corner arrays are ordered
/// TL, TR, BR, BL to match the image source rectangle (0,0)(W,0)(W,H)(0,H).
/// </summary>
public static class StitcherMath
{
    /// <summary>Maps the source rectangle (WxH) onto four world corners.</summary>
    public static double[] ProjectionForCorners(double w, double h, (double X, double Y)[] c)
    {
        var src = new[] { (0.0, 0.0), (w, 0.0), (w, h), (0.0, h) };
        var dst = new[] { (c[0].X, c[0].Y), (c[1].X, c[1].Y), (c[2].X, c[2].Y), (c[3].X, c[3].Y) };
        return General2DProjection(src, dst);
    }

    /// <summary>Renders a source-rect -> corners homography as a CSS matrix3d transform.</summary>
    public static string Matrix3d(double w, double h, (double X, double Y)[] corners)
    {
        var m = ProjectionForCorners(w, h, corners);
        var i = m[8];
        if (Math.Abs(i) < 1e-12)
        {
            i = 1e-12;
        }

        // 3x3 [a b c; d e f; g h i] embedded column-major into a 4x4.
        double a = m[0] / i, b = m[1] / i, c0 = m[2] / i;
        double d = m[3] / i, e = m[4] / i, f = m[5] / i;
        double g = m[6] / i, hh = m[7] / i;
        var vals = new[] { a, d, 0, g, b, e, 0, hh, 0, 0, 1, 0, c0, f, 0, 1.0 };
        return "matrix3d(" + string.Join(",", vals.Select(N)) + ")";
    }

    /// <summary>Projects a point through a row-major 3x3 homography.</summary>
    public static (double X, double Y) Apply(double[] m, double x, double y)
    {
        var w = (m[6] * x) + (m[7] * y) + m[8];
        if (Math.Abs(w) < 1e-12)
        {
            w = 1e-12;
        }

        return (((m[0] * x) + (m[1] * y) + m[2]) / w, ((m[3] * x) + (m[4] * y) + m[5]) / w);
    }

    public static double[] MultMM(double[] a, double[] b)
    {
        var r = new double[9];
        for (var row = 0; row < 3; row++)
        {
            for (var col = 0; col < 3; col++)
            {
                r[(row * 3) + col] =
                    (a[(row * 3) + 0] * b[col]) +
                    (a[(row * 3) + 1] * b[3 + col]) +
                    (a[(row * 3) + 2] * b[6 + col]);
            }
        }

        return r;
    }

    private static double[] General2DProjection((double, double)[] s, (double, double)[] d)
    {
        var sm = BasisToPoints(s);
        var dm = BasisToPoints(d);
        return MultMM(dm, Adjugate(sm));
    }

    private static double[] BasisToPoints((double X, double Y)[] p)
    {
        var m = new[]
        {
            p[0].X, p[1].X, p[2].X,
            p[0].Y, p[1].Y, p[2].Y,
            1, 1, 1,
        };
        var v = MultMV(Adjugate(m), new[] { p[3].X, p[3].Y, 1.0 });
        var scale = new[] { v[0], 0, 0, 0, v[1], 0, 0, 0, v[2] };
        return MultMM(m, scale);
    }

    private static double[] MultMV(double[] m, double[] v) => new[]
    {
        (m[0] * v[0]) + (m[1] * v[1]) + (m[2] * v[2]),
        (m[3] * v[0]) + (m[4] * v[1]) + (m[5] * v[2]),
        (m[6] * v[0]) + (m[7] * v[1]) + (m[8] * v[2]),
    };

    private static double[] Adjugate(double[] m) => new[]
    {
        (m[4] * m[8]) - (m[5] * m[7]),
        (m[2] * m[7]) - (m[1] * m[8]),
        (m[1] * m[5]) - (m[2] * m[4]),
        (m[5] * m[6]) - (m[3] * m[8]),
        (m[0] * m[8]) - (m[2] * m[6]),
        (m[2] * m[3]) - (m[0] * m[5]),
        (m[3] * m[7]) - (m[4] * m[6]),
        (m[1] * m[6]) - (m[0] * m[7]),
        (m[0] * m[4]) - (m[1] * m[3]),
    };

    private static string N(double v) => v.ToString("0.########", CultureInfo.InvariantCulture);
}
