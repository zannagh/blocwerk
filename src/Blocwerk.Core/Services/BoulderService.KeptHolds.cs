// <copyright file="BoulderService.KeptHolds.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Services;

/// <summary>
/// A hold the last panel update did not find again and nobody has confirmed since
/// (<see cref="Hold.KeptUnconfirmedGeneration"/>) may not be chosen for a boulder: it may not be on the wall any more.
/// A boulder that already uses such a hold keeps it; only ADDING one is refused, here as in the designer, so the
/// offline queue and scripts meet the same rule.
/// </summary>
public partial class BoulderService
{
    /// <summary>The refusal when a boulder is given a hold that was not found again and is not confirmed.</summary>
    public const string UnconfirmedHoldMessage =
        "One of the holds was not found again in the last update. Confirm it in the look-over first, or choose another hold.";

    /// <summary>Throws when <paramref name="holds"/> adds a kept-unconfirmed hold the boulder did not use before.</summary>
    /// <param name="db">The context.</param>
    /// <param name="holds">The holds the boulder is being saved with.</param>
    /// <param name="alreadyUsed">The holds it uses now (empty for a new boulder).</param>
    private static async Task EnsureNoUnconfirmedHoldAddedAsync(BlocwerkDbContext db, IEnumerable<BoulderHoldInput> holds, IReadOnlyCollection<Guid> alreadyUsed)
    {
        var added = holds.Select(h => h.HoldId).Where(id => !alreadyUsed.Contains(id)).Distinct().ToList();
        if (added.Count == 0)
        {
            return;
        }

        // A boulder revised after an update is saved with the successors of the holds it used: those it had already.
        var carried = alreadyUsed.Count == 0
            ? []
            : await db.HoldGenerationLinks
                .Where(l => l.OldHoldId != null && l.NewHoldId != null && alreadyUsed.Contains(l.OldHoldId.Value) && added.Contains(l.NewHoldId.Value))
                .Select(l => l.NewHoldId!.Value)
                .ToListAsync();
        if (await db.Holds.AnyAsync(h => added.Contains(h.Id) && !carried.Contains(h.Id) && h.KeptUnconfirmedGeneration != null))
        {
            throw new UserFacingException(UnconfirmedHoldMessage);
        }
    }
}
