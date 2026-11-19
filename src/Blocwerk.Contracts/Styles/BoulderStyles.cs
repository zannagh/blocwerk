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
    /// <summary>
    /// Gets every style in the order the cards and pickers show them: the ONE place a style is defined. Adding a style means
    /// a new flag in <see cref="BoulderStyle"/> (the next free bit, so stored values keep their meaning) and a line here.
    /// </summary>
    public static IReadOnlyList<BrowseDefinition<BoulderStyle>> Definitions { get; } =
    [
        new(BoulderStyle.Warmup, "Warmup", "<path d=\"M12 22a6 6 0 0 0 6-6c0-4-3-6-4-10-2 2-3 4-3 6-1-1-2-2-2-3-2 2-3 4-3 7a6 6 0 0 0 6 6z\"/>"),
        new(BoulderStyle.Tension, "Tension", "<path d=\"M3 12h18\"/><path d=\"M7 8l-4 4 4 4\"/><path d=\"M17 8l4 4-4 4\"/>"),
        new(BoulderStyle.Dynamic, "Dynamic", "<path d=\"M4 19c2-8 7-12 16-13\"/><path d=\"M15 3l5 3-3 5\"/>"),
        new(BoulderStyle.Funky, "Funky", "<path d=\"M2 13c2.5-7 5-7 7.5 0s5 7 7.5 0c1.2-3 2.4-4 5-3\"/>"),
        new(BoulderStyle.Endurance, "Endurance", "<rect x=\"2\" y=\"8\" width=\"17\" height=\"8\" rx=\"2\"/><path d=\"M22 11v2\"/><path d=\"M6 12h6\"/>"),
        new(BoulderStyle.Power, "Power", "<path d=\"M13 2L4 14h7l-1 8 9-12h-7z\"/>"),
        new(BoulderStyle.Technical, "Technical", "<circle cx=\"12\" cy=\"12\" r=\"3\"/><path d=\"M12 2v3M12 19v3M2 12h3M19 12h3M4.9 4.9l2.1 2.1M17 17l2.1 2.1M4.9 19.1L7 17M17 7l2.1-2.1\"/>"),
    ];

    /// <summary>Gets the styles in the order the cards and pickers show them.</summary>
    public static IReadOnlyList<BoulderStyle> All { get; } = Definitions.Select(d => d.Value).ToList();

    /// <summary>Gets every style at once, the mask that drops unknown bits.</summary>
    public static BoulderStyle Mask { get; } = All.Aggregate(BoulderStyle.None, (acc, s) => acc | s);

    /// <summary>The display label of one style.</summary>
    /// <param name="style">A single style.</param>
    /// <returns>Its name.</returns>
    public static string Label(BoulderStyle style) => Definitions.FirstOrDefault(d => d.Value == style)?.Label ?? style.ToString();

    /// <summary>The icon of one style (see <see cref="BrowseDefinition{T}.Icon"/>); empty for anything but a single style.</summary>
    /// <param name="style">A single style.</param>
    /// <returns>The SVG content.</returns>
    public static string Icon(BoulderStyle style) => Definitions.FirstOrDefault(d => d.Value == style)?.Icon ?? string.Empty;

    /// <summary>The URL value of one style (lower case, the label).</summary>
    /// <param name="style">A single style.</param>
    /// <returns>The slug.</returns>
    public static string Slug(BoulderStyle style) => Label(style).ToLowerInvariant();

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
