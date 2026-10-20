// <copyright file="HoldStackMemo.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;
using Blocwerk.Core.HoldStacks;

namespace Blocwerk.Web.Components.Shared;

/// <summary>
/// The wall editor's stack map and drawing order for one state of its working copy. The editor renders on every
/// state change; rebuilding both for hundreds of holds each time is waste when no hold was added, removed,
/// replaced or put on another one. <see cref="Matches"/> compares the hold objects themselves and their bases,
/// exactly, so a Discard (fresh objects) or a restack always builds them again.
/// </summary>
internal sealed class HoldStackMemo
{
    private readonly Hold[] holds;
    private readonly Guid?[] bases;

    private HoldStackMemo(Hold[] snapshot, Dictionary<Guid, Guid?> on, List<Hold> order)
    {
        holds = snapshot;
        bases = [.. snapshot.Select(h => h.StackedOnHoldId)];
        On = on;
        Order = order;
    }

    /// <summary>Gets hold id to the id of the hold it sits on.</summary>
    public Dictionary<Guid, Guid?> On { get; }

    /// <summary>Gets the holds with every base before what sits on it.</summary>
    public List<Hold> Order { get; }

    /// <summary>Builds both for the holds as they are now.</summary>
    /// <param name="current">The working copy.</param>
    /// <returns>The memo.</returns>
    public static HoldStackMemo For(IReadOnlyList<Hold> current)
    {
        var on = current.GroupBy(h => h.Id).ToDictionary(g => g.Key, g => g.First().StackedOnHoldId);
        var order = HoldStackOrder.BackToFront(current, h => h.Id, h => h.StackedOnHoldId);
        return new HoldStackMemo([.. current], on, order);
    }

    /// <summary>Whether the working copy still has exactly these holds, in this order, on these bases.</summary>
    /// <param name="current">The working copy.</param>
    /// <returns>True when the memo still holds.</returns>
    public bool Matches(IReadOnlyList<Hold> current)
    {
        if (current.Count != holds.Length)
        {
            return false;
        }

        for (int i = 0; i < holds.Length; i++)
        {
            if (!ReferenceEquals(current[i], holds[i]) || current[i].StackedOnHoldId != bases[i])
            {
                return false;
            }
        }

        return true;
    }
}
