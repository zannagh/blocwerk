// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Enums;

namespace Blocwerk.Core.Helpers;

/// <summary>
/// The boulder browser's hold types, the same on the Server and in the offline client: the five grips and the two hooks in
/// card order, their names and URL values, and a boulder's prominent types. Those are the ones set on the boulder when there
/// are any; otherwise the one grip its hand holds show, when enough of them have a type to tell (<see cref="Infer"/>).
/// </summary>
public static class BoulderHoldTypes
{
    /// <summary>
    /// Gets every hold type in the order the cards and pickers show them: the ONE place a type is defined. Adding one means
    /// a new flag in <see cref="BoulderHoldType"/> (the next free bit, so stored values keep their meaning) and a line here.
    /// </summary>
    public static IReadOnlyList<BrowseDefinition<BoulderHoldType>> Definitions { get; } =
    [
        new(BoulderHoldType.Jug, "Jug", "<path d=\"M4 8h16\"/><path d=\"M6 8c0 6 2.5 9 6 9s6-3 6-9\"/>", "Large, easy to grip"),
        new(BoulderHoldType.Crimp, "Crimp", "<path d=\"M3 13h18l-2 3H5z\"/><path d=\"M8 13V8M12 13V7M16 13V8\"/>", "Small edges held with the fingertips"),
        new(BoulderHoldType.Pinch, "Pinch", "<path d=\"M8 4c-3 5-3 11 0 16\"/><path d=\"M16 4c3 5 3 11 0 16\"/><path d=\"M12 8v8\"/>",
            "Squeezed between thumb and fingers"),
        new(BoulderHoldType.Sloper, "Sloper", "<path d=\"M3 18c1-7 5-11 9-11s8 4 9 11z\"/>", "Rounded, held by friction"),
        new(BoulderHoldType.Pocket, "Pocket", "<circle cx=\"12\" cy=\"12\" r=\"9\"/><circle cx=\"12\" cy=\"12\" r=\"3\"/>", "Hollows for one or more fingers"),
        new(BoulderHoldType.HeelHook, "Heel hook", Shoe(0) + "<path d=\"M13 21.5a4 3.5 0 0 1 8 0z\"/>", "Pulling with the heel on a hold"),
        new(BoulderHoldType.ToeHook, "Toe hook", Shoe(4) + "<path d=\"M10 10H1a4.5 4 0 0 0 9 0z\"/>", "Pulling with the top of the toes under a hold"),
    ];

    /// <summary>Gets the types in the order the cards and pickers show them.</summary>
    public static IReadOnlyList<BoulderHoldType> All { get; } = Definitions.Select(d => d.Value).ToList();

    /// <summary>Gets every type at once, the mask that drops unknown bits.</summary>
    public static BoulderHoldType Mask { get; } = All.Aggregate(BoulderHoldType.None, (acc, t) => acc | t);

    /// <summary>Gets the five grips: the types a hold can have, and the only ones a 0.7.0 client knows.</summary>
    public static BoulderHoldType Grips { get; } =
        BoulderHoldType.Jug | BoulderHoldType.Crimp | BoulderHoldType.Pinch | BoulderHoldType.Sloper | BoulderHoldType.Pocket;

    /// <summary>The display label of one type.</summary>
    /// <param name="type">A single type.</param>
    /// <returns>Its name.</returns>
    public static string Label(BoulderHoldType type) => Definitions.FirstOrDefault(d => d.Value == type)?.Label ?? type.ToString();

    /// <summary>The icon of one type (see <see cref="BrowseDefinition{T}.Icon"/>).</summary>
    /// <param name="type">A single type.</param>
    /// <returns>The SVG content.</returns>
    public static string Icon(BoulderHoldType type) => Definitions.FirstOrDefault(d => d.Value == type)?.Icon ?? string.Empty;

    /// <summary>What one type means, for a tooltip.</summary>
    /// <param name="type">A single type.</param>
    /// <returns>The description.</returns>
    public static string Description(BoulderHoldType type) => Definitions.FirstOrDefault(d => d.Value == type)?.Description ?? string.Empty;

    /// <summary>The URL value of one type (<c>crimp</c>, <c>heel-hook</c>).</summary>
    /// <param name="type">A single type.</param>
    /// <returns>The slug.</returns>
    public static string Slug(BoulderHoldType type) => Definitions.FirstOrDefault(d => d.Value == type)?.Slug ?? type.ToString().ToLowerInvariant();

    /// <summary>The single type a URL value, a document name or an enum name stands for, or null for anything else.</summary>
    /// <param name="slug">The value, any case: <c>heel-hook</c>, <c>Heel hook</c> and <c>HeelHook</c> all name the heel hook.</param>
    /// <returns>The type, or null.</returns>
    public static BoulderHoldType? FromSlug(string? slug)
    {
        var key = slug?.Trim().Replace(" ", string.Empty).Replace("-", string.Empty);
        return Definitions.FirstOrDefault(d => string.Equals(d.Value.ToString(), key, StringComparison.OrdinalIgnoreCase))?.Value;
    }

    /// <summary>The boulder type of a hold's grip.</summary>
    /// <param name="hand">The hold's hand type.</param>
    /// <returns>The matching grip.</returns>
    public static BoulderHoldType FromHand(HoldHandType hand) => hand switch
    {
        HoldHandType.Jug => BoulderHoldType.Jug,
        HoldHandType.Crimp => BoulderHoldType.Crimp,
        HoldHandType.Pinch => BoulderHoldType.Pinch,
        HoldHandType.Sloper => BoulderHoldType.Sloper,
        HoldHandType.Pocket => BoulderHoldType.Pocket,
        _ => BoulderHoldType.None,
    };

    /// <summary>The single types set in <paramref name="value"/>, in display order.</summary>
    /// <param name="value">Any combination.</param>
    /// <returns>The types it holds.</returns>
    public static IReadOnlyList<BoulderHoldType> Split(BoulderHoldType value) => All.Where(t => value.HasFlag(t)).ToList();

    /// <summary>Keeps only the known bits.</summary>
    /// <param name="value">Any value.</param>
    /// <returns>The value restricted to the fixed set.</returns>
    public static BoulderHoldType Normalize(BoulderHoldType value) => value & Mask;

    /// <summary>The names of the types in <paramref name="value"/>, as the offline document and requests carry them.</summary>
    /// <param name="value">Any combination.</param>
    /// <returns>The labels, in display order; empty for none.</returns>
    public static IReadOnlyList<string> ToNames(BoulderHoldType value) => Split(value).Select(Label).ToList();

    /// <summary>The types a list of names stands for; an unknown name is ignored.</summary>
    /// <param name="names">The names, or null.</param>
    /// <returns>The combination.</returns>
    public static BoulderHoldType FromNames(IEnumerable<string?>? names) =>
        (names ?? []).Select(FromSlug).Aggregate(BoulderHoldType.None, (acc, t) => acc | (t ?? BoulderHoldType.None));

    /// <summary>Whether a hold of a boulder counts towards its type: a hand hold the boulder does not restrict to feet.</summary>
    /// <param name="category">The hold's category.</param>
    /// <param name="usage">Its usage in the boulder.</param>
    /// <returns>True when it is gripped.</returns>
    public static bool IsGripped(HoldCategory category, HoldUsage usage) => category == HoldCategory.Hand && usage != HoldUsage.FootOnly;

    /// <summary>
    /// The grip a boulder's gripped holds show: the most common one among those with a type, but only when at least half
    /// of them have a type and one type is more common than every other (a tie tells nothing). Never a hook.
    /// </summary>
    /// <param name="gripped">The type of each gripped hold, null where none is set.</param>
    /// <returns>The type, or null.</returns>
    public static BoulderHoldType? Infer(IReadOnlyCollection<HoldHandType?> gripped)
    {
        var typed = gripped.Where(t => t.HasValue).Select(t => FromHand(t!.Value)).Where(t => t != BoulderHoldType.None).ToList();
        if (typed.Count == 0 || typed.Count * 2 < gripped.Count)
        {
            return null;
        }

        var ranked = typed.GroupBy(t => t).Select(g => (Type: g.Key, Count: g.Count())).OrderByDescending(g => g.Count).ToList();
        return ranked.Count > 1 && ranked[0].Count == ranked[1].Count ? null : ranked[0].Type;
    }

    /// <summary>The types the browser files a boulder under: the ones set on it, else the one its holds show.</summary>
    /// <param name="set">The types set on the boulder; <see cref="BoulderHoldType.None"/> leaves it to the holds.</param>
    /// <param name="gripped">The type of each of its gripped holds.</param>
    /// <returns>The types and where they come from, or null when there are none.</returns>
    public static EffectiveHoldType? Effective(BoulderHoldType set, IReadOnlyCollection<HoldHandType?> gripped) =>
        Normalize(set) is var chosen && chosen != BoulderHoldType.None ? new EffectiveHoldType(chosen, false)
        : Infer(gripped) is { } inferred ? new EffectiveHoldType(inferred, true)
        : null;

    /// <summary>How many families each type counts: a family counts once under each type a published member has.</summary>
    /// <param name="families">The hold types of each family.</param>
    /// <returns>The count per type, for every type in <see cref="Definitions"/>.</returns>
    public static IReadOnlyDictionary<BoulderHoldType, int> CountBy(IEnumerable<BoulderHoldType> families)
    {
        var counts = All.ToDictionary(t => t, _ => 0);
        foreach (var value in families)
        {
            foreach (var type in All.Where(t => value.HasFlag(t)))
            {
                counts[type]++;
            }
        }

        return counts;
    }

    // A climbing shoe from the side, toe left, heel right, lowered by dy: the hooks' icons put a hold under its heel or
    // over its toe.
    private static string Shoe(int dy) =>
        $"<path d=\"M3 {16 + dy}c0-2 2-3 5-3.5l4-1.5 1.5-2.5h5.5l1 5.5c0 1.5-1 2.5-2.5 2.5H4c-.6 0-1-.4-1-1z\"/>";
}
