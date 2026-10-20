// <copyright file="WallUpdateSessionService.ExceptionAnswers.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Services;

/// <summary>
/// Answering a confirm-screen card. An answer is written as the very decision a person would record in the full review
/// (a confirmed carry verdict, a kept or dropped detection), so the summary, the full review and the promote all read it
/// the same way; <see cref="UpdateExceptionAnswer.Undo"/> puts the update's default back.
/// </summary>
public partial class WallUpdateSessionService
{
    /// <summary>The refusal of "not the same hold" on a card that pairs nothing.</summary>
    public const string NotSameRefused =
        "\"Not the same hold\" answers an unsure match or a hand-placed merge; this card pairs no detection with an old hold.";

    /// <summary>The refusal of a "Remove" answer for a hold on a panel other than the centre.</summary>
    public const string OffCentreRemoveRefused =
        "A hold on a panel other than the centre cannot be removed in this update: it is carried over, and the look-over "
        + "after Apply asks about it again.";

    /// <inheritdoc/>
    public async Task DecideUpdateExceptionAsync(Guid wallId, Guid exceptionId, UpdateExceptionAnswer answer, Guid? expectedSessionId = null)
    {
        var user = await currentUserService.GetCurrentUserAsync();
        await using var db = await dbContextFactory.CreateDbContextAsync();
        db.CurrentUserId = user.Id;
        await WallAdminGuard.EnsureWallAdminAsync(db, wallId, user.Id, CancellationToken.None);

        await using var transaction = await db.Database.BeginTransactionAsync();
        var session = await RequireOpenAsync(db, wallId, expectedSessionId);
        await LockSessionAsync(db, session.Id);
        var card = await db.WallUpdateExceptions.FirstOrDefaultAsync(e => e.Id == exceptionId && e.SessionId == session.Id)
            ?? throw new UserFacingException("That check is no longer part of this update.");
        var now = DateTimeOffset.UtcNow;
        switch (card.Kind)
        {
            case UpdateExceptionKind.ReplacedByOther:
            case UpdateExceptionKind.PossiblyRemoved:
                EnsureNotSameTaken(card, answer);

                // Both kinds: a removal off the centre would be reset to "carried" at the promote while the card read
                // "Removed" (C4), so it is refused instead.
                await EnsureRemovableHereAsync(db, card, answer);
                await AnswerPossiblyRemovedAsync(db, session.Id, card, answer, user.Id, now);
                break;
            case UpdateExceptionKind.LowConfidenceMatch:
                await AnswerLowConfidenceAsync(db, session.Id, card, answer, user.Id, now);
                break;
            case UpdateExceptionKind.MatchedToHandPlaced:
                await AnswerHandPlacedMergeAsync(db, session.Id, card, answer, user.Id, now);
                break;
            case UpdateExceptionKind.HandPlacedAmbiguous:
                EnsureNotSameTaken(card, answer);
                break;
            default:
                EnsureNotSameTaken(card, answer);
                await AnswerConflictingNewAsync(db, session.Id, card, answer);
                break;
        }

        card.Status = answer switch
        {
            UpdateExceptionAnswer.Keep => UpdateExceptionStatus.Kept,
            UpdateExceptionAnswer.Remove or UpdateExceptionAnswer.NotSame => UpdateExceptionStatus.Removed,
            _ => UpdateExceptionStatus.Pending,
        };
        card.DecidedByUserId = answer == UpdateExceptionAnswer.Undo ? null : user.Id;
        card.DecidedAt = answer == UpdateExceptionAnswer.Undo ? null : now;
        WallUpdateSessions.Touch(session, user.Id);
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
    }

    /// <summary>
    /// A removal is only promoted for a hold on the centre panel (<see cref="CarryoverScope"/> resets any other to
    /// "carried"), so it is refused for a replaced or possibly removed hold elsewhere instead of being undone unseen.
    /// </summary>
    private static async Task EnsureRemovableHereAsync(BlocwerkDbContext db, WallUpdateException card, UpdateExceptionAnswer answer)
    {
        if (answer != UpdateExceptionAnswer.Remove)
        {
            return;
        }

        var onCentre = await db.Holds.AnyAsync(h => h.Id == card.OldHoldId && (h.WallPanel == null || (h.WallPanel.Col == 0 && h.WallPanel.Row == 0)));
        if (!onCentre)
        {
            throw new UserFacingException(OffCentreRemoveRefused);
        }
    }

    /// <summary>Keep: carried where it was, confirmed. Remove: removed, confirmed. Undo: carried in place, unconfirmed.</summary>
    private static async Task AnswerPossiblyRemovedAsync(
        BlocwerkDbContext db, Guid sessionId, WallUpdateException card, UpdateExceptionAnswer answer, Guid userId, DateTimeOffset now)
    {
        var row = await CarryRowAsync(db, sessionId, card.OldHoldId!.Value);
        var kind = answer == UpdateExceptionAnswer.Remove ? CarryKind.Removed : CarryKind.Carried;
        if (answer == UpdateExceptionAnswer.Undo)
        {
            CarryConfirmationPolicy.Apply(row, CarryKind.Carried, null, confirmed: false, userId, now);
            CarryConfirmationPolicy.Clear(row);
        }
        else
        {
            CarryConfirmationPolicy.Apply(row, kind, null, confirmed: true, userId, now);
        }

        row.UpdatedAt = now;
    }

    /// <summary>"Not the same hold" is an answer only where a detection was paired with an old hold.</summary>
    private static void EnsureNotSameTaken(WallUpdateException card, UpdateExceptionAnswer answer)
    {
        if (answer == UpdateExceptionAnswer.NotSame)
        {
            throw new UserFacingException(NotSameRefused);
        }
    }

    /// <summary>
    /// Keep: carried onto the matched detection, confirmed. Undo: the same verdict, unconfirmed. NotSame: the pair is taken
    /// apart as the full review's unlink does (the old hold carried blind, unconfirmed; the card's answer marks it for review)
    /// and the detection becomes a new hold. Remove is not an answer here: the old hold may well still be there.
    /// </summary>
    private static async Task AnswerLowConfidenceAsync(
        BlocwerkDbContext db, Guid sessionId, WallUpdateException card, UpdateExceptionAnswer answer, Guid userId, DateTimeOffset now)
    {
        if (answer == UpdateExceptionAnswer.Remove)
        {
            throw new UserFacingException("A match is confirmed here, answered \"not the same hold\" (notSame), or changed in the full review.");
        }

        var row = await CarryRowAsync(db, sessionId, card.OldHoldId!.Value);
        var apart = answer == UpdateExceptionAnswer.NotSame;
        CarryConfirmationPolicy.Apply(
            row, CarryKind.Carried, apart ? null : card.StagedHoldId, confirmed: answer == UpdateExceptionAnswer.Keep, userId, now);
        if (answer != UpdateExceptionAnswer.Keep)
        {
            CarryConfirmationPolicy.Clear(row);
        }

        row.UpdatedAt = now;
        if (apart && card.StagedHoldId is { } holdId)
        {
            await KeepDetectionAsNewAsync(db, sessionId, holdId);
        }
    }

    /// <summary>
    /// Keep or Undo: the old hold takes the detection (confirmed on Keep). Remove: the merge is undone, the old hold stays
    /// as it was and the detection becomes a new hold beside it.
    /// </summary>
    private static async Task AnswerHandPlacedMergeAsync(
        BlocwerkDbContext db, Guid sessionId, WallUpdateException card, UpdateExceptionAnswer answer, Guid userId, DateTimeOffset now)
    {
        var row = await CarryRowAsync(db, sessionId, card.OldHoldId!.Value);
        var rejected = answer is UpdateExceptionAnswer.Remove or UpdateExceptionAnswer.NotSame;
        CarryConfirmationPolicy.Apply(row, CarryKind.Carried, rejected ? null : card.StagedHoldId, confirmed: answer != UpdateExceptionAnswer.Undo, userId, now);
        if (answer == UpdateExceptionAnswer.Undo)
        {
            CarryConfirmationPolicy.Clear(row);
        }

        row.UpdatedAt = now;
        if (rejected)
        {
            await KeepDetectionAsNewAsync(db, sessionId, card.StagedHoldId!.Value);
        }
    }

    /// <summary>A detection taken apart from an old hold becomes a new hold: un-discarded on the centre, not removed on a neighbour.</summary>
    private static async Task KeepDetectionAsNewAsync(BlocwerkDbContext db, Guid sessionId, Guid holdId)
    {
        var detection = await db.WallUpdateHoldDecisions.FirstOrDefaultAsync(d =>
            d.SessionId == sessionId && d.Kind == WallUpdateHoldDecisionKind.NewCentreHold && d.HoldId == holdId);
        if (detection is not null)
        {
            detection.Discarded = false;
            return;
        }

        var removal = await db.WallUpdateNeighbourDecisions.FirstOrDefaultAsync(d =>
            d.SessionId == sessionId && d.Kind == WallUpdateNeighbourDecisionKind.Removed && d.HoldId == holdId);
        if (removal is not null)
        {
            db.WallUpdateNeighbourDecisions.Remove(removal);
            return;
        }

        // A centre detection no decision mentions yet (the merge consumed it): record it as kept. A neighbour's is kept anyway.
        var onCentre = await db.Holds.AnyAsync(h => h.Id == holdId && h.WallPanel != null && h.WallPanel.Col == 0 && h.WallPanel.Row == 0);
        if (onCentre)
        {
            db.WallUpdateHoldDecisions.Add(new WallUpdateHoldDecision
            {
                SessionId = sessionId, Kind = WallUpdateHoldDecisionKind.NewCentreHold, HoldId = holdId, Discarded = false,
            });
        }
    }

    /// <summary>Keep: the detection becomes a new hold. Remove or Undo: it is left out, as the photo check suggested.</summary>
    private static async Task AnswerConflictingNewAsync(BlocwerkDbContext db, Guid sessionId, WallUpdateException card, UpdateExceptionAnswer answer)
    {
        var holdId = card.StagedHoldId!.Value;
        var keep = answer == UpdateExceptionAnswer.Keep;
        var centre = await db.WallUpdateHoldDecisions.FirstOrDefaultAsync(d =>
            d.SessionId == sessionId && d.Kind == WallUpdateHoldDecisionKind.NewCentreHold && d.HoldId == holdId);
        if (centre is not null)
        {
            centre.Discarded = !keep;
            centre.UpdatedAt = DateTimeOffset.UtcNow;
            return;
        }

        var removal = await db.WallUpdateNeighbourDecisions.FirstOrDefaultAsync(d =>
            d.SessionId == sessionId && d.Kind == WallUpdateNeighbourDecisionKind.Removed && d.HoldId == holdId);
        if (keep && removal is not null)
        {
            db.WallUpdateNeighbourDecisions.Remove(removal);
        }
        else if (!keep && removal is null && card.PanelId is { } panelId)
        {
            db.WallUpdateNeighbourDecisions.Add(new WallUpdateNeighbourDecision
            {
                SessionId = sessionId, Kind = WallUpdateNeighbourDecisionKind.Removed, PanelId = panelId, HoldId = holdId,
            });
        }
    }

    private static async Task<WallUpdateHoldDecision> CarryRowAsync(BlocwerkDbContext db, Guid sessionId, Guid oldHoldId)
    {
        var row = await db.WallUpdateHoldDecisions.FirstOrDefaultAsync(d =>
            d.SessionId == sessionId && d.Kind == WallUpdateHoldDecisionKind.Carry && d.HoldId == oldHoldId);
        if (row is null)
        {
            row = new WallUpdateHoldDecision { SessionId = sessionId, Kind = WallUpdateHoldDecisionKind.Carry, HoldId = oldHoldId };
            db.WallUpdateHoldDecisions.Add(row);
        }

        return row;
    }
}
