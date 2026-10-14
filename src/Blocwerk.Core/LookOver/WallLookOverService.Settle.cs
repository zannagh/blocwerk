// <copyright file="WallLookOverService.Settle.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.LookOver;

/// <summary>
/// "It is fine as it is", one finding or many at once, and taking that back. Settling changes no hold; where it says
/// the update's reason was wrong it also withdraws that reason from the boulders (the Boulders part). Each settle and
/// each undo is one transaction: the finding's status, the marks it took off and the record of what it took off are
/// saved together or not at all, and the finding is claimed first, so of two people answering at once one is told.
/// </summary>
public sealed partial class WallLookOverService
{
    /// <summary>The refusal for whoever comes second when two people answer one finding at the same moment.</summary>
    public const string AnsweredMeanwhile = "Someone else just answered this finding. The list below is the current one.";

    /// <inheritdoc/>
    public async Task<LookOverBulkResult> SettleAllAsync(
        Guid wallId, LookOverFindingKind kind, IReadOnlyCollection<Guid> findingIds, bool onlyWithoutBoulders, CancellationToken ct = default)
    {
        if (!LookOverRules.CanSettleAll(kind))
        {
            throw new UserFacingException("These findings are answered one by one.");
        }

        var (db, access) = await OpenAsync(wallId, ct);
        await using (db)
        {
            // Only what the user was shown and confirmed, and of that only what is still open (and still unused).
            var asked = findingIds.Distinct().ToList();
            var open = await db.WallLookOverFindings
                .Where(f => f.WallId == wallId && f.Status == LookOverFindingStatus.Open && f.Kind == kind && asked.Contains(f.Id))
                .ToListAsync(ct);
            var views = (await ToViewsAsync(db, wallId, open, access, ct)).ToDictionary(v => v.Id);
            using var batch = BeginBatch(wallId);
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var settled = 0;
            foreach (var finding in open.Where(f => !onlyWithoutBoulders || views[f.Id].Boulders.Count == 0))
            {
                // Claimed meanwhile by someone else: theirs to settle.
                var claimed = await ClaimAsync(db, finding.Id, LookOverFindingStatus.Open, ct);
                if (!claimed)
                {
                    continue;
                }

                await SettleAsync(db, finding, views[finding.Id], access.UserId, ct);
                settled++;
            }

            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            logger.LogInformation(
                "{Count} of {Asked} {Kind} findings on wall {WallId} settled at once by {UserId} (only without boulders: {Unused})",
                settled, asked.Count, kind, wallId, access.UserId, onlyWithoutBoulders);
            return new LookOverBulkResult(settled, asked.Count);
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

            // The view says whether undoing still makes sense: answered in this round, about a hold that is still live.
            var view = (await ToViewsAsync(db, wallId, [finding], access, ct))[0];
            if (!view.CanUndo)
            {
                throw new UserFacingException("This answer can no longer be undone here. Reload the page.");
            }

            using var batch = BeginBatch(wallId);

            // An added detection is taken off the wall again first, by the editor's delete on its own context: when
            // that refuses, nothing here has changed.
            if (finding is { Answer: LookOverAnswer.AddHold, HoldId: { } addedId })
            {
                await walls.DeleteHoldAsync(addedId);
                finding.HoldId = null;
            }

            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            if (!await ClaimAsync(db, finding.Id, LookOverFindingStatus.Settled, ct))
            {
                throw new UserFacingException(AnsweredMeanwhile);
            }

            await RestoreMarksAsync(db, finding, view.HoldId, ct);
            (finding.Status, finding.Answer, finding.AnsweredByUserId, finding.AnsweredAt) = (LookOverFindingStatus.Open, null, null, null);
            (finding.ClearedHoldMark, finding.ClearedKept, finding.ClearedBoulderIds, finding.AnswerBoulders) = (false, false, null, null);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            logger.LogInformation("Look-over finding {FindingId} on wall {WallId} reopened by {UserId}", findingId, wallId, access.UserId);
        }
    }

    /// <summary>Settles one open finding in its own transaction; the second of two people is refused.</summary>
    private async Task SettleOneAsync(BlocwerkDbContext db, WallLookOverFinding finding, LookOverFindingView view, Guid userId, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        if (!await ClaimAsync(db, finding.Id, LookOverFindingStatus.Open, ct))
        {
            throw new UserFacingException(AnsweredMeanwhile);
        }

        await SettleAsync(db, finding, view, userId, ct);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }

    /// <summary>
    /// Takes the finding for this transaction, if it still has <paramref name="status"/>: an update that changes
    /// nothing but locks the row, so a second transaction waits here and then finds the status changed.
    /// </summary>
    private static async Task<bool> ClaimAsync(BlocwerkDbContext db, Guid findingId, LookOverFindingStatus status, CancellationToken ct) =>
        await db.WallLookOverFindings
            .Where(f => f.Id == findingId && f.Status == status)
            .ExecuteUpdateAsync(s => s.SetProperty(f => f.Status, status), ct) == 1;

    /// <summary>Settles one claimed finding (the caller's transaction): the hold's own mark first, since a marked hold keeps its boulders marked.</summary>
    private async Task SettleAsync(BlocwerkDbContext db, WallLookOverFinding finding, LookOverFindingView view, Guid userId, CancellationToken ct)
    {
        var hold = view.HoldId is { } live ? await db.Holds.FirstOrDefaultAsync(h => h.Id == live, ct) : null;
        if (finding.FlaggedHold && hold is { NeedsReview: true })
        {
            hold.NeedsReview = false;
            finding.ClearedHoldMark = true;
        }

        // "Still there", "Keep…", "Both are there": a person says the hold the update could not find is on the wall.
        if (hold is { KeptUnconfirmedGeneration: not null } && LookOverRules.CanHaveOther(finding.Kind))
        {
            hold.KeptUnconfirmedGeneration = null;
            finding.ClearedKept = true;
        }

        MarkSettled(finding, LookOverAnswer.Settle, userId, view.Boulders.Count);
        await db.SaveChangesAsync(ct);
        if (view.HoldId is { } holdId && LookOverRules.SettlingWithdrawsTheReason(finding.Kind))
        {
            finding.ClearedBoulderIds = await WithdrawReasonAsync(db, finding, holdId, ct);
        }
    }
}
