using OpenCvSharp;

namespace Blocwerk.HoldDetection.Matching;

/// <summary>
/// Appearance patch descriptors for the NCC tie-break: a normalised grayscale patch around
/// a hold, zero-mean and unit-norm so a dot product gives normalised cross-correlation.
/// Faithful port of the Python <c>patch_desc</c> (patch sized ~1.4× the hold box).
/// </summary>
internal static class MatchAppearance
{
    /// <summary>
    /// Builds a zero-mean unit-norm descriptor for the hold centred at (<paramref name="cxPx"/>,
    /// <paramref name="cyPx"/>) whose box side is <paramref name="sizePx"/>. Returns null if the
    /// patch is off-frame or too small.
    /// </summary>
    public static float[]? Describe(Mat img, double cxPx, double cyPx, double sizePx, int outSize = 32)
    {
        double r = sizePx * 0.7;
        int x0 = (int)(cxPx - r);
        int y0 = (int)(cyPx - r);
        int x1 = (int)(cxPx + r);
        int y1 = (int)(cyPx + r);
        int x0c = Math.Max(0, x0);
        int y0c = Math.Max(0, y0);
        int x1c = Math.Min(img.Width, x1);
        int y1c = Math.Min(img.Height, y1);
        if (x1c - x0c < 6 || y1c - y0c < 6)
        {
            return null;
        }

        using var roi = new Mat(img, new Rect(x0c, y0c, x1c - x0c, y1c - y0c));
        using var gray = new Mat();
        Cv2.CvtColor(roi, gray, ColorConversionCodes.BGR2GRAY);
        using var small = new Mat();
        Cv2.Resize(gray, small, new Size(outSize, outSize), 0, 0, InterpolationFlags.Area);
        using var f = new Mat();
        small.ConvertTo(f, MatType.CV_32F);

        f.GetArray(out float[] data);
        double mean = 0.0;
        for (int i = 0; i < data.Length; i++)
        {
            mean += data[i];
        }

        mean /= data.Length;

        double norm = 0.0;
        for (int i = 0; i < data.Length; i++)
        {
            data[i] = (float)(data[i] - mean);
            norm += (double)data[i] * data[i];
        }

        norm = Math.Sqrt(norm);
        if (norm <= 1e-6)
        {
            return null;
        }

        for (int i = 0; i < data.Length; i++)
        {
            data[i] = (float)(data[i] / norm);
        }

        return data;
    }
}
