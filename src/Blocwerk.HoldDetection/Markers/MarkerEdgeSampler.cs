using System.Runtime.InteropServices;
using OpenCvSharp;

namespace Blocwerk.HoldDetection.Markers;

/// <summary>
/// A Gaussian-blurred (sigma 1, float) window of the grayscale image around one marker, plus the
/// sub-pixel edge sampling of the black square's sides. The window is padded by more than the blur
/// radius, so every sample equals what a blur of the whole image would give (the reference
/// blurs the full frame once; a per-marker window keeps the app from allocating a 48 MB float copy).
/// </summary>
internal sealed class MarkerEdgeSampler
{
    /// <summary>Profile step along the normal, px (reference: 0.25).</summary>
    private const double Step = 0.25;

    /// <summary>OpenCV's kernel for a float image at sigma 1 is 9 taps: radius 4, plus 2 spare.</summary>
    private const int BlurPad = 6;

    /// <summary>Minimum gradient (grey levels per 0.25 px sample) for an edge to count.</summary>
    private const double MinGradient = 2.0;

    private readonly float[] data;
    private readonly int originX;
    private readonly int originY;
    private readonly int stride;
    private readonly int imageWidth;
    private readonly int imageHeight;

    private MarkerEdgeSampler(float[] pixels, Rect window, Size image)
    {
        data = pixels;
        originX = window.X;
        originY = window.Y;
        stride = window.Width;
        imageWidth = image.Width;
        imageHeight = image.Height;
    }

    /// <summary>Half-length of an edge profile: stays inside the black border (side/6) of the marker.</summary>
    public static double ProfileHalfLength(double side) => Math.Max(3.0, side / 7.0);

    /// <summary>Blurs the window of <paramref name="gray"/> that covers <paramref name="corners"/>; null when it is off-image.</summary>
    public static MarkerEdgeSampler? Create(Mat gray, IReadOnlyList<Point2d> corners, double side)
    {
        var reach = (int)Math.Ceiling(ProfileHalfLength(side)) + 2 + BlurPad;
        var x0 = Math.Max(0, (int)Math.Floor(corners.Min(c => c.X)) - reach);
        var y0 = Math.Max(0, (int)Math.Floor(corners.Min(c => c.Y)) - reach);
        var x1 = Math.Min(gray.Width, (int)Math.Ceiling(corners.Max(c => c.X)) + reach + 1);
        var y1 = Math.Min(gray.Height, (int)Math.Ceiling(corners.Max(c => c.Y)) + reach + 1);
        if (x1 - x0 < 2 || y1 - y0 < 2)
        {
            return null;
        }

        var window = new Rect(x0, y0, x1 - x0, y1 - y0);
        using var roi = new Mat(gray, window);
        using var asFloat = new Mat();
        roi.ConvertTo(asFloat, MatType.CV_32FC1);
        using var blurred = new Mat();
        Cv2.GaussianBlur(asFloat, blurred, new Size(0, 0), 1.0);

        var buffer = new float[window.Width * window.Height];
        Marshal.Copy(blurred.Data, buffer, 0, buffer.Length);
        return new MarkerEdgeSampler(buffer, window, gray.Size());
    }

    /// <summary>
    /// Sub-pixel dark→bright (inside→outside) edge points along side a→b, sampled at 15..85 % of the
    /// side so neither corner (screw heads, paper edge) can pull them.
    /// </summary>
    public List<Point2d> EdgePoints(Point2d a, Point2d b, Point2d centre, double side, EdgeSubPixelMethod method)
    {
        var ab = b - a;
        var length = Math.Sqrt(ab.DotProduct(ab));
        if (length < 1e-6)
        {
            return [];
        }

        var d = ab * (1.0 / length);
        var n = new Point2d(d.Y, -d.X);
        var mid = (a + b) * 0.5;
        if (n.DotProduct(mid - centre) < 0)
        {
            n = n * -1.0;
        }

        var offsets = ProfileOffsets(ProfileHalfLength(side));
        var count = (int)Math.Clamp(length / 2.0, 8.0, 80.0);
        var points = new List<Point2d>(count);
        var profile = new double[offsets.Length];
        for (var i = 0; i < count; i++)
        {
            var t = 0.15 + (0.7 * i / (count - 1));
            var p = a + (ab * t);
            if (TryEdgeOffset(p, n, offsets, profile, method, out var offset))
            {
                points.Add(p + (n * offset));
            }
        }

        return points;
    }

    /// <summary>numpy.arange(-w, w + 1e-9, 0.25).</summary>
    private static double[] ProfileOffsets(double w)
    {
        var count = (int)Math.Ceiling(((2 * w) + 1e-9) / Step);
        var s = new double[count];
        for (var i = 0; i < count; i++)
        {
            s[i] = -w + (i * Step);
        }

        return s;
    }

    private bool TryEdgeOffset(Point2d p, Point2d n, double[] s, double[] profile, EdgeSubPixelMethod method, out double offset)
    {
        offset = 0;
        var first = p + (n * s[0]);
        var last = p + (n * s[^1]);
        if (Math.Min(first.X, last.X) < 1 || Math.Min(first.Y, last.Y) < 1
            || Math.Max(first.X, last.X) > imageWidth - 2 || Math.Max(first.Y, last.Y) > imageHeight - 2)
        {
            return false;
        }

        for (var i = 0; i < s.Length; i++)
        {
            profile[i] = Bilinear(p.X + (s[i] * n.X), p.Y + (s[i] * n.Y));
        }

        var g = Gradient(profile);
        var k = ArgMax(g);
        if (g[k] <= MinGradient || k == 0 || k == s.Length - 1)
        {
            return false;
        }

        offset = method == EdgeSubPixelMethod.PeakCentroid ? HalfMaxCentroid(g, s, k) : Parabola(g, s, k);
        return true;
    }

    /// <summary>numpy.gradient: central differences inside, one-sided at the ends.</summary>
    private static double[] Gradient(double[] f)
    {
        var last = f.Length - 1;
        var g = new double[f.Length];
        g[0] = f[1] - f[0];
        g[last] = f[last] - f[last - 1];
        for (var i = 1; i < last; i++)
        {
            g[i] = (f[i + 1] - f[i - 1]) / 2.0;
        }

        return g;
    }

    /// <summary>First index of the maximum, like numpy.argmax.</summary>
    private static int ArgMax(double[] g)
    {
        var k = 0;
        for (var i = 1; i < g.Length; i++)
        {
            if (g[i] > g[k])
            {
                k = i;
            }
        }

        return k;
    }

    /// <summary>The reference's 3-point parabola through the gradient peak.</summary>
    private static double Parabola(double[] g, double[] s, int k)
    {
        var den = g[k - 1] - (2 * g[k]) + g[k + 1];
        var sub = den != 0 ? 0.5 * (g[k - 1] - g[k + 1]) / den : 0.0;
        return s[k] + (sub * Step);
    }

    /// <summary>
    /// Gradient-weighted centroid of the peak's half-maximum run. Bilinear sampling makes the profile
    /// piecewise linear, so its gradient is flat within each pixel; a 3-point parabola on that plateau
    /// snaps the edge to pixel cells (a 1 px staircase along near-axis-aligned sides). The centroid
    /// of the whole peak does not.
    /// </summary>
    private static double HalfMaxCentroid(double[] g, double[] s, int k)
    {
        var half = 0.5 * g[k];
        var lo = k;
        while (lo > 0 && g[lo - 1] > half)
        {
            lo--;
        }

        var hi = k;
        while (hi < g.Length - 1 && g[hi + 1] > half)
        {
            hi++;
        }

        double sum = 0, weighted = 0;
        for (var i = lo; i <= hi; i++)
        {
            sum += g[i];
            weighted += g[i] * s[i];
        }

        return weighted / sum;
    }

    /// <summary>scipy map_coordinates(order=1) at full-image (x, y); callers keep it 1 px inside the image.</summary>
    private double Bilinear(double x, double y)
    {
        var lx = x - originX;
        var ly = y - originY;
        var ix = (int)Math.Floor(lx);
        var iy = (int)Math.Floor(ly);
        var fx = lx - ix;
        var fy = ly - iy;
        var i = (iy * stride) + ix;
        var top = (data[i] * (1 - fx)) + (data[i + 1] * fx);
        var bottom = (data[i + stride] * (1 - fx)) + (data[i + stride + 1] * fx);
        return (top * (1 - fy)) + (bottom * fy);
    }
}
