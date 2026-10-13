// <copyright file="HoldColorAgreement.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Holds.Colors;

/// <summary>
/// Compares two <see cref="HoldPalette"/> colours of what may be one hold. Pure. A missing colour is neutral, and so are
/// two colours a photo or a person easily mixes up (pink and red, blue and purple, the wood tones, grey and white):
/// only two clearly different colours count as a disagreement.
/// </summary>
public static class HoldColorAgreement
{
    private static readonly HashSet<(string, string)> Confusable = Pairs(
        ("pink", "red"),
        ("red", "orange"),
        ("orange", "yellow"),
        ("blue", "purple"),
        ("purple", "pink"),
        ("salmon", "pink"),
        ("salmon", "orange"),
        ("salmon", "red"),
        ("gray", "white"),
        ("gray", "black"));

    /// <summary>The relation of two colours.</summary>
    /// <param name="a">One colour key, or null.</param>
    /// <param name="b">The other colour key, or null.</param>
    /// <returns>Whether they agree, clearly disagree, or say nothing.</returns>
    public static HoldColorRelation Compare(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
        {
            return HoldColorRelation.Unknown;
        }

        var (x, y) = (a.Trim().ToLowerInvariant(), b.Trim().ToLowerInvariant());
        if (x == y)
        {
            return HoldColorRelation.Same;
        }

        // The photo estimate never names a wood tone, and bleached or dark wood reads as almost anything.
        if (IsWood(x) || IsWood(y) || !Known(x) || !Known(y) || Confusable.Contains((x, y)))
        {
            return HoldColorRelation.Unknown;
        }

        return HoldColorRelation.Different;
    }

    private static bool IsWood(string key) => key.StartsWith("wood", StringComparison.Ordinal);

    private static bool Known(string key) => HoldPalette.All.Any(c => c.Key == key);

    private static HashSet<(string, string)> Pairs(params (string A, string B)[] pairs) =>
        [.. pairs, .. pairs.Select(p => (p.B, p.A))];
}
