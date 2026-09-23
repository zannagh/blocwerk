// <copyright file="WallBigUpdateService.Shapes.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Services;

/// <summary>
/// The promote's share of the optional shape step: refuse while a recognition is still running, and apply
/// the reviewed shapes of a COMPLETED run onto the promoted holds. Skipped, failed or never-run steps apply
/// nothing, so every hold keeps the shape it had — which is the whole of "the step is optional".
/// </summary>
public partial class WallBigUpdateService
{
    /// <summary>Refuses to promote under a live (or interrupted) recognition run.</summary>
    private static async Task EnsureShapeStepSettledAsync(BlocwerkDbContext db, Guid wallId)
    {
        var session = await WallUpdateSessions.FindOpenAsync(db, wallId);
        if (session?.ShapeStatus == ShapeRecognitionStatus.Running)
        {
            throw new InvalidOperationException(
                "Hold shapes are still being recognised on this update. Wait for it to finish, or skip the step.");
        }
    }

    /// <summary>
    /// Writes every reviewed proposal onto its hold. Runs after the carry (so an accepted outline replaces the
    /// warped old one) and before the session closes. Proposal ids are staged hold ids, and a promoted staged
    /// hold keeps its id, so each resolves straight to its live row; a hold the promote deleted is skipped.
    /// No SaveChanges — the promote commits.
    /// </summary>
    private static async Task ApplyShapeDecisionsAsync(BlocwerkDbContext db, Guid wallId, int newGen)
    {
        var session = await WallUpdateSessions.FindOpenAsync(db, wallId);
        if (session?.ShapeStatus != ShapeRecognitionStatus.Completed)
        {
            return;
        }

        var decided = new[] { ShapeReviewDecision.Accepted, ShapeReviewDecision.Adjusted, ShapeReviewDecision.Circle };
        var rows = await db.WallUpdateShapeProposals.AsNoTracking()
            .Where(p => p.SessionId == session.Id && decided.Contains(p.Decision))
            .ToListAsync();
        foreach (var row in rows)
        {
            var hold = db.Holds.Local.FirstOrDefault(h => h.Id == row.HoldId)
                ?? await db.Holds.FirstOrDefaultAsync(h => h.Id == row.HoldId);
            if (hold is null || db.Entry(hold).State == EntityState.Deleted || hold.Generation != newGen)
            {
                continue;
            }

            ShapeDecisionApplier.Apply(hold, row);
        }
    }
}
