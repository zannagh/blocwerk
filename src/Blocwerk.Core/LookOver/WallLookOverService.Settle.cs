// <copyright file="WallLookOverService.Settle.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.HoldMoves;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.LookOver;

/// <summary>
/// "It is fine as it is", one finding or a whole kind at once, and taking that back. Settling changes no hold. Where
/// it says the update's reason was wrong ("Same hold", "Still there") it also withdraws that reason from the boulders:
/// the "not found again" rows of the hold are settled the way a 3D re-measurement settles a move
/// (<see cref="BoulderHoldMove.RemeasuredOutcome"/> = stayed), and <see cref="HoldMoveRemeasurer"/>'s own rule then
/// clears a boulder that has no other reason left. Only a boulder this update marked is ever cleared. What an answer
/// cleared is kept with the finding, so undoing the answer puts every mark back.
/// </summary>
public sealed partial class WallLookOverService
{
    /// <inheritdoc/>
    public async Task<int> SettleAllAsync(Guid wallId, LookOverFindingKind kind, bool onlyWithoutBoulders, CancellationToken ct = default)
    {
        if (!LookOverRules.CanSettleAll(kind))
        {
            throw new UserFacingException("These findings are answered one by one.");
        }

        var (db, access) = await OpenAsync(wallId, ct);
        await using (db)
        {
            var open = await db.WallLookOverFindings
                .Where(f => f.WallId == wallId && f.Status == LookOverFindingStatus.Open && f.Kind == kind)
                .ToListAsync(ct);
            var views = (await ToViewsAsync(db, wallId, open, access, ct)).ToDictionary(v => v.Id);
            using var batch = BeginBatch(wallId);
            var settled = 0;
            foreach (var finding in open.Where(f => !onlyWithoutBoulders || views[f.Id].Boulders.Count == 0))
            {
                await SettleAsync(db, finding, views[finding.Id], access.UserId, ct);
                settled++;
            }

            await db.SaveChangesAsync(ct);
            logger.LogInformation(
                "{Count} {Kind} findings on wall {WallId} settled at once by {UserId} (only without boulders: {Unused})", settled, kind, wallId, access.UserId, onlyWithoutBoulders);
            return settled;
        }
    }

    /// <inheritdoc/>
    public async Task UndoAsync(Guid wallId, Guid findingId, CancellationToken ct = default)
    {
        var (db, access) = await OpenAsync(wallId, ct);
        await using (db)
        {
            var finding = await db.WallLookOverFindings.FirstOrDefaultAsync(f => f.Id == findingId && f.WallId == wallId, ct)
                ?? throw new UserFacingException("That finding no longer exists. Reload the page.");
            if (finding is not { Status: LookOverFindingStatus.Settled, Answer: LookOverAnswer.Settle })
            {
                throw new UserFacingException("Only an answer that changed nothing on the wall can be undone here. Reload the page.");
            }

            using var batch = BeginBatch(wallId);
            await RestoreMarksAsync(db, finding, ct);
            (finding.Status, finding.Answer, finding.AnsweredByUserId, finding.AnsweredAt) = (LookOverFindingStatus.Open, null, null, null);
            (finding.ClearedHoldMark, finding.ClearedBoulderIds, finding.AnswerBoulders) = (false, null, null);
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Look-over finding {FindingId} on wall {WallId} reopened by {UserId}", findingId, wallId, access.UserId);
        }
    }

    /// <summary>Settles one finding and saves: the hold's own mark first, since a marked hold keeps its boulders marked.</summary>
    private async Task SettleAsync(BlocwerkDbContext db, WallLookOverFinding finding, LookOverFindingView view, Guid userId, CancellationToken ct)
    {
        if (finding.FlaggedHold && view.HoldId is { } flagged && await db.Holds.FirstOrDefaultAsync(h => h.Id == flagged, ct) is { NeedsReview: true } hold)
        {
            hold.NeedsReview = false;
            finding.ClearedHoldMark = true;
        }

        MarkSettled(finding, LookOverAnswer.Settle, userId, view.Boulders.Count);
        await db.SaveChangesAsync(ct);
        if (view.HoldId is { } holdId && LookOverRules.SettlingWithdrawsTheReason(finding.Kind))
        {
            finding.ClearedBoulderIds = await WithdrawReasonAsync(db, finding, holdId, ct);
        }
    }

    /// <summary>
    /// Settles the hold's "not found again" rows of this update and clears the boulders that have nothing else left:
    /// only those the update marked (<see cref="WallLookOverFinding.MarkedBoulderIds"/>), by the re-measurement's rule
    /// (no other unsettled reason, no marked or changed hold), and with no other open finding on one of their holds.
    /// </summary>
    private async Task<List<Guid>> WithdrawReasonAsync(BlocwerkDbContext db, WallLookOverFinding finding, Guid holdId, CancellationToken ct)
    {
        var rows = await db.BoulderHoldMoves
            .Where(m => m.NewHoldId == holdId && m.ToGeneration == finding.Generation && m.Outcome == HoldMoveOutcome.NotFound && m.RemeasuredOutcome == null)
            .ToListAsync(ct);
        rows.ForEach(r => r.RemeasuredOutcome = HoldMoveOutcome.Stayed);
        await db.SaveChangesAsync(ct);
        return await HoldMoveRemeasurer.ClearSettledBouldersAsync(
            db,
            finding.MarkedBoulderIds ?? [],
            finding.Generation,
            logger,
            boulderId => db.WallLookOverFindings.AnyAsync(
                f => f.WallId == finding.WallId && f.Id != finding.Id && f.Status == LookOverFindingStatus.Open && f.HoldId != null
                    && db.BoulderHolds.Any(bh => bh.BoulderId == boulderId && bh.HoldId == f.HoldId),
                ct));
    }

    /// <summary>Puts back what settling took off: the hold's mark, the boulders' marks and their "not found again" reason.</summary>
    private static async Task RestoreMarksAsync(BlocwerkDbContext db, WallLookOverFinding finding, CancellationToken ct)
    {
        if (finding is { ClearedHoldMark: true, HoldId: { } marked } && await db.Holds.FirstOrDefaultAsync(h => h.Id == marked, ct) is { } hold)
        {
            hold.NeedsReview = true;
        }

        if (finding.HoldId is { } holdId && LookOverRules.SettlingWithdrawsTheReason(finding.Kind))
        {
            var rows = await db.BoulderHoldMoves
                .Where(m => m.NewHoldId == holdId && m.ToGeneration == finding.Generation && m.Outcome == HoldMoveOutcome.NotFound
                    && m.RemeasuredOutcome == HoldMoveOutcome.Stayed && m.RemeasuredDistanceMm == null)
                .ToListAsync(ct);
            rows.ForEach(r => r.RemeasuredOutcome = null);
        }

        if (finding.ClearedBoulderIds is { Count: > 0 } cleared)
        {
            var boulders = await db.Boulders.Where(b => cleared.Contains(b.Id) && !b.IsArchived && !b.IsHistoric).ToListAsync(ct);
            boulders.ForEach(b => b.NeedsReview = true);
        }
    }
}
