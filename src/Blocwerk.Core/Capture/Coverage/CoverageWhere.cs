// <copyright file="CoverageWhere.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Geometry;

namespace Blocwerk.Core.Capture.Coverage;

/// <summary>Places on a facet and facet names in plain words, for the "what to add" list.</summary>
public static class CoverageWhere
{
    /// <summary>
    /// Where (a, b) lies on the region, by thirds: "the bottom left", "the top", "the centre", "the right", … (a runs
    /// to the right as you face the facet, b up it).
    /// </summary>
    /// <param name="region">The facet's region.</param>
    /// <param name="a">Along u, mm.</param>
    /// <param name="b">Along v, mm.</param>
    /// <returns>The place.</returns>
    public static string Describe(PlaneRectMm region, double a, double b)
    {
        var horizontal = Horizontal(region, a);
        var vertical = Third((b - region.BMin) / Math.Max(1, region.Height)) switch
        {
            0 => "bottom",
            2 => "top",
            _ => null,
        };
        return (vertical, horizontal) switch
        {
            (null, null) => "the centre",
            (null, { } h) => $"the {h}",
            ({ } v, null) => $"the {v}",
            ({ } v, { } h) => $"the {v} {h}",
        };
    }

    /// <summary>"left", "right" or null (the middle third) for a position along the facet.</summary>
    /// <param name="region">The facet's region.</param>
    /// <param name="a">Along u, mm.</param>
    /// <returns>The side, or null.</returns>
    public static string? Horizontal(PlaneRectMm region, double a) =>
        Third((a - region.AMin) / Math.Max(1, region.Width)) switch
        {
            0 => "left",
            2 => "right",
            _ => null,
        };

    /// <summary>The facet's name for a sentence: "the main wall", "the kickboard (1a)".</summary>
    /// <param name="name">The segment name.</param>
    /// <returns>The phrase.</returns>
    public static string Facet(string name)
    {
        var trimmed = name.Trim();
        if (trimmed.Length == 0)
        {
            return "the wall";
        }

        // "Main wall" → "main wall", but keep "ABC" or "Cave" names that are not sentence-cased words as typed.
        var lower = trimmed.Length > 1 && char.IsUpper(trimmed[0]) && !char.IsUpper(trimmed[1])
            ? char.ToLowerInvariant(trimmed[0]) + trimmed[1..]
            : trimmed;
        return lower.StartsWith("the ", StringComparison.OrdinalIgnoreCase) ? lower : $"the {lower}";
    }

    /// <summary>"volume 4", "volumes 3 and 5", "volumes 3, 5 and 6".</summary>
    /// <param name="numbers">The volume numbers.</param>
    /// <returns>The phrase.</returns>
    public static string Volumes(IReadOnlyList<int> numbers) =>
        numbers.Count == 1 ? $"volume {numbers[0]}" : $"volumes {List(numbers.Select(n => n.ToString(System.Globalization.CultureInfo.InvariantCulture)))}";

    /// <summary>"a", "a and b", "a, b and c".</summary>
    /// <param name="items">The items.</param>
    /// <returns>The phrase.</returns>
    public static string List(IEnumerable<string> items)
    {
        var list = items.ToList();
        return list.Count <= 1 ? string.Concat(list) : $"{string.Join(", ", list.Take(list.Count - 1))} and {list[^1]}";
    }

    private static int Third(double t) => t < 1.0 / 3 ? 0 : t > 2.0 / 3 ? 2 : 1;
}
