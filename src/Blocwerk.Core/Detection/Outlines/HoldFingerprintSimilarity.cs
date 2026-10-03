using Blocwerk.Core.Abstractions;

namespace Blocwerk.Core.Detection.Outlines;

/// <summary>
/// The scoring behind <see cref="HoldFingerprint.Similarity"/>. Every sub-score is 0..1.
/// <list type="bullet">
/// <item><b>Colour</b> = 0.6 · dominant + 0.4 · histogram. Dominant is <c>exp(-(ΔE/20)²)</c> on the median
/// Lab with lightness weighted ×0.5 (exposure, chalk and shadow move L far more than hue); histogram is the
/// intersection of the two hue histograms after a ±1-bin circular smoothing.</item>
/// <item><b>Shape</b> = 0.4 · Hu + 0.35 · aspect + 0.25 · solidity. Hu is <c>exp(-d/0.35)</c> on the mean
/// absolute difference of the first four log-Hu moments (higher orders are noise at hold resolution);
/// aspect is <c>exp(-|ln(ratio)|/0.3)</c>; solidity is <c>1 - |Δ|/0.25</c>.</item>
/// <item><b>Size</b> (only when BOTH fingerprints carry millimetres) = 0.5 · area + 0.5 · long side, each
/// <c>exp(-|ln(ratio)|/0.25)</c>.</item>
/// </list>
/// Totals: with sizes <c>0.45·colour + 0.30·size + 0.25·shape</c>; without <c>0.60·colour + 0.40·shape</c>.
/// Pixel areas are never compared — they depend on the camera distance of each photo.
/// </summary>
public static class HoldFingerprintSimilarity
{
    /// <summary>Colour weight when metric sizes are available.</summary>
    public const double ColourWeightMetric = 0.45;

    /// <summary>Size weight when metric sizes are available.</summary>
    public const double SizeWeightMetric = 0.30;

    /// <summary>Shape weight when metric sizes are available.</summary>
    public const double ShapeWeightMetric = 0.25;

    /// <summary>Colour weight in the size-free comparison.</summary>
    public const double ColourWeight = 0.60;

    /// <summary>Shape weight in the size-free comparison.</summary>
    public const double ShapeWeight = 0.40;

    /// <summary>Computes the similarity of two fingerprints (0..1).</summary>
    /// <param name="a">First fingerprint.</param>
    /// <param name="b">Second fingerprint.</param>
    /// <returns>The weighted similarity.</returns>
    public static double Compute(HoldFingerprint a, HoldFingerprint b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);

        double colour = ColourScore(a, b);
        double shape = ShapeScore(a, b);
        double? size = SizeScore(a, b);
        double total = size is { } s
            ? (ColourWeightMetric * colour) + (SizeWeightMetric * s) + (ShapeWeightMetric * shape)
            : (ColourWeight * colour) + (ShapeWeight * shape);
        return Math.Clamp(total, 0, 1);
    }

    /// <summary>The colour sub-score (dominant Lab + hue histogram).</summary>
    /// <param name="a">First fingerprint.</param>
    /// <param name="b">Second fingerprint.</param>
    /// <returns>0..1.</returns>
    public static double ColourScore(HoldFingerprint a, HoldFingerprint b)
    {
        double dl = 0.5 * (a.L - b.L) * 100.0 / 255.0;
        double da = a.A - b.A;
        double db = a.B - b.B;
        double de = Math.Sqrt((dl * dl) + (da * da) + (db * db));
        double dominant = Math.Exp(-Math.Pow(de / 20.0, 2));
        double hist = HistogramIntersection(a.Histogram, b.Histogram);
        return (0.6 * dominant) + (0.4 * hist);
    }

    /// <summary>The size-free shape sub-score (Hu moments, aspect, solidity).</summary>
    /// <param name="a">First fingerprint.</param>
    /// <param name="b">Second fingerprint.</param>
    /// <returns>0..1.</returns>
    public static double ShapeScore(HoldFingerprint a, HoldFingerprint b)
    {
        double hu = 0;
        int n = Math.Min(4, Math.Min(a.Hu.Length, b.Hu.Length));
        for (int i = 0; i < n; i++)
        {
            hu += Math.Abs(a.Hu[i] - b.Hu[i]);
        }

        double huScore = n == 0 ? 0.5 : Math.Exp(-(hu / n) / 0.35);
        double aspect = Math.Exp(-Math.Abs(Math.Log(Math.Max(a.Aspect, 1) / Math.Max(b.Aspect, 1))) / 0.3);
        double solidity = Math.Clamp(1 - (Math.Abs(a.Solidity - b.Solidity) / 0.25), 0, 1);
        return (0.4 * huScore) + (0.35 * aspect) + (0.25 * solidity);
    }

    /// <summary>The metric size sub-score, or null unless both fingerprints carry millimetres.</summary>
    /// <param name="a">First fingerprint.</param>
    /// <param name="b">Second fingerprint.</param>
    /// <returns>0..1, or null.</returns>
    public static double? SizeScore(HoldFingerprint a, HoldFingerprint b)
    {
        if (a.AreaMm2 is not > 0 || b.AreaMm2 is not > 0 || a.WidthMm is not > 0 || b.WidthMm is not > 0)
        {
            return null;
        }

        double area = Math.Exp(-Math.Abs(Math.Log(a.AreaMm2.Value / b.AreaMm2.Value)) / 0.25);
        double width = Math.Exp(-Math.Abs(Math.Log(a.WidthMm.Value / b.WidthMm.Value)) / 0.25);
        return (0.5 * area) + (0.5 * width);
    }

    private static double HistogramIntersection(double[] a, double[] b)
    {
        int bins = HoldFingerprint.HueBins;
        if (a.Length < bins + 1 || b.Length < bins + 1)
        {
            return 0;
        }

        double[] sa = Smooth(a, bins);
        double[] sb = Smooth(b, bins);
        double sum = 0;
        for (int i = 0; i <= bins; i++)
        {
            sum += Math.Min(sa[i], sb[i]);
        }

        return Math.Clamp(sum, 0, 1);
    }

    private static double[] Smooth(double[] h, int bins)
    {
        var s = new double[bins + 1];
        for (int i = 0; i < bins; i++)
        {
            s[i] = (0.5 * h[i]) + (0.25 * h[(i + bins - 1) % bins]) + (0.25 * h[(i + 1) % bins]);
        }

        s[bins] = h[bins];
        return s;
    }
}
