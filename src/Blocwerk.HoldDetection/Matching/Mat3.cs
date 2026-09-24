using OpenCvSharp;

namespace Blocwerk.HoldDetection.Matching;

/// <summary>3×3 homogeneous matrices as plain arrays, the form <see cref="HomographyHelper"/> uses.</summary>
internal static class Mat3
{
    /// <summary>A resize by <paramref name="s"/> in OpenCV's pixel-centre convention: <c>x' = (x + 0.5)·s − 0.5</c>.</summary>
    /// <param name="s">The scale.</param>
    /// <returns>The matrix.</returns>
    public static double[,] Scale(double s) => Scale(s, s);

    /// <summary>A resize by (<paramref name="sx"/>, <paramref name="sy"/>) in OpenCV's pixel-centre convention.</summary>
    /// <param name="sx">Scale along x.</param>
    /// <param name="sy">Scale along y.</param>
    /// <returns>The matrix.</returns>
    public static double[,] Scale(double sx, double sy) => new[,]
    {
        { sx, 0, (0.5 * sx) - 0.5 },
        { 0, sy, (0.5 * sy) - 0.5 },
        { 0, 0, 1 },
    };

    /// <summary>The inverse matrix.</summary>
    /// <param name="h">The matrix.</param>
    /// <returns>Its inverse.</returns>
    public static double[,] Invert(double[,] h) => HomographyHelper.Invert(h);

    /// <summary>A translation.</summary>
    /// <param name="dx">Shift in x.</param>
    /// <param name="dy">Shift in y.</param>
    /// <returns>The matrix.</returns>
    public static double[,] Translate(double dx, double dy) => new[,]
    {
        { 1, 0, dx },
        { 0, 1, dy },
        { 0, 0, 1 },
    };

    /// <summary>The product <c>a·b</c> (apply <paramref name="b"/> first).</summary>
    /// <param name="a">Applied second.</param>
    /// <param name="b">Applied first.</param>
    /// <returns>The product.</returns>
    public static double[,] Mul(double[,] a, double[,] b)
    {
        var r = new double[3, 3];
        for (var i = 0; i < 3; i++)
        {
            for (var j = 0; j < 3; j++)
            {
                r[i, j] = (a[i, 0] * b[0, j]) + (a[i, 1] * b[1, j]) + (a[i, 2] * b[2, j]);
            }
        }

        return r;
    }

    /// <summary>The projective depth <c>w</c> of a point under <paramref name="h"/>.</summary>
    /// <param name="h">The matrix.</param>
    /// <param name="x">Point x.</param>
    /// <param name="y">Point y.</param>
    /// <returns>w.</returns>
    public static double Depth(double[,] h, double x, double y) => (h[2, 0] * x) + (h[2, 1] * y) + h[2, 2];

    /// <summary>A CV_64F matrix for OpenCV calls; dispose it.</summary>
    /// <param name="h">The matrix.</param>
    /// <returns>The Mat.</returns>
    public static Mat ToMat(double[,] h)
    {
        var m = new Mat(3, 3, MatType.CV_64FC1);
        for (var r = 0; r < 3; r++)
        {
            for (var c = 0; c < 3; c++)
            {
                m.Set(r, c, h[r, c]);
            }
        }

        return m;
    }
}
