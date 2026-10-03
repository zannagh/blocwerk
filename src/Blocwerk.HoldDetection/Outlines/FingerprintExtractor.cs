using Blocwerk.Core.Abstractions;
using OpenCvSharp;

namespace Blocwerk.HoldDetection.Outlines;

/// <summary>
/// Measures a <see cref="HoldFingerprint"/> from a hold mask on its working crop. Colours are read in the
/// OpenCV 8-bit Lab convention the overlap matcher's <c>LabSampling</c> uses; sizes are converted back to
/// full-image pixels.
/// </summary>
internal static class FingerprintExtractor
{
    /// <summary>Chroma (8-bit a/b units) below which a pixel counts as neutral (white/grey/black).</summary>
    public const double NeutralChroma = 10;

    /// <summary>Builds the fingerprint.</summary>
    /// <param name="crop">The working crop.</param>
    /// <param name="mask">Filled binary hold mask at working resolution.</param>
    /// <param name="contour">The hold's outer contour at working resolution.</param>
    /// <returns>The fingerprint.</returns>
    public static HoldFingerprint Extract(OutlineCrop crop, Mat mask, Point[] contour)
    {
        using var lab = new Mat();
        Cv2.CvtColor(crop.Bgr, lab, ColorConversionCodes.BGR2Lab);
        byte[] labBytes = CropPixels.ToBytes(lab);
        byte[] maskBytes = CropPixels.ToBytes(mask);
        var (median, hist) = ColourStats(labBytes, maskBytes);

        double area = Cv2.ContourArea(contour);
        RotatedRect rect = Cv2.MinAreaRect(contour);
        double longSide = Math.Max(rect.Size.Width, rect.Size.Height);
        double shortSide = Math.Max(1e-3, Math.Min(rect.Size.Width, rect.Size.Height));
        double angle = rect.Size.Width >= rect.Size.Height ? rect.Angle : rect.Angle + 90;
        Point[] hull = Cv2.ConvexHull(contour);
        double hullArea = Math.Max(1e-6, Cv2.ContourArea(hull));

        return new HoldFingerprint
        {
            L = Math.Round(median[0], 2),
            A = Math.Round(median[1], 2),
            B = Math.Round(median[2], 2),
            Histogram = hist.Select(v => Math.Round(v, 4)).ToArray(),
            AreaPx = Math.Round(area / (crop.Scale * crop.Scale), 1),
            Aspect = Math.Round(longSide / shortSide, 4),
            OrientationDeg = Math.Round(((-angle % 180) + 180) % 180, 2),
            Solidity = Math.Round(Math.Clamp(area / hullArea, 0, 1), 4),
            Hu = LogHu(contour),
        };
    }

    private static (double[] Median, double[] Histogram) ColourStats(byte[] lab, byte[] mask)
    {
        var ls = new List<double>();
        var as_ = new List<double>();
        var bs = new List<double>();
        var hist = new double[HoldFingerprint.HueBins + 1];
        for (int i = 0; i < mask.Length; i++)
        {
            if (mask[i] == 0)
            {
                continue;
            }

            int o = i * 3;
            ls.Add(lab[o]);
            as_.Add(lab[o + 1]);
            bs.Add(lab[o + 2]);
            double a = lab[o + 1] - 128.0, b = lab[o + 2] - 128.0;
            if (Math.Sqrt((a * a) + (b * b)) < NeutralChroma)
            {
                hist[HoldFingerprint.HueBins]++;
                continue;
            }

            double hue = (Math.Atan2(b, a) * 180 / Math.PI) + 360;
            hist[(int)(hue % 360 / (360.0 / HoldFingerprint.HueBins)) % HoldFingerprint.HueBins]++;
        }

        double total = Math.Max(1, ls.Count);
        for (int k = 0; k < hist.Length; k++)
        {
            hist[k] /= total;
        }

        return ([Median(ls), Median(as_), Median(bs)], hist);
    }

    private static double[] LogHu(Point[] contour)
    {
        Moments m = Cv2.Moments(contour);
        double[] hu = m.HuMoments();
        return hu
            .Select(h => Math.Abs(h) < 1e-30 ? 0 : Math.Round(-Math.Sign(h) * Math.Log10(Math.Abs(h)), 4))
            .ToArray();
    }

    private static double Median(List<double> values)
    {
        if (values.Count == 0)
        {
            return 128;
        }

        values.Sort();
        return values[values.Count / 2];
    }
}
