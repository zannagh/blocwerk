// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Contracts.Documents;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Helpers;

namespace Blocwerk.Client.Views;

/// <summary>
/// A cached boulder's effective hold type, by the Server's rule (<see cref="BoulderHoldTypes.Effective"/>): the type set on
/// it (the boulders document's <c>HoldType</c>), else the one its gripped holds show in the holds document. Without the
/// holds document only a type set on the boulder counts.
/// </summary>
public static class DocumentHoldTypes
{
    /// <summary>The holds document's holds by id, for <see cref="Of"/>.</summary>
    /// <param name="holds">The holds document, if the device holds it.</param>
    /// <returns>The holds by id.</returns>
    public static IReadOnlyDictionary<Guid, HoldsDocumentHold> Index(HoldsDocument? holds) =>
        (holds?.Holds ?? []).GroupBy(h => h.Id).ToDictionary(g => g.Key, g => g.First());

    /// <summary>The effective hold type of one boulder.</summary>
    /// <param name="boulder">The boulder.</param>
    /// <param name="holds">The holds by id (<see cref="Index"/>).</param>
    /// <returns>The type and whether it is inferred, or null.</returns>
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
                gripped.Add(BoulderHoldTypes.FromSlug(hold.HandType));
            }
        }

        return BoulderHoldTypes.Effective(BoulderHoldTypes.FromSlug(boulder.HoldType), gripped);
    }
}
