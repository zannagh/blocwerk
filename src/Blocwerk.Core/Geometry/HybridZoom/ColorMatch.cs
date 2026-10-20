// <copyright file="ColorMatch.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Geometry.HybridZoom;

/// <summary>One colour sample, 0..255 per channel.</summary>
/// <param name="R">Red.</param>
/// <param name="G">Green.</param>
/// <param name="B">Blue.</param>
public readonly record struct Rgb(double R, double G, double B);

/// <summary>A per-channel gain and offset: out = gain · in + offset.</summary>
/// <param name="Gain">Gain.</param>
/// <param name="Offset">Offset, 0..255 units.</param>
public readonly record struct ChannelFit(double Gain, double Offset)
{
    /// <summary>The mapping as a 256-entry lookup table.</summary>
    /// <returns>The table.</returns>
    public byte[] Lut()
    {
        var lut = new byte[256];
        for (var i = 0; i < 256; i++)
        {
            lut[i] = (byte)Math.Clamp(Math.Round((Gain * i) + Offset), 0, 255);
        }

        return lut;
    }
}

/// <summary>The colour transfer from a close-up to the panel photo, per channel.</summary>
/// <param name="R">Red.</param>
/// <param name="G">Green.</param>
/// <param name="B">Blue.</param>
/// <param name="Samples">Samples the fit kept.</param>
public sealed record ColorFit(ChannelFit R, ChannelFit G, ChannelFit B, int Samples)
{
    /// <summary>A sample mapped through the fit.</summary>
    /// <param name="c">The sample.</param>
    /// <returns>The mapped sample, clamped to 0..255.</returns>
    public Rgb Apply(Rgb c) => new(Map(R, c.R), Map(G, c.G), Map(B, c.B));

    private static double Map(ChannelFit f, double v) => Math.Clamp((f.Gain * v) + f.Offset, 0, 255);
}

/// <summary>
/// Matches a close-up's colours to the panel photo it is laid over. Capture photos (close range, the phone's own
/// exposure and white balance per shot) and panel photos differ visibly, so the crossfade would read as a colour
/// jump. A per-channel gain + offset is fitted by least squares on paired samples of the two over the same wall,
/// robustly: clipped samples (near black or white in either) are left out, then the worst-fitting quarter is dropped
/// twice (holds that shift between the views, specular spots) and the fit redone. Gains and offsets are bounded so a
/// bad overlap can never invert or blow out the image. <see cref="MeanDeltaE"/> measures the result (CIE76 in Lab).
/// Pure: no I/O.
/// </summary>
public static class ColorMatch
{
    /// <summary>Fewest samples a fit needs.</summary>
    public const int MinSamples = 64;

    /// <summary>Gain bounds.</summary>
    public const double MinGain = 0.6;

    public const double MaxGain = 1.6;

    /// <summary>Offset bound, 0..255 units.</summary>
    public const double MaxOffset = 60;

    private const double Clipped = 6;
    private const double TrimShare = 0.25;
    private const int TrimPasses = 2;

    /// <summary>The fit from <paramref name="source"/> (close-up) to <paramref name="target"/> (panel), or null with too few usable samples.</summary>
    /// <param name="source">Close-up samples.</param>
    /// <param name="target">Panel samples at the same places.</param>
    /// <returns>The fit.</returns>
    public static ColorFit? Fit(IReadOnlyList<Rgb> source, IReadOnlyList<Rgb> target)
    {
        var pairs = source.Zip(target, (s, t) => (S: s, T: t)).Where(p => !IsClipped(p.S) && !IsClipped(p.T)).ToList();
        if (pairs.Count < MinSamples)
        {
            return null;
        }

        var fit = FitAll(pairs);
        for (var pass = 0; pass < TrimPasses; pass++)
        {
            var current = fit;
            var keep = (int)Math.Max(MinSamples, pairs.Count * (1 - TrimShare));
            pairs = [.. pairs.OrderBy(p => Residual(current, p.S, p.T)).Take(keep)];
            fit = FitAll(pairs);
        }

        return fit;
    }

    /// <summary>The mean CIE76 colour difference between paired samples (clipped pairs included: it is what one sees).</summary>
    /// <param name="a">One side.</param>
    /// <param name="b">The other.</param>
    /// <returns>Mean ΔE, or 0 without samples.</returns>
    public static double MeanDeltaE(IReadOnlyList<Rgb> a, IReadOnlyList<Rgb> b)
    {
        var n = Math.Min(a.Count, b.Count);
        if (n == 0)
        {
            return 0;
        }

        var sum = 0.0;
        for (var i = 0; i < n; i++)
        {
            var (l1, a1, b1) = Lab(a[i]);
            var (l2, a2, b2) = Lab(b[i]);
            sum += Math.Sqrt(((l1 - l2) * (l1 - l2)) + ((a1 - a2) * (a1 - a2)) + ((b1 - b2) * (b1 - b2)));
        }

        return sum / n;
    }

    /// <summary>
    /// ΔE between the mean colours of two sample sets: the overall cast one sees as a jump in a crossfade, without the
    /// per-pixel differences that come from the two views not lining up pixel for pixel.
    /// </summary>
    /// <param name="a">One side.</param>
    /// <param name="b">The other.</param>
    /// <returns>ΔE of the means, or 0 without samples.</returns>
    public static double CastDeltaE(IReadOnlyList<Rgb> a, IReadOnlyList<Rgb> b) =>
        a.Count == 0 || b.Count == 0 ? 0 : MeanDeltaE([Mean(a)], [Mean(b)]);

    /// <summary>sRGB (0..255, D65) → CIE L*a*b*.</summary>
    /// <param name="c">The colour.</param>
    /// <returns>L, a, b.</returns>
    public static (double L, double A, double B) Lab(Rgb c)
    {
        double r = Linear(c.R), g = Linear(c.G), b = Linear(c.B);
        var x = ((0.4124564 * r) + (0.3575761 * g) + (0.1804375 * b)) / 0.95047;
        var y = (0.2126729 * r) + (0.7151522 * g) + (0.0721750 * b);
        var z = ((0.0193339 * r) + (0.1191920 * g) + (0.9503041 * b)) / 1.08883;
        double fx = F(x), fy = F(y), fz = F(z);
        return ((116 * fy) - 16, 500 * (fx - fy), 200 * (fy - fz));
    }

    private static Rgb Mean(IReadOnlyList<Rgb> c) => new(c.Average(x => x.R), c.Average(x => x.G), c.Average(x => x.B));

    private static ColorFit FitAll(List<(Rgb S, Rgb T)> pairs) => new(
        Channel(pairs, p => p.S.R, p => p.T.R),
        Channel(pairs, p => p.S.G, p => p.T.G),
        Channel(pairs, p => p.S.B, p => p.T.B),
        pairs.Count);

    /// <summary>Least squares t = g·s + o, bounded; a flat channel (no spread) only shifts.</summary>
    private static ChannelFit Channel(List<(Rgb S, Rgb T)> pairs, Func<(Rgb S, Rgb T), double> s, Func<(Rgb S, Rgb T), double> t)
    {
        var ms = pairs.Average(s);
        var mt = pairs.Average(t);
        var cov = pairs.Sum(p => (s(p) - ms) * (t(p) - mt));
        var spread = pairs.Sum(p => (s(p) - ms) * (s(p) - ms));
        var gain = spread > 1e-6 ? Math.Clamp(cov / spread, MinGain, MaxGain) : 1;
        return new ChannelFit(gain, Math.Clamp(mt - (gain * ms), -MaxOffset, MaxOffset));
    }

    private static double Residual(ColorFit fit, Rgb s, Rgb t)
    {
        var m = fit.Apply(s);
        return ((m.R - t.R) * (m.R - t.R)) + ((m.G - t.G) * (m.G - t.G)) + ((m.B - t.B) * (m.B - t.B));
    }

    private static bool IsClipped(Rgb c) =>
        Math.Min(c.R, Math.Min(c.G, c.B)) < Clipped || Math.Max(c.R, Math.Max(c.G, c.B)) > 255 - Clipped;

    private static double Linear(double v)
    {
        var c = v / 255;
        return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    }

    private static double F(double t) => t > 216.0 / 24389 ? Math.Cbrt(t) : ((24389.0 / 27 * t) + 16) / 116;
}
