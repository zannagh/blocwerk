// <copyright file="HoldReviewOrdering.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Services;

/// <summary>
/// Ordering for the wall-update review queues: holds that a LIVE boulder is built from come first, so
/// the consequential work is done while the user is still paying attention. A mis-carried hold that no
/// boulder uses costs nothing; a mis-carried hold under a boulder freezes it.
/// <para>
/// The membership set is derived once per component from <see cref="IBoulderService.GetHoldUsageAsync"/>
/// — one query per wall, already twin-expanded through the wall's hold links — and then only read.
/// Historic boulders are deliberately excluded: they are already retired, so a hold that only they use
/// is no more consequential than an unused one. Drafts DO count; they are live work in progress.
/// </para>
/// </summary>
public static class HoldReviewOrdering
{
    /// <summary>
    /// Reduces a wall's hold-usage map to the ids of holds used by at least one non-historic boulder.
    /// </summary>
    /// <param name="usage">The wall's hold usage, as <see cref="IBoulderService.GetHoldUsageAsync"/> returns it.</param>
    /// <returns>The hold ids that carry a live boulder.</returns>
    public static HashSet<Guid> LiveBoulderHoldIds(IReadOnlyDictionary<Guid, List<HoldUsageRef>> usage)
    {
        var ids = new HashSet<Guid>();
        foreach (var (holdId, refs) in usage)
        {
            if (refs.Any(r => !r.IsHistoric))
            {
                ids.Add(holdId);
            }
        }

        return ids;
    }

    /// <summary>True when the hold carries a live boulder. A null id (a brand-new hold) never does.</summary>
    /// <param name="holdId">The hold being ranked, or null when the item has no old hold.</param>
    /// <param name="boulderHoldIds">The membership set from <see cref="LiveBoulderHoldIds"/>.</param>
    /// <returns>Whether the hold is part of a live boulder.</returns>
    public static bool IsBoulderHold(Guid? holdId, IReadOnlySet<Guid> boulderHoldIds) =>
        holdId is { } id && boulderHoldIds.Contains(id);

    /// <summary>
    /// Orders items boulder-first, leaving the incoming order intact within each group (LINQ ordering
    /// is stable), so a caller that already has a meaningful order only gets the boulder holds lifted.
    /// </summary>
    /// <typeparam name="T">The queue item type.</typeparam>
    /// <param name="items">The queue.</param>
    /// <param name="holdIdOf">The hold each item is about.</param>
    /// <param name="boulderHoldIds">The membership set from <see cref="LiveBoulderHoldIds"/>.</param>
    /// <returns>The reordered queue.</returns>
    public static List<T> BoulderFirst<T>(
        IEnumerable<T> items,
        Func<T, Guid?> holdIdOf,
        IReadOnlySet<Guid> boulderHoldIds) =>
        items.OrderByDescending(item => IsBoulderHold(holdIdOf(item), boulderHoldIds)).ToList();

    /// <summary>
    /// Orders items boulder-first, then by the caller's own ranking DESCENDING — the shape every
    /// existing review queue had before boulder membership became the primary key.
    /// </summary>
    /// <typeparam name="T">The queue item type.</typeparam>
    /// <typeparam name="TKey">The secondary ranking key (residual, confidence, …).</typeparam>
    /// <param name="items">The queue.</param>
    /// <param name="holdIdOf">The hold each item is about.</param>
    /// <param name="rankOf">The previous ranking, still applied within each group.</param>
    /// <param name="boulderHoldIds">The membership set from <see cref="LiveBoulderHoldIds"/>.</param>
    /// <returns>The reordered queue.</returns>
    public static List<T> BoulderFirstThenByDescending<T, TKey>(
        IEnumerable<T> items,
        Func<T, Guid?> holdIdOf,
        Func<T, TKey> rankOf,
        IReadOnlySet<Guid> boulderHoldIds) =>
        items
            .OrderByDescending(item => IsBoulderHold(holdIdOf(item), boulderHoldIds))
            .ThenByDescending(rankOf)
            .ToList();
}
