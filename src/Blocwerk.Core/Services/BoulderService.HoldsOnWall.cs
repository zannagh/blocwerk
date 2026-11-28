// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Services;

/// <summary>
/// A boulder's holds are holds of its own wall. The designer only ever offers those, but the offline queue and the
/// companion app send hold ids, so create and revise refuse an unknown hold or one of another wall here.
/// </summary>
public partial class BoulderService
{
    /// <summary>The refusal when a boulder is given a hold that is not on its wall.</summary>
    public const string HoldOtherWallMessage = "One of the holds is not on this wall.";

    /// <summary>Throws <see cref="UserFacingException"/> unless every hold of <paramref name="holds"/> exists on <paramref name="wallId"/>.</summary>
    /// <param name="db">The context.</param>
    /// <param name="wallId">The boulder's wall.</param>
    /// <param name="holds">The holds the boulder is being saved with.</param>
    private static async Task EnsureHoldsOnWallAsync(BlocwerkDbContext db, Guid wallId, IEnumerable<BoulderHoldInput> holds)
    {
        var ids = holds.Select(h => h.HoldId).Distinct().ToList();
        if (ids.Count == 0)
        {
            return;
        }

        var onWall = await db.Holds.IgnoreQueryFilters().CountAsync(h => ids.Contains(h.Id) && h.WallId == wallId);
        if (onWall != ids.Count)
        {
            throw new UserFacingException(HoldOtherWallMessage);
        }
    }
}
