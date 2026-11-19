// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Enums;

namespace Blocwerk.Core.Helpers;

/// <summary>
/// The boulder browser's hold types, the same on the Server and in the offline client: the five grip types in card order,
/// their names and URL values, and a boulder's dominant type. That is the one set on the boulder when there is one;
/// otherwise the one its hand holds show, when enough of them have a type to tell (<see cref="Infer"/>).
/// </summary>
public static class BoulderHoldTypes
{
    /// <summary>Gets the hold types in the order the cards and pickers show them.</summary>
    public static IReadOnlyList<BrowseDefinition<HoldHandType>> Definitions { get; } =
    [
        new(HoldHandType.Jug, "Jug", "<path d=\"M4 8h16\"/><path d=\"M6 8c0 6 2.5 9 6 9s6-3 6-9\"/>"),
        new(HoldHandType.Crimp, "Crimp", "<path d=\"M3 13h18l-2 3H5z\"/><path d=\"M8 13V8M12 13V7M16 13V8\"/>"),
        new(HoldHandType.Pinch, "Pinch", "<path d=\"M8 4c-3 5-3 11 0 16\"/><path d=\"M16 4c3 5 3 11 0 16\"/><path d=\"M12 8v8\"/>"),
        new(HoldHandType.Sloper, "Sloper", "<path d=\"M3 18c1-7 5-11 9-11s8 4 9 11z\"/>"),
        new(HoldHandType.Pocket, "Pocket", "<circle cx=\"12\" cy=\"12\" r=\"9\"/><circle cx=\"12\" cy=\"12\" r=\"3\"/>"),
    ];

    /// <summary>The display label of a hold type.</summary>
    /// <param name="type">The type.</param>
    /// <returns>Its name.</returns>
    public static string Label(HoldHandType type) => Definitions.FirstOrDefault(d => d.Value == type)?.Label ?? type.ToString();

    /// <summary>The icon of a hold type (see <see cref="BrowseDefinition{T}.Icon"/>).</summary>
    /// <param name="type">The type.</param>
    /// <returns>The SVG content.</returns>
    public static string Icon(HoldHandType type) => Definitions.FirstOrDefault(d => d.Value == type)?.Icon ?? string.Empty;

    /// <summary>The URL value of a hold type.</summary>
    /// <param name="type">The type.</param>
    /// <returns>The slug.</returns>
    public static string Slug(HoldHandType type) => Label(type).ToLowerInvariant();

    /// <summary>The hold type a URL value or a document name stands for, or null for anything else.</summary>
    /// <param name="slug">The value, any case.</param>
    /// <returns>The type, or null.</returns>
    public static HoldHandType? FromSlug(string? slug) =>
        Definitions.FirstOrDefault(d => string.Equals(d.Slug, slug?.Trim(), StringComparison.OrdinalIgnoreCase))?.Value;

    /// <summary>Whether a hold of a boulder counts towards its type: a hand hold the boulder does not restrict to feet.</summary>
    /// <param name="category">The hold's category.</param>
    /// <param name="usage">Its usage in the boulder.</param>
    /// <returns>True when it is gripped.</returns>
    public static bool IsGripped(HoldCategory category, HoldUsage usage) => category == HoldCategory.Hand && usage != HoldUsage.FootOnly;

    /// <summary>
    /// The type a boulder's gripped holds show: the most common one among those with a type, but only when at least half
    /// of them have a type and one type is more common than every other (a tie tells nothing).
    /// </summary>
    /// <param name="gripped">The type of each gripped hold, null where none is set.</param>
    /// <returns>The type, or null.</returns>
    public static HoldHandType? Infer(IReadOnlyCollection<HoldHandType?> gripped)
    {
        var typed = gripped.Where(t => t.HasValue).Select(t => t!.Value).ToList();
        if (typed.Count == 0 || typed.Count * 2 < gripped.Count)
        {
            return null;
        }

        var ranked = typed.GroupBy(t => t).Select(g => (Type: g.Key, Count: g.Count())).OrderByDescending(g => g.Count).ToList();
        return ranked.Count > 1 && ranked[0].Count == ranked[1].Count ? null : ranked[0].Type;
    }

    /// <summary>The type the browser files a boulder under: the one set on it, else the one its holds show.</summary>
    /// <param name="dominant">The type set on the boulder, if any.</param>
    /// <param name="gripped">The type of each of its gripped holds.</param>
    /// <returns>The type and where it comes from, or null when there is none.</returns>
    public static EffectiveHoldType? Effective(HoldHandType? dominant, IReadOnlyCollection<HoldHandType?> gripped) =>
        dominant is { } set ? new EffectiveHoldType(set, false)
        : Infer(gripped) is { } inferred ? new EffectiveHoldType(inferred, true)
        : null;

    /// <summary>How many families each hold type counts: a family counts once under each type a published member has.</summary>
    /// <param name="families">The hold types of each family.</param>
    /// <returns>The count per type, for every type in <see cref="Definitions"/>.</returns>
    public static IReadOnlyDictionary<HoldHandType, int> CountBy(IEnumerable<IReadOnlyCollection<HoldHandType>?> families)
    {
        var counts = Definitions.ToDictionary(d => d.Value, _ => 0);
        foreach (var type in families.SelectMany(f => (f ?? []).Distinct()).Where(counts.ContainsKey))
        {
            counts[type]++;
        }

        return counts;
    }
}
