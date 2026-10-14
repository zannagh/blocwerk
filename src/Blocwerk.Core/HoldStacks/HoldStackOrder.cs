// <copyright file="HoldStackOrder.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.HoldStacks;

/// <summary>
/// The order of stacked holds, from "hold id to the id of the hold it sits on": which hold is in front (drawn last,
/// tapped first), what sits on a hold and what it sits on. A base that is not among the holds reads as no base, and a
/// chain is followed for at most as many steps as there are holds, so bad data can never hang a page. Pure.
/// </summary>
public static class HoldStackOrder
{
    /// <summary>The holds a hold sits on, nearest first.</summary>
    /// <param name="on">Hold id to its base's id.</param>
    /// <param name="id">The hold.</param>
    /// <returns>Its base, that hold's base, and so on.</returns>
    public static List<Guid> Below(IReadOnlyDictionary<Guid, Guid?> on, Guid id)
    {
        var chain = new List<Guid>();
        var seen = new HashSet<Guid> { id };
        while (on.TryGetValue(id, out var next) && next is { } lower && on.ContainsKey(lower) && seen.Add(lower))
        {
            chain.Add(lower);
            id = lower;
        }

        return chain;
    }

    /// <summary>Everything that sits on a hold, at any depth.</summary>
    /// <param name="on">Hold id to its base's id.</param>
    /// <param name="id">The hold.</param>
    /// <returns>The ids, in the order of <paramref name="on"/>.</returns>
    public static List<Guid> Above(IReadOnlyDictionary<Guid, Guid?> on, Guid id) =>
        [.. on.Keys.Where(other => other != id && Below(on, other).Contains(id))];

    /// <summary>Whether putting <paramref name="upper"/> on <paramref name="lower"/> would make a chain loop.</summary>
    /// <param name="on">Hold id to its base's id, as it is now.</param>
    /// <param name="upper">The hold to put on top.</param>
    /// <param name="lower">Its new base.</param>
    /// <returns>True for a hold on itself, or on something that already sits on it.</returns>
    public static bool WouldLoop(IReadOnlyDictionary<Guid, Guid?> on, Guid upper, Guid lower) =>
        upper == lower || Below(on, lower).Contains(upper);

    /// <summary>The holds with every base before what sits on it; otherwise in the order given.</summary>
    /// <typeparam name="T">A hold.</typeparam>
    /// <param name="holds">The holds of one photo.</param>
    /// <param name="id">A hold's id.</param>
    /// <param name="stackedOn">The id of the hold it sits on.</param>
    /// <returns>The holds back to front.</returns>
    public static List<T> BackToFront<T>(IEnumerable<T> holds, Func<T, Guid> id, Func<T, Guid?> stackedOn)
    {
        var list = holds as IReadOnlyList<T> ?? [.. holds];
        if (!list.Any(h => stackedOn(h) is not null))
        {
            return [.. list];
        }

        var on = new Dictionary<Guid, Guid?>();
        foreach (var hold in list)
        {
            on[id(hold)] = stackedOn(hold);
        }

        return [.. list.Select((h, i) => (Hold: h, Index: i, Depth: Below(on, id(h)).Count)).OrderBy(e => e.Depth).ThenBy(e => e.Index).Select(e => e.Hold)];
    }
}
