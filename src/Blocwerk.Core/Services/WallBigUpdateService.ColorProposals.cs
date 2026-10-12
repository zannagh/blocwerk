// <copyright file="WallBigUpdateService.ColorProposals.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Services;

/// <summary>
/// Colour proposals for the holds a panel update added. The detector finds holds but not their colour, so a new hold
/// used to go live without one; here each gets the colour its new panel photo shows, when that can be told
/// (<see cref="IHoldColorProposer"/>). Carried holds keep what they had and are not looked at.
/// </summary>
public partial class WallBigUpdateService
{
    /// <summary>
    /// Proposes colours for the holds that are new in <paramref name="newGen"/> (no old hold behind them) and have no
    /// colour. Runs after the promote is committed, inside the still-open wall-update batch, and before the linked-hold
    /// sync. Best effort: a failure only logs and leaves the holds as the promote wrote them; the wall page's "Suggest
    /// colours" does the same later.
    /// </summary>
    private async Task ProposeNewHoldColorsAfterPromoteAsync(BlocwerkDbContext db, Guid wallId, int newGen)
    {
        try
        {
            var newHoldIds = await db.Holds
                .Where(h => h.WallId == wallId && h.Generation == newGen && !h.IsVirtual
                    && (h.Color == null || h.Color == string.Empty)
                    && !db.HoldGenerationLinks.Any(l => l.NewHoldId == h.Id && l.OldHoldId != null))
                .Select(h => h.Id)
                .ToListAsync();
            if (newHoldIds.Count == 0)
            {
                return;
            }

            var report = await colorProposer.ProposeAsync(db, wallId, newHoldIds);
            if (report.Any)
            {
                await db.SaveChangesAsync();
            }

            logger.LogInformation("Colour proposals after the promote of wall {WallId}: {Summary}", wallId, report.Summary());
        }
        catch (Exception ex)
        {
            // Whatever the failed step left half-written must not ride along with the linked-hold sync's save.
            db.ChangeTracker.Clear();
            logger.LogWarning(ex, "Colour proposals after the promote of wall {WallId} failed; run Suggest colours on the wall page.", wallId);
        }
    }
}
