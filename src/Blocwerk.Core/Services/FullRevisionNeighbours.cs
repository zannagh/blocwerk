// <copyright file="FullRevisionNeighbours.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Enums;
using Blocwerk.Core.Refresh;

namespace Blocwerk.Core.Services;

/// <summary>
/// Holds a neighbour photo shows again in a full revision. Every centre hold is new there, and the neighbour-panel rule
/// spares a detection on a kept new centre hold (it expects the pair to be linked), but the quick review links only
/// the pairs the overlap matcher is sure of, so on The Attic 296 right-panel holds became a second copy of a centre
/// hold. Here the matcher's own pairs decide: a neighbour detection the matcher pairs with a centre hold is that hold
/// seen again. A sure pair stays and is linked as before; any other pair is left out (the centre keeps the hold), so a
/// spot the matcher did not pair keeps its detection.
/// </summary>
internal static class FullRevisionNeighbours
{
    /// <summary>
    /// The triage's discards plus every neighbour detection of an overlap pair the quick review does not link (below
    /// <see cref="QuickUpdateDefaults.OverlapThreshold"/>, not moved, its centre hold kept), as seen on the neighbour panel.
    /// </summary>
    /// <param name="discards">The triage's suggested discards.</param>
    /// <param name="neighbours">The neighbour panels' overlap proposals.</param>
    /// <returns>The discards to suggest.</returns>
    public static Dictionary<Guid, NewHoldDiscardReason> WithPairedTwins(
        IReadOnlyDictionary<Guid, NewHoldDiscardReason> discards, IReadOnlyList<NeighbourOverlap> neighbours)
    {
        ArgumentNullException.ThrowIfNull(discards);
        ArgumentNullException.ThrowIfNull(neighbours);
        var result = discards.ToDictionary(d => d.Key, d => d.Value);
        var pairs = neighbours.SelectMany(n => n.Proposals).Where(p => !p.Moved && !discards.ContainsKey(p.HoldAId)).ToList();
        var linked = pairs.Where(p => p.Confidence >= QuickUpdateDefaults.OverlapThreshold).Select(p => p.HoldBId).ToHashSet();
        foreach (var pair in pairs.Where(p => !linked.Contains(p.HoldBId)))
        {
            result.TryAdd(pair.HoldBId, NewHoldDiscardReason.SeenOnNeighbourPanel);
        }

        return result;
    }
}
