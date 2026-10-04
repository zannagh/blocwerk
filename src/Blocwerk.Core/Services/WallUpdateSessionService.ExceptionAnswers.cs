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
    /// <inheritdoc/>
    public async Task DecideUpdateExceptionAsync(Guid wallId, Guid exceptionId, UpdateExceptionAnswer answer)
    {
        var user = await currentUserService.GetCurrentUserAsync();
        await using var db = await dbContextFactory.CreateDbContextAsync();
        db.CurrentUserId = user.Id;
        await WallAdminGuard.EnsureWallAdminAsync(db, wallId, user.Id, CancellationToken.None);

        await using var transaction = await db.Database.BeginTransactionAsync();
        var session = await RequireOpenAsync(db, wallId);
        await LockSessionAsync(db, session.Id);
        var card = await db.WallUpdateExceptions.FirstOrDefaultAsync(e => e.Id == exceptionId && e.SessionId == session.Id)
            ?? throw new InvalidOperationException("That check is no longer part of this update.");
        var now = DateTimeOffset.UtcNow;
        switch (card.Kind)
        {
            case UpdateExceptionKind.PossiblyRemoved:
                await AnswerPossiblyRemovedAsync(db, session.Id, card, answer, user.Id, now);
                break;
            case UpdateExceptionKind.LowConfidenceMatch:
                await AnswerLowConfidenceAsync(db, session.Id, card, answer, user.Id, now);
                break;
            case UpdateExceptionKind.MatchedToHandPlaced:
                await AnswerHandPlacedMergeAsync(db, session.Id, card, answer, user.Id, now);
                break;
            case UpdateExceptionKind.HandPlacedAmbiguous:
                break;
            default:
                await AnswerConflictingNewAsync(db, session.Id, card, answer);
                break;
        }

        card.Status = answer switch
        {
            UpdateExceptionAnswer.Keep => UpdateExceptionStatus.Kept,
            UpdateExceptionAnswer.Remove => UpdateExceptionStatus.Removed,
            _ => UpdateExceptionStatus.Pending,
        };
        card.DecidedByUserId = answer == UpdateExceptionAnswer.Undo ? null : user.Id;
        card.DecidedAt = answer == UpdateExceptionAnswer.Undo ? null : now;
        WallUpdateSessions.Touch(session, user.Id);
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
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

    /// <summary>Keep: carried onto the matched detection, confirmed. Undo: the same verdict, unconfirmed. Remove is not an answer here.</summary>
    private static async Task AnswerLowConfidenceAsync(
        BlocwerkDbContext db, Guid sessionId, WallUpdateException card, UpdateExceptionAnswer answer, Guid userId, DateTimeOffset now)
    {
        if (answer == UpdateExceptionAnswer.Remove)
        {
            throw new InvalidOperationException("A match is confirmed here or changed in the full review.");
        }

        var row = await CarryRowAsync(db, sessionId, card.OldHoldId!.Value);
        CarryConfirmationPolicy.Apply(row, CarryKind.Carried, card.StagedHoldId, confirmed: answer == UpdateExceptionAnswer.Keep, userId, now);
        if (answer == UpdateExceptionAnswer.Undo)
        {
            CarryConfirmationPolicy.Clear(row);
        }

        row.UpdatedAt = now;
    }

    /// <summary>
    /// Keep or Undo: the old hold takes the detection (confirmed on Keep). Remove: the merge is undone, the old hold stays
    /// as it was and the detection becomes a new hold beside it.
    /// </summary>
    private static async Task AnswerHandPlacedMergeAsync(
        BlocwerkDbContext db, Guid sessionId, WallUpdateException card, UpdateExceptionAnswer answer, Guid userId, DateTimeOffset now)
    {
        var row = await CarryRowAsync(db, sessionId, card.OldHoldId!.Value);
        var rejected = answer == UpdateExceptionAnswer.Remove;
        CarryConfirmationPolicy.Apply(row, CarryKind.Carried, rejected ? null : card.StagedHoldId, confirmed: answer != UpdateExceptionAnswer.Undo, userId, now);
        if (answer == UpdateExceptionAnswer.Undo)
        {
            CarryConfirmationPolicy.Clear(row);
        }

        row.UpdatedAt = now;
        if (!rejected)
        {
            return;
        }

        var holdId = card.StagedHoldId!.Value;
        var detection = await db.WallUpdateHoldDecisions.FirstOrDefaultAsync(d =>
            d.SessionId == sessionId && d.Kind == WallUpdateHoldDecisionKind.NewCentreHold && d.HoldId == holdId);
        if (detection is null)
        {
            db.WallUpdateHoldDecisions.Add(new WallUpdateHoldDecision
            {
                SessionId = sessionId, Kind = WallUpdateHoldDecisionKind.NewCentreHold, HoldId = holdId, Discarded = false,
            });
        }
        else
        {
            detection.Discarded = false;
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
