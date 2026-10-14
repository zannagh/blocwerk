// <copyright file="HoldColorPalette.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Holds.Colors;

/// <summary>
/// The wall's own palette: the holds of known colour on the same photo. Pink, red and orange overlap in hue, and where
/// <see cref="HoldColorEstimator"/> cannot tell them apart a hold is compared with the wall's own pink, red and orange
/// holds instead: when nearly all of its nearest ones (<see cref="HoldColorSignature"/>, against the bare wall) have
/// one colour, it gets that colour. Only those three: the wood tones and white do not separate this way.
/// </summary>
internal sealed class HoldColorPalette
{
    /// <summary>How many holds of known colour are asked.</summary>
    private const int Neighbours = 9;

    /// <summary>How many of them must agree.</summary>
    private const int Agreeing = 7;

    /// <summary>How much a difference in lightness counts against one in colour (shadow and chalk change lightness most).</summary>
    private const double LightnessWeight = 0.03;

    /// <summary>The chroma a hold needs to be asked about: paler ones are as often salmon, which sits between all three.</summary>
    private const double MinChroma = 5.5;

    /// <summary>The warm hues (degrees, from inclusive, to exclusive) where the palette is asked.</summary>
    private const double WarmFrom = 0;
    private const double WarmTo = 45;

    private static readonly HashSet<string> Votable = ["pink", "red", "orange"];

    private readonly List<(HoldColorSignature Signature, string Color)> references;

    /// <summary>Initializes a new instance of the <see cref="HoldColorPalette"/> class.</summary>
    /// <param name="known">The holds of known colour on the photo: their signatures and colours.</param>
    public HoldColorPalette(IEnumerable<(HoldColorSignature Signature, string Color)> known) => references = [.. known];

    /// <summary>The estimate, or the palette's colour for a warm hold the estimate left unset between two colours or as pale.</summary>
    /// <param name="estimate">The estimator's answer.</param>
    /// <param name="signature">The hold's signature, or null.</param>
    /// <returns>The estimate to use.</returns>
    public HoldColorEstimate Refine(HoldColorEstimate estimate, HoldColorSignature? signature)
    {
        if (estimate is not { Color: null, Verdict: HoldColorVerdict.BetweenColors or HoldColorVerdict.Pale }
            || signature is not { Hue: >= WarmFrom and < WarmTo, Chroma: >= MinChroma } || references.Count < Neighbours)
        {
            return estimate;
        }

        var (color, votes) = references
            .OrderBy(r => Distance(r.Signature, signature))
            .Take(Neighbours)
            .GroupBy(r => r.Color, StringComparer.Ordinal)
            .Select(g => (Color: g.Key, Votes: g.Count()))
            .MaxBy(g => g.Votes);
        return votes >= Agreeing && Votable.Contains(color)
            ? estimate with { Color = color, Verdict = HoldColorVerdict.Proposed }
            : estimate;
    }

    private static double Distance(HoldColorSignature x, HoldColorSignature y)
    {
        double da = x.A - y.A, db = x.B - y.B, dl = LightnessWeight * (x.L - y.L);
        return Math.Sqrt((da * da) + (db * db) + (dl * dl));
    }
}
