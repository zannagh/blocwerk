// <copyright file="WallUsageHeat.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;

namespace Blocwerk.Core.Services;

/// <summary>
/// How often each hold is used by the wall's boulders, for the usage heat map.
/// <para>
/// Only boulders a climber can actually climb count: published (not a draft), not archived and not
/// historic. The same rule applies to every viewer, so a share-link viewer sees exactly the map a
/// member sees. Each boulder counts once per physical hold, whatever its role (start, top, normal)
/// or usage (hand, foot) there, so the number reads as "how many boulders touch this hold". On a
/// multi-panel wall the count is shared by every twin of a linked hold, and a boulder that saved
/// both twins is counted once.
/// </para>
/// <para>
/// A boulder with <see cref="Boulder.KickboardFootholdsOn"/> also uses every hold marked as on the
/// kickboard, even though those holds are not in its own hold list. A kickboard hold the boulder
/// lists explicitly is still counted once.
/// </para>
/// </summary>
public static class WallUsageHeat
{
    /// <summary>Counts distinct usable boulders per hold id; holds nobody uses are absent.</summary>
    public static Dictionary<Guid, int> CountByHold(
        IEnumerable<Boulder> boulders,
        IReadOnlyCollection<HoldLinkPair> links,
        IReadOnlyCollection<Guid>? kickboardHoldIds = null)
    {
        var counts = new Dictionary<Guid, int>();
        foreach (var boulder in boulders)
        {
            if (boulder.IsArchived || boulder.IsDraft || boulder.IsHistoric)
            {
                continue;
            }

            var physical = BoulderHoldReconciler.Reconcile(
                boulder.BoulderHolds.Select(bh => new ReconcilableHold(bh.HoldId, bh.Type, bh.Usage)),
                links);

            var used = physical.SelectMany(p => p.HoldIds).ToHashSet();
            if (boulder.KickboardFootholdsOn && kickboardHoldIds is not null)
            {
                used.UnionWith(kickboardHoldIds);
            }

            foreach (var holdId in used)
            {
                counts[holdId] = counts.GetValueOrDefault(holdId) + 1;
            }
        }

        return counts;
    }
}
