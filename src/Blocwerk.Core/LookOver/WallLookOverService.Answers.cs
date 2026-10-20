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
/// Answering a finding after the update is live. "Different hold" is the editor's "mark as changed"
/// (<see cref="IWallService.MarkHoldModifiedAsync"/>) and then takes the pair apart (the Split part), "gone" is the editor's delete
/// (<see cref="IWallService.DeleteHoldAsync"/>: the boulders that use the hold become historic), and "use the new hold"
/// moves the boulders onto the new hold as the duplicate review's merge does, per boulder and undoable there (the NewHold part).
/// Each of those checks the user again by its own rule, which is also what decides the answers a user is offered.
/// An answer only ever reaches a hold that is live, and only with the boulders the user was shown. Settling is in the
/// Settle part; what it does to boulders in the Boulders part.
/// </summary>
public sealed partial class WallLookOverService
{
    /// <summary>The refusal when other boulders use the hold than the user was shown.</summary>
    public const string BouldersChanged = "The boulders that use this hold have changed. Reload the page and check again.";

    /// <inheritdoc/>
    public async Task AnswerAsync(
        Guid wallId,
        Guid findingId,
        LookOverAnswer answer,
        IReadOnlyCollection<Guid>? expectedBoulderIds = null,
        IReadOnlyCollection<Guid>? newHoldBoulderIds = null,
        CancellationToken ct = default) =>
        await AnswerCoreAsync(wallId, findingId, answer, expectedBoulderIds, newHoldBoulderIds, null, ct);

    /// <summary>
    /// One answer, for <see cref="AnswerAsync"/> and <see cref="DifferentHoldAsync"/>: the latter names the hold that replaced
    /// the old one (<paramref name="replacementHoldId"/>), checked against the finding's photo before anything changes.
    /// </summary>
    private async Task AnswerCoreAsync(
        Guid wallId,
        Guid findingId,
        LookOverAnswer answer,
        IReadOnlyCollection<Guid>? expectedBoulderIds,
        IReadOnlyCollection<Guid>? newHoldBoulderIds,
        Guid? replacementHoldId,
        CancellationToken ct)
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

            if (replacementHoldId is { } replacement)
            {
                await EnsureReplacementAsync(db, wallId, view, replacement, ct);
            }

            // Asked per boulder: every boulder "no" is the old hold gone, every boulder "yes" the merge.
            (answer, var onlyThese) = NewHoldChoice(answer, newHoldBoulderIds, view);

            using var batch = BeginBatch(wallId);
            if (answer == LookOverAnswer.Settle)
            {
                await SettleOneAsync(db, finding, view, access.UserId, ct);
            }
            else
            {
                await ChangeAsync(db, finding, view, answer, onlyThese, replacementHoldId, access.UserId, ct);
            }

            logger.LogInformation(
                "Look-over finding {FindingId} ({Kind}) on wall {WallId} answered {Answer} by {UserId}", findingId, finding.Kind, wallId, answer, access.UserId);
        }
    }

    /// <summary>
    /// An answer that changes the wall. The hold operations run on their own contexts and commit by themselves, so the
    /// finding is claimed first with a conditional update (only one of two people gets it) and released again when the
    /// operation refuses. Acts on <see cref="LookOverFindingView.HoldId"/>, which is only ever a live hold.
    /// </summary>
    private async Task ChangeAsync(
        BlocwerkDbContext db,
        WallLookOverFinding finding,
        LookOverFindingView view,
        LookOverAnswer answer,
        IReadOnlySet<Guid>? onlyThese,
        Guid? replacementHoldId,
        Guid userId,
        CancellationToken ct)
    {
        var mine = db.WallLookOverFindings.Where(f => f.Id == finding.Id);
        var different = answer == LookOverAnswer.DifferentHold;
        var claimed = different
            ? await mine.Where(f => f.Status == LookOverFindingStatus.Open && !f.Converted).ExecuteUpdateAsync(s => s.SetProperty(f => f.Converted, true), ct)
            : await mine.Where(f => f.Status == LookOverFindingStatus.Open).ExecuteUpdateAsync(s => s.SetProperty(f => f.Status, LookOverFindingStatus.Settled), ct);
        if (claimed != 1)
        {
            throw new UserFacingException(AnsweredMeanwhile);
        }

        Hold? added;
        try
        {
            added = await ApplyAsync(db, finding, view, answer, onlyThese, ct);
            if (different && replacementHoldId is { } replacement)
            {
                await PointAtReplacementAsync(db, finding, replacement, ct);
            }
        }
        catch
        {
            await mine.ExecuteUpdateAsync(
                s => s.SetProperty(f => f.Status, LookOverFindingStatus.Open).SetProperty(f => f.Converted, finding.Converted), CancellationToken.None);
            throw;
        }

        if (different)
        {
            // Not settled: what to do with the hold is the replaced case's question, asked next.
            (finding.Kind, finding.Converted, finding.FlaggedHold) = (LookOverFindingKind.ReplacedByOther, true, false);
        }
        else
        {
            MarkSettled(finding, answer, userId, view.Boulders.Count);
        }

        await db.SaveChangesAsync(ct);
        if (added is not null)
        {
            await SuggestColorAsync(db, finding.WallId, [added.Id], ct);
        }
    }

    /// <summary>Runs the hold operation of an answer. Returns the new hold when the answer took a pair apart.</summary>
    private async Task<Hold?> ApplyAsync(
        BlocwerkDbContext db, WallLookOverFinding finding, LookOverFindingView view, LookOverAnswer answer, IReadOnlySet<Guid>? onlyThese, CancellationToken ct)
    {
        switch (answer)
        {
            case LookOverAnswer.DifferentHold:
                await walls.MarkHoldModifiedAsync(view.HoldId!.Value);
                await RecordReplacedAsync(db, finding, view.HoldId.Value, ct);
                return await SplitAsync(db, finding, view.HoldId.Value, ct);
            case LookOverAnswer.RemoveHold:
                // Called a different hold before the pair was taken apart: the hold still is the new one on the wall.
                // Split first, so only the old hold goes and the detection stays as a new hold.
                Hold? detection = null;
                if (IsUnsplit(finding))
                {
                    detection = await SplitAsync(db, finding, view.HoldId!.Value, ct);
                    await db.SaveChangesAsync(ct);
                }

                await walls.DeleteHoldAsync(view.HoldId!.Value);
                return detection;
            case LookOverAnswer.AddHold:
                return await AddDetectionAsync(db, finding, ct);
            case LookOverAnswer.Unlink:
                await UnlinkAsync(db, finding, ct);
                return null;
            default:
                await UseNewHoldAsync(db, finding, view, onlyThese, ct);
                return null;
        }
    }

    /// <summary>"No, two holds": the link between the two holds goes; both holds stay as they are. Saved with the answer.</summary>
    private static async Task UnlinkAsync(BlocwerkDbContext db, WallLookOverFinding finding, CancellationToken ct)
    {
        var (a, b) = (finding.HoldId, finding.OtherHoldId);
        var links = await db.HoldLinks
            .Where(l => l.WallId == finding.WallId && ((l.HoldAId == a && l.HoldBId == b) || (l.HoldAId == b && l.HoldBId == a)))
            .ToListAsync(ct);
        db.HoldLinks.RemoveRange(links);
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
