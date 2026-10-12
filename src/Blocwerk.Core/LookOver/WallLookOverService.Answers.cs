// <copyright file="WallLookOverService.Answers.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Data;
using Blocwerk.Core.Detection.Outlines;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.LookOver;

/// <summary>
/// Answering a finding after the update is live. Nothing here has hold semantics of its own: "different hold" is the
/// editor's "mark as changed" (<see cref="IWallService.MarkHoldModifiedAsync"/>), "gone" is the editor's delete
/// (<see cref="IWallService.DeleteHoldAsync"/>: the boulders that use the hold become historic), and "use the new hold"
/// is the duplicate review's merge (<see cref="IHoldDuplicateService.MergeAsync"/>: the boulders move over, undoable).
/// Each of those checks the user again by its own rule; the merge is for wall admins.
/// </summary>
public sealed partial class WallLookOverService
{
    /// <inheritdoc/>
    public async Task AnswerAsync(Guid wallId, Guid findingId, LookOverAnswer answer, CancellationToken ct = default)
    {
        var (db, userId, wallAdmin) = await OpenAsync(wallId, ct);
        await using (db)
        {
            var finding = await db.WallLookOverFindings.FirstOrDefaultAsync(f => f.Id == findingId && f.WallId == wallId, ct)
                ?? throw new UserFacingException("That finding no longer exists. Reload the page.");
            if (finding.Status != LookOverFindingStatus.Open)
            {
                throw new UserFacingException("That finding was already answered. Reload the page.");
            }

            var view = (await ToViewsAsync(db, [finding], wallAdmin, ct))[0];
            if (!view.Answers.Contains(answer))
            {
                throw new UserFacingException("That answer does not fit this finding any more. Reload the page.");
            }

            await ApplyAsync(db, finding, answer, userId, ct);
            await db.SaveChangesAsync(ct);
            logger.LogInformation(
                "Look-over finding {FindingId} ({Kind}) on wall {WallId} answered {Answer} by {UserId}", findingId, finding.Kind, wallId, answer, userId);
        }
    }

    /// <inheritdoc/>
    public async Task<int> SettleUnsureMatchesAsync(Guid wallId, CancellationToken ct = default)
    {
        var (db, userId, _) = await OpenAsync(wallId, ct);
        await using (db)
        {
            var open = await db.WallLookOverFindings
                .Where(f => f.WallId == wallId && f.Status == LookOverFindingStatus.Open && f.Kind == LookOverFindingKind.UnsureMatch)
                .ToListAsync(ct);
            foreach (var finding in open)
            {
                await ClearFlagAsync(db, finding, ct);
                Settle(finding, LookOverAnswer.Settle, userId);
            }

            await db.SaveChangesAsync(ct);
            logger.LogInformation("{Count} unsure matches on wall {WallId} settled as the same hold by {UserId}", open.Count, wallId, userId);
            return open.Count;
        }
    }

    private async Task ApplyAsync(BlocwerkDbContext db, WallLookOverFinding finding, LookOverAnswer answer, Guid userId, CancellationToken ct)
    {
        switch (answer)
        {
            case LookOverAnswer.DifferentHold:
                // Not settled: what to do with the hold is the replaced case's question, asked next.
                await walls.MarkHoldModifiedAsync(finding.HoldId!.Value);
                (finding.Kind, finding.Converted, finding.FlaggedHold) = (LookOverFindingKind.ReplacedByOther, true, false);
                return;
            case LookOverAnswer.RemoveHold:
                await walls.DeleteHoldAsync(finding.HoldId!.Value);
                break;
            case LookOverAnswer.UseNewHold:
                var merge = await duplicates.MergeAsync(finding.WallId, finding.OtherHoldId!.Value, finding.HoldId!.Value, HoldMergeMode.KeepLeft, ct);
                finding.MergeBatchId = merge.BatchId;
                break;
            default:
                await ClearFlagAsync(db, finding, ct);
                break;
        }

        Settle(finding, answer, userId);
    }

    /// <summary>Takes the review mark off the hold when the update set it for this finding alone.</summary>
    private static async Task ClearFlagAsync(BlocwerkDbContext db, WallLookOverFinding finding, CancellationToken ct)
    {
        if (!finding.FlaggedHold || finding.HoldId is not { } holdId)
        {
            return;
        }

        if (await db.Holds.FirstOrDefaultAsync(h => h.Id == holdId, ct) is { } hold)
        {
            hold.NeedsReview = false;
        }
    }

    private static void Settle(WallLookOverFinding finding, LookOverAnswer answer, Guid userId)
    {
        finding.Status = LookOverFindingStatus.Settled;
        finding.Answer = answer;
        finding.AnsweredByUserId = userId;
        finding.AnsweredAt = DateTimeOffset.UtcNow;
    }
}
