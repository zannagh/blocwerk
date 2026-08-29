using Blocwerk.Core.Abstractions;
using OpenCvSharp;

namespace Blocwerk.HoldDetection.Matching;

/// <summary>
/// Samples a per-hold CIE-Lab colour directly from the decoded image so colour matching works
/// even when the caller supplies no <see cref="MatcherHold.Color"/>. Each colour is a small
/// median Lab patch at the hold centre; a caller-supplied colour always takes precedence.
/// </summary>
internal static class LabSampling
{
    /// <summary>
    /// Returns one Lab triple (OpenCV 8-bit convention) per hold: the caller's
    /// <see cref="MatcherHold.Color"/> when present, otherwise a median patch sampled at the
    /// hold centre. Entries are null only when the sample falls entirely off-frame.
    /// </summary>
    /// <param name="bgr">The decoded BGR image the holds were detected on.</param>
    /// <param name="holds">Holds with normalized centres (0..1).</param>
    /// <param name="patchRadius">Half-size of the square median patch, in pixels.</param>
    public static double[]?[] Effective(Mat bgr, IReadOnlyList<MatcherHold> holds, int patchRadius = 2)
    {
        using var lab = new Mat();
        Cv2.CvtColor(bgr, lab, ColorConversionCodes.BGR2Lab);
        int w = lab.Width, hgt = lab.Height;
        var outp = new double[holds.Count][];
        for (int i = 0; i < holds.Count; i++)
        {
            MatcherHold hld = holds[i];
            if (hld.Color is not null)
            {
                outp[i] = new[] { hld.Color.L, hld.Color.A, hld.Color.B };
                continue;
            }

            int cx = (int)Math.Round(hld.X * w);
            int cy = (int)Math.Round(hld.Y * hgt);
            outp[i] = SampleMedian(lab, cx, cy, patchRadius);
        }

        return outp;
    }

    private static double[]? SampleMedian(Mat lab, int cx, int cy, int r)
    {
        int x0 = Math.Max(0, cx - r), x1 = Math.Min(lab.Width - 1, cx + r);
        int y0 = Math.Max(0, cy - r), y1 = Math.Min(lab.Height - 1, cy + r);
        if (x1 < x0 || y1 < y0)
        {
            return null;
        }

        var ls = new List<double>();
        var as_ = new List<double>();
        var bs = new List<double>();
        for (int y = y0; y <= y1; y++)
        {
            for (int x = x0; x <= x1; x++)
            {
                Vec3b px = lab.At<Vec3b>(y, x);
                ls.Add(px.Item0);
                as_.Add(px.Item1);
                bs.Add(px.Item2);
            }
        }

        return new[] { Median(ls), Median(as_), Median(bs) };
    }

    private static double Median(List<double> values)
    {
        values.Sort();
        int n = values.Count;
        return n % 2 == 1 ? values[n / 2] : 0.5 * (values[(n / 2) - 1] + values[n / 2]);
    }
}

/// <summary>Small Lab-space helpers shared by the matcher's colour tie-break and rescue.</summary>
internal static class LabMath
{
    /// <summary>Euclidean distance between two Lab vectors.</summary>
    public static double Norm(double[] a, double[] b)
    {
        double s = 0;
        for (int i = 0; i < a.Length; i++)
        {
            double d = a[i] - b[i];
            s += d * d;
        }

        return Math.Sqrt(s);
    }

    /// <summary>Quantile-matched Lab distance for a proposed pair (40 when either colour is missing).</summary>
    public static double Distance(double[]? la, double[]? lb, ColorQuantileMap qmap)
    {
        if (la is null || lb is null)
        {
            return 40.0;
        }

        return Norm(la, qmap.Apply(lb));
    }

    /// <summary>Filters a nullable colour array down to the present values.</summary>
    public static List<double[]> NonNull(double[]?[] colours)
    {
        var list = new List<double[]>();
        foreach (double[]? c in colours)
        {
            if (c is not null)
            {
                list.Add(c);
            }
        }

        return list;
    }
}

/// <summary>
/// Maps a right-image Lab colour into the left image's colour distribution by per-channel
/// quantile (histogram) matching — no correspondences needed. This absorbs global exposure
/// / white-balance differences between the two photos before colours are compared. Faithful
/// port of the Python <c>quantile_match</c>.
/// </summary>
internal sealed class ColorQuantileMap
{
    private readonly double[][] sortedL;
    private readonly double[][] sortedR;
    private readonly double[][] quantL;
    private readonly double[][] quantR;

    /// <summary>Initializes a new instance of the <see cref="ColorQuantileMap"/> class from the two images' Lab colour marginals.</summary>
    /// <param name="labL">Left-image hold colours (each length-3 Lab).</param>
    /// <param name="labR">Right-image hold colours (each length-3 Lab).</param>
    public ColorQuantileMap(IReadOnlyList<double[]> labL, IReadOnlyList<double[]> labR)
    {
        sortedL = new double[3][];
        sortedR = new double[3][];
        quantL = new double[3][];
        quantR = new double[3][];
        for (int ch = 0; ch < 3; ch++)
        {
            sortedL[ch] = SortedChannel(labL, ch);
            sortedR[ch] = SortedChannel(labR, ch);
            quantL[ch] = LinSpace(sortedL[ch].Length);
            quantR[ch] = LinSpace(sortedR[ch].Length);
        }
    }

    /// <summary>Maps a right-image Lab vector into the left image's colour distribution.</summary>
    public double[] Apply(double[] labR)
    {
        var outv = new double[3];
        for (int ch = 0; ch < 3; ch++)
        {
            double q = Interp(labR[ch], sortedR[ch], quantR[ch]);
            outv[ch] = Interp(q, quantL[ch], sortedL[ch]);
        }

        return outv;
    }

    private static double[] SortedChannel(IReadOnlyList<double[]> lab, int ch)
    {
        if (lab.Count == 0)
        {
            return new[] { 0.0 };
        }

        var v = new double[lab.Count];
        for (int i = 0; i < lab.Count; i++)
        {
            v[i] = lab[i][ch];
        }

        Array.Sort(v);
        return v;
    }

    private static double[] LinSpace(int n)
    {
        if (n <= 1)
        {
            return new[] { 0.0 };
        }

        var q = new double[n];
        for (int i = 0; i < n; i++)
        {
            q[i] = (double)i / (n - 1);
        }

        return q;
    }

    /// <summary>Linear interpolation matching numpy.interp (clamps at the ends).</summary>
    private static double Interp(double x, double[] xp, double[] fp)
    {
        int n = xp.Length;
        if (n == 0)
        {
            return 0.0;
        }

        if (x <= xp[0])
        {
            return fp[0];
        }

        if (x >= xp[n - 1])
        {
            return fp[n - 1];
        }

        int lo = 0;
        int hi = n - 1;
        while (hi - lo > 1)
        {
            int mid = (lo + hi) / 2;
            if (xp[mid] <= x)
            {
                lo = mid;
            }
            else
            {
                hi = mid;
            }
        }

        double denom = xp[hi] - xp[lo];
        if (Math.Abs(denom) < 1e-12)
        {
            return fp[lo];
        }

        double t = (x - xp[lo]) / denom;
        return fp[lo] + (t * (fp[hi] - fp[lo]));
    }
}
