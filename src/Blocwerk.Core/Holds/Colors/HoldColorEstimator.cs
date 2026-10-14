// <copyright file="HoldColorEstimator.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Holds.Colors;

/// <summary>
/// Proposes a <see cref="HoldPalette"/> colour for a hold from the pixels inside its outline, or nothing when it is not
/// sure: a wrong colour is worse than none. Pure, so it runs on plain pixel lists.
/// <para>
/// Pixels that look like the hold's surroundings (the wall showing inside a loose outline) are dropped first. Of what is
/// left, the grey and white pixels (chalk, highlights) are ignored when enough coloured pixels remain; those vote with
/// their hue, weighted by how colourful they are, because chalk and shadow change how light and how strong a colour is
/// but hardly its hue. A hold with almost no colour is judged by <see cref="HoldColorGreys"/>: white, black or dark
/// blue only when nearly all of it is light or dark. Red needs a strong colour: a weak red is as often a pink, salmon
/// or orange hold.
/// </para>
/// <para>
/// Never proposed: grey (a shadowed white and a chalked black look the same), salmon (it sits between pink and red) and
/// the wood tones (a wooden hold on a wooden wall mostly looks like the wall, and the hues of wood and weak warm
/// colours are left alone so wood is not called orange).
/// </para>
/// </summary>
public static class HoldColorEstimator
{
    /// <summary>Fewer usable pixels than this say nothing.</summary>
    public const int MinPixels = 12;

    /// <summary>Chroma below which a pixel counts as grey (chalk, white, black).</summary>
    private const double GreyChroma = 3.5;

    /// <summary>A hold pixel this close to the surroundings' median colour counts as surroundings.</summary>
    private const double SurroundingsDistance = 4.0;

    /// <summary>At least this share of the outline must differ from the surroundings.</summary>
    private const double MinOwnShare = 0.25;

    /// <summary>The share of coloured pixels from which the hold is judged by its hue.</summary>
    private const double ColoredShare = 0.40;

    /// <summary>The share of coloured pixels up to which the hold is judged as white or black.</summary>
    private const double GreyShare = 0.10;

    /// <summary>How well the coloured pixels must agree on one hue (1 is perfect agreement).</summary>
    private const double HueAgreement = 0.90;

    /// <summary>The median chroma a coloured hold needs at the least.</summary>
    private const double MinChroma = 4.5;

    /// <summary>
    /// The hue ranges (degrees, from inclusive, to exclusive) with the chroma each needs. The gaps between them are
    /// where two colours cannot be told apart. Wood sits between orange and yellow (hue 56 to 84), so orange ends where
    /// wood begins, and red, orange and yellow need more chroma than the cool colours.
    /// </summary>
    private static readonly (string Color, double From, double To, double Chroma)[] Hues =
    [
        ("pink", 338, 12, MinChroma),
        ("red", 19, 30, 9.0),
        ("orange", 38, 56, 7.0),
        ("yellow", 84, 112, 5.0),
        ("green", 120, 185, MinChroma),
        ("blue", 225, 272, MinChroma),
        ("purple", 280, 322, MinChroma),
    ];

    /// <summary>The palette keys this can propose.</summary>
    public static IReadOnlyList<string> Proposable { get; } = [.. Hues.Select(h => h.Color), "white", "black"];

    /// <summary>Estimates one hold.</summary>
    /// <param name="hold">The pixels inside the hold's outline.</param>
    /// <param name="surroundings">Pixels in a ring around the hold; empty when unknown.</param>
    /// <param name="area">The hold's area in photo pixels, passed through to the estimate.</param>
    /// <returns>The estimate.</returns>
    public static HoldColorEstimate Estimate(IReadOnlyList<OklabColor> hold, IReadOnlyList<OklabColor> surroundings, double area = 0)
    {
        if (hold.Count < MinPixels)
        {
            return HoldColorEstimate.Unset(HoldColorVerdict.TooFewPixels);
        }

        OklabColor? wall = surroundings.Count < MinPixels ? null : MedianOf(surroundings);
        var own = wall is { } w ? hold.Where(p => p.DistanceTo(w) > SurroundingsDistance).ToList() : [.. hold];
        if (own.Count < Math.Max(MinPixels, MinOwnShare * hold.Count))
        {
            return HoldColorEstimate.Unset(HoldColorVerdict.LikeSurroundings);
        }

        var colored = own.Where(p => p.Chroma >= GreyChroma).ToList();
        var share = colored.Count / (double)own.Count;
        var (color, verdict) = share >= ColoredShare ? ByHue(colored)
            : share <= GreyShare ? HoldColorGreys.Judge(own, wall)
            : HoldColorGreys.IsChalkedWhite(own, wall) ? ("white", HoldColorVerdict.Proposed)
            : (null, HoldColorVerdict.MostlyChalk);
        return new HoldColorEstimate(color, verdict, area);
    }

    /// <summary>The per-channel median of some pixels.</summary>
    /// <param name="pixels">The pixels; at least one.</param>
    /// <returns>The median colour.</returns>
    internal static OklabColor MedianOf(IReadOnlyList<OklabColor> pixels) =>
        new(Median(pixels.Select(p => p.L)), Median(pixels.Select(p => p.A)), Median(pixels.Select(p => p.B)));

    private static (string? Color, HoldColorVerdict Verdict) ByHue(List<OklabColor> colored)
    {
        // The chroma-weighted mean direction of the a/b vectors: its length against the summed chroma says how well
        // the pixels agree on one hue.
        double sumA = colored.Sum(p => p.A), sumB = colored.Sum(p => p.B), total = colored.Sum(p => p.Chroma);
        if (Math.Sqrt((sumA * sumA) + (sumB * sumB)) / total < HueAgreement)
        {
            return (null, HoldColorVerdict.MixedHues);
        }

        var hue = new OklabColor(0, sumA, sumB).Hue;
        var range = Hues.FirstOrDefault(h => h.From < h.To ? hue >= h.From && hue < h.To : hue >= h.From || hue < h.To);
        if (range.Color is null)
        {
            return (null, HoldColorVerdict.BetweenColors);
        }

        return Median(colored.Select(p => p.Chroma)) < range.Chroma
            ? (null, HoldColorVerdict.Pale)
            : (range.Color, HoldColorVerdict.Proposed);
    }

    /// <summary>The median of some values.</summary>
    /// <param name="values">The values; at least one.</param>
    /// <returns>The median.</returns>
    internal static double Median(IEnumerable<double> values)
    {
        var sorted = values.Order().ToList();
        return sorted.Count % 2 == 1 ? sorted[sorted.Count / 2] : (sorted[(sorted.Count / 2) - 1] + sorted[sorted.Count / 2]) / 2.0;
    }
}
