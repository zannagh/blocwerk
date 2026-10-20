// <copyright file="WallUpdateSessionService.LiveHolds.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Services;

/// <summary>
/// Which of the holds a decision write names still exist: a staged hold another admin deleted meanwhile would break
/// the foreign key, so every bulk write drops those ids rather than failing.
/// </summary>
public partial class WallUpdateSessionService
{
    private static List<Guid> CollectHoldIds(
        IReadOnlyList<CarryoverDecision> carryover,
        IReadOnlyList<Guid> accepted,
        IReadOnlyList<Guid> removed)
    {
        var ids = new List<Guid>();
        foreach (var decision in carryover)
        {
            ids.Add(decision.OldHoldId);
            if (decision.NewHoldId is { } newId)
            {
                ids.Add(newId);
            }
        }

        ids.AddRange(accepted);
        ids.AddRange(removed);
        return ids;
    }

    /// <summary>The subset of <paramref name="candidates"/> that still exists on the wall.</summary>
    private static async Task<HashSet<Guid>> LoadLiveHoldIdsAsync(
        BlocwerkDbContext db, Guid wallId, List<Guid> candidates)
    {
        if (candidates.Count == 0)
        {
            return [];
        }

        var distinct = candidates.Distinct().ToList();
        var found = await db.Holds
            .Where(h => h.WallId == wallId && distinct.Contains(h.Id))
            .Select(h => h.Id)
            .ToListAsync();
        return found.ToHashSet();
    }
}
