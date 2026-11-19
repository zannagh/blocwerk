// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Contracts.Documents;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Helpers;

namespace Blocwerk.Client.Views;

/// <summary>
/// A cached boulder's effective hold types, by the Server's rule (<see cref="BoulderHoldTypes.Effective"/>): the types set on
/// it (the boulders document's <c>HoldTypes</c>, or its single <c>HoldType</c> from a Server before them), else the one its
/// gripped holds show in the holds document. Without the holds document only types set on the boulder count.
/// </summary>
public static class DocumentHoldTypes
{
    /// <summary>The holds document's holds by id, for <see cref="Of"/>.</summary>
    /// <param name="holds">The holds document, if the device holds it.</param>
    /// <returns>The holds by id.</returns>
    public static IReadOnlyDictionary<Guid, HoldsDocumentHold> Index(HoldsDocument? holds) =>
        (holds?.Holds ?? []).GroupBy(h => h.Id).ToDictionary(g => g.Key, g => g.First());

    /// <summary>The effective hold types of one boulder.</summary>
    /// <param name="boulder">The boulder.</param>
    /// <param name="holds">The holds by id (<see cref="Index"/>).</param>
    /// <returns>The types and whether they are inferred, or null.</returns>
    public static EffectiveHoldType? Of(BouldersDocumentBoulder boulder, IReadOnlyDictionary<Guid, HoldsDocumentHold> holds)
    {
        var gripped = new List<HoldHandType?>();
        foreach (var bh in boulder.Holds)
        {
            if (holds.TryGetValue(bh.HoldId, out var hold)
                && Enum.TryParse<HoldCategory>(hold.Category, out var category)
                && Enum.TryParse<HoldUsage>(bh.Usage, out var usage)
                && BoulderHoldTypes.IsGripped(category, usage))
            {
                gripped.Add(Enum.TryParse<HoldHandType>(hold.HandType, ignoreCase: true, out var hand) ? hand : null);
            }
        }

        var set = boulder.HoldTypes is { } names ? BoulderHoldTypes.FromNames(names) : BoulderHoldTypes.FromNames([boulder.HoldType]);
        return BoulderHoldTypes.Effective(set, gripped);
    }
}
