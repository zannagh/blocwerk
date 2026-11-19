// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Enums;

namespace Blocwerk.Core.Helpers;

/// <summary>
/// What the Server's pages and the offline client share about <see cref="BoulderStyle"/>: the fixed set in its display
/// order, the names that go over the wire and into URLs, and counting boulders per style.
/// </summary>
public static class BoulderStyles
{
    /// <summary>Gets the styles in the order the cards and pickers show them.</summary>
    public static IReadOnlyList<BoulderStyle> All { get; } =
    [
        BoulderStyle.Tension,
        BoulderStyle.Dynamic,
        BoulderStyle.Funky,
        BoulderStyle.Endurance,
        BoulderStyle.Power,
        BoulderStyle.Technical,
    ];

    /// <summary>Gets every style at once, the mask that drops unknown bits.</summary>
    public static BoulderStyle Mask { get; } = All.Aggregate(BoulderStyle.None, (acc, s) => acc | s);

    /// <summary>The display label of one style.</summary>
    /// <param name="style">A single style.</param>
    /// <returns>Its name.</returns>
    public static string Label(BoulderStyle style) => style.ToString();

    /// <summary>The URL value of one style (lower case, the label).</summary>
    /// <param name="style">A single style.</param>
    /// <returns>The slug.</returns>
    public static string Slug(BoulderStyle style) => style.ToString().ToLowerInvariant();

    /// <summary>The single style a URL value names, or null for anything else ("all", empty, unknown).</summary>
    /// <param name="slug">The query value.</param>
    /// <returns>The style, or null.</returns>
    public static BoulderStyle? FromSlug(string? slug)
    {
        foreach (var style in All)
        {
            if (string.Equals(Slug(style), slug?.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return style;
            }
        }

        return null;
    }

    /// <summary>The <c>?style=</c> value a link may carry: a style's slug, <c>all</c>, or null for anything else.</summary>
    /// <param name="slug">The raw query value.</param>
    /// <returns>The normalised value, or null.</returns>
    public static string? QueryValue(string? slug) =>
        string.Equals(slug?.Trim(), "all", StringComparison.OrdinalIgnoreCase) ? "all" : FromSlug(slug) is { } s ? Slug(s) : null;

    /// <summary>The styles set in <paramref name="value"/>, in display order.</summary>
    /// <param name="value">Any combination.</param>
    /// <returns>The single styles it holds.</returns>
    public static IReadOnlyList<BoulderStyle> Split(BoulderStyle value) => All.Where(s => value.HasFlag(s)).ToList();

    /// <summary>Keeps only the known bits, so a stray value can never be stored.</summary>
    /// <param name="value">Any value.</param>
    /// <returns>The value restricted to the fixed set.</returns>
    public static BoulderStyle Normalize(BoulderStyle value) => value & Mask;

    /// <summary>The names of the styles in <paramref name="value"/>, as the offline document carries them.</summary>
    /// <param name="value">Any combination.</param>
    /// <returns>The names, or null when none is set (the document leaves the field out).</returns>
    public static IReadOnlyList<string>? ToNames(BoulderStyle value) =>
        Split(value) is { Count: > 0 } set ? set.Select(Label).ToList() : null;

    /// <summary>The styles the document's names stand for; an unknown name is ignored.</summary>
    /// <param name="names">The names, or null.</param>
    /// <returns>The combination.</returns>
    public static BoulderStyle FromNames(IEnumerable<string>? names) =>
        (names ?? []).Select(FromSlug).Where(s => s is not null).Aggregate(BoulderStyle.None, (acc, s) => acc | s!.Value);

    /// <summary>How many of <paramref name="boulders"/> carry each style (a boulder counts once under each of its styles).</summary>
    /// <param name="boulders">The styles of each boulder.</param>
    /// <returns>The count per style, for every style in <see cref="All"/>.</returns>
    public static IReadOnlyDictionary<BoulderStyle, int> CountBy(IEnumerable<BoulderStyle> boulders)
    {
        var counts = All.ToDictionary(s => s, _ => 0);
        foreach (var value in boulders)
        {
            foreach (var style in All.Where(s => value.HasFlag(s)))
            {
                counts[style]++;
            }
        }

        return counts;
    }
}
