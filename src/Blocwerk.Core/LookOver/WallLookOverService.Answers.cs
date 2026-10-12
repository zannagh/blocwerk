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
/// Each of those checks the user again by its own rule, which is also what decides the answers a user is offered.
/// An answer only ever reaches a hold that is live, and only with the boulders the user was shown. Settling is in the
/// Settle part.
/// </summary>
public sealed partial class WallLookOverService
{
    /// <summary>The refusal when other boulders use the hold than the user was shown.</summary>
    public const string BouldersChanged = "The boulders that use this hold have changed. Reload the page and check again.";

    /// <inheritdoc/>
    public async Task AnswerAsync(
        Guid wallId, Guid findingId, LookOverAnswer answer, IReadOnlyCollection<Guid>? expectedBoulderIds = null, CancellationToken ct = default)
    {
        var (db, access) = await OpenAsync(wallId, ct);
        await using (db)
        {
            var finding = await db.WallLookOverFindings.FirstOrDefaultAsync(f => f.Id == findingId && f.WallId == wallId, ct)
                ?? throw new UserFacingException("That finding no longer exists. Reload the page.");
            if (finding.Status != LookOverFindingStatus.Open)
            {
                throw new UserFacingException("That finding was already answered. Reload the page.");
            }

            // The view is the one place that says what the hold is NOW and what this user may do with it.
            var view = (await ToViewsAsync(db, wallId, [finding], access, ct))[0];
            if (!view.Answers.Contains(answer))
            {
                throw new UserFacingException("That answer does not fit this finding any more. Reload the page.");
            }

            if (LookOverRules.ChangesTheWall(answer) && expectedBoulderIds?.ToHashSet().SetEquals(view.Boulders.Select(b => b.Id)) != true)
            {
                throw new UserFacingException(BouldersChanged);
            }

            using var batch = BeginBatch(wallId);
            await ApplyAsync(db, finding, view, answer, access.UserId, ct);
            await db.SaveChangesAsync(ct);
            logger.LogInformation(
                "Look-over finding {FindingId} ({Kind}) on wall {WallId} answered {Answer} by {UserId}", findingId, finding.Kind, wallId, answer, access.UserId);
        }
    }

    /// <summary>Acts on <see cref="LookOverFindingView.HoldId"/>, which is only ever a live hold; never on the stored id.</summary>
    private async Task ApplyAsync(
        BlocwerkDbContext db, WallLookOverFinding finding, LookOverFindingView view, LookOverAnswer answer, Guid userId, CancellationToken ct)
    {
        switch (answer)
        {
            case LookOverAnswer.DifferentHold:
                // Not settled: what to do with the hold is the replaced case's question, asked next.
                await walls.MarkHoldModifiedAsync(view.HoldId!.Value);
                await RecordReplacedAsync(db, finding, view.HoldId.Value, ct);
                (finding.Kind, finding.Converted, finding.FlaggedHold) = (LookOverFindingKind.ReplacedByOther, true, false);
                return;
            case LookOverAnswer.RemoveHold:
                await walls.DeleteHoldAsync(view.HoldId!.Value);
                break;
            case LookOverAnswer.UseNewHold:
                var merge = await duplicates.MergeAsync(finding.WallId, view.OtherHoldId!.Value, view.HoldId!.Value, HoldMergeMode.KeepLeft, ct);
                finding.MergeBatchId = merge.BatchId;
                break;
            default:
                await SettleAsync(db, finding, view, userId, ct);
                return;
        }

        MarkSettled(finding, answer, userId, view.Boulders.Count);
    }

    /// <summary>
    /// Tells the boulders on the hold why they are marked, the way the update itself does for a replaced hold
    /// (<see cref="BoulderHoldMove"/> with <see cref="HoldMoveOutcome.Replaced"/>): the boulder page then reads
    /// "another hold is here now" instead of a mark without a reason. A boulder the update already told something
    /// about this hold keeps that.
    /// </summary>
    private static async Task RecordReplacedAsync(BlocwerkDbContext db, WallLookOverFinding finding, Guid holdId, CancellationToken ct)
    {
        var uses = await db.BoulderHolds
            .Where(bh => bh.HoldId == holdId && !bh.Boulder.IsArchived && !bh.Boulder.IsHistoric)
            .Select(bh => new { bh.BoulderId, bh.Type, bh.Usage, bh.Boulder.Generation })
            .ToListAsync(ct);
        var told = (await db.BoulderHoldMoves.Where(m => m.NewHoldId == holdId).Select(m => new { m.BoulderId, m.ToGeneration }).ToListAsync(ct))
            .Select(m => (m.BoulderId, m.ToGeneration))
            .ToHashSet();
        var old = await db.Holds.Where(h => h.Id == finding.OldHoldId).Select(h => new { h.Id, h.Generation }).FirstOrDefaultAsync(ct);
        foreach (var use in uses.Where(u => !told.Contains((u.BoulderId, u.Generation))))
        {
            db.BoulderHoldMoves.Add(new BoulderHoldMove
            {
                WallId = finding.WallId,
                BoulderId = use.BoulderId,
                OldHoldId = old?.Id,
                NewHoldId = holdId,
                Type = use.Type,
                Usage = use.Usage,
                Source = HoldMoveSource.TwoD,
                Outcome = HoldMoveOutcome.Replaced,
                FromGeneration = old?.Generation ?? finding.Generation - 1,
                ToGeneration = use.Generation,
            });
        }
    }

    private static void MarkSettled(WallLookOverFinding finding, LookOverAnswer answer, Guid userId, int boulders)
    {
        finding.Status = LookOverFindingStatus.Settled;
        finding.Answer = answer;
        finding.AnsweredByUserId = userId;
        finding.AnsweredAt = DateTimeOffset.UtcNow;
        finding.AnswerBoulders = boulders;
    }
}
