// <copyright file="UpdateReviewService.HoldAnswers.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Refresh.Review;

/// <summary>
/// The answers about holds that are not cards, each in one transaction under the session's row lock: the person's
/// answer (<see cref="WallUpdateReviewAnswer"/>) and its effect on the decision rows the promote reads. Not found: the
/// carry verdict (kept and confirmed, or removed). A detection: the centre's keep/discard row, or a neighbour panel's
/// removal row (with any link of it). Undo puts the update's default back and forgets the answer.
/// </summary>
public sealed partial class UpdateReviewService
{
    /// <summary>
    /// Writes a run of answers about holds in one transaction, in order. A refused answer changes nothing of its own: the
    /// ones before it are committed, and the refusal is reported with how many were written in all.
    /// </summary>
    /// <returns>How many of the run were written.</returns>
    private async Task<int> WriteHoldRunAsync(WallRefresh refresh, Guid userId, IReadOnlyList<ReviewAnswerRequest> run, int writtenBefore)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync();
        db.CurrentUserId = userId;
        await using var transaction = await db.Database.BeginTransactionAsync();
        var session = await WallUpdateSessions.FindOpenAsync(db, refresh.WallId);
        WallUpdateSessions.EnsureIs(refresh.WallId, refresh.UpdateSessionId, session, WallUpdateSessionSupersededException.ChangeRefused);
        var open = session ?? throw new UserFacingException("This update was replaced or discarded in the full review.");
        await db.WallUpdateSessions.Where(s => s.Id == open.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.UpdatedAt, DateTimeOffset.UtcNow));
        var written = 0;
        UserFacingException? refused = null;
        foreach (var answer in run)
        {
            try
            {
                var (prefix, holdId) = ParseKey(answer.Key);
                await WriteHoldAnswerAsync(db, open, userId, holdId, SubjectOf(prefix), answer.Answer);
                written++;
            }
            catch (UserFacingException ex)
            {
                refused = ex;
                break;
            }
        }

        WallUpdateSessions.Touch(open, userId);
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        return refused is null ? written : throw new ReviewAnswersStoppedException(writtenBefore + written, run[written].Key, refused);
    }

    private static async Task WriteHoldAnswerAsync(
        BlocwerkDbContext db, WallUpdateSession open, Guid userId, Guid holdId, ReviewSubject subject, ReviewAnswer answer)
    {
        var now = DateTimeOffset.UtcNow;
        if (subject == ReviewSubject.NotFound)
        {
            await AnswerNotFoundAsync(db, open.Id, holdId, answer, userId, now);
        }
        else
        {
            await AnswerDetectionAsync(db, open, holdId, subject, answer);
        }

        await RecordAnswerAsync(db, open.Id, holdId, subject, answer, userId, now);
    }

    /// <summary>Still there: kept where it was, confirmed. Gone: removed, confirmed. Undo: kept, unconfirmed.</summary>
    private static async Task AnswerNotFoundAsync(BlocwerkDbContext db, Guid sessionId, Guid oldHoldId, ReviewAnswer answer, Guid userId, DateTimeOffset now)
    {
        var row = await db.WallUpdateHoldDecisions.FirstOrDefaultAsync(d =>
            d.SessionId == sessionId && d.Kind == WallUpdateHoldDecisionKind.Carry && d.HoldId == oldHoldId);
        if (row is null || row.PairedHoldId is not null)
        {
            throw new UserFacingException("This old hold is paired with a detection now; answer it on its card.");
        }

        switch (answer)
        {
            case ReviewAnswer.StillThere:
                CarryConfirmationPolicy.Apply(row, CarryKind.Carried, null, confirmed: true, userId, now);
                break;
            case ReviewAnswer.Gone:
                CarryConfirmationPolicy.Apply(row, CarryKind.Removed, null, confirmed: true, userId, now);
                break;
            case ReviewAnswer.Undo:
                CarryConfirmationPolicy.Apply(row, CarryKind.Carried, null, confirmed: false, userId, now);
                CarryConfirmationPolicy.Clear(row);
                break;
            default:
                throw new UserFacingException("A hold that was not found again is answered \"still there\" or \"gone\".");
        }

        row.UpdatedAt = now;
    }

    private static async Task AnswerDetectionAsync(BlocwerkDbContext db, WallUpdateSession session, Guid holdId, ReviewSubject subject, ReviewAnswer answer)
    {
        var hold = await db.Holds.AsNoTracking()
            .Where(h => h.Id == holdId && h.WallId == session.WallId && h.Generation == session.StagedGeneration && h.WallPanelId != null)
            .Select(h => new { PanelId = h.WallPanelId!.Value, OnCentre = h.WallPanel!.Col == 0 && h.WallPanel.Row == 0 })
            .FirstOrDefaultAsync()
            ?? throw new UserFacingException("That detection is no longer part of this update.");
        var leftOut = LeavesOut(subject, answer);
        if (hold.OnCentre)
        {
            await SetCentreDiscardedAsync(db, session.Id, holdId, leftOut);
        }
        else
        {
            await SetNeighbourRemovedAsync(db, session.Id, hold.PanelId, holdId, leftOut);
        }
    }

    /// <summary>Whether the answer leaves the detection out (Undo: its default, kept when new, out when the triage left it out).</summary>
    private static bool LeavesOut(ReviewSubject subject, ReviewAnswer answer) => (subject, answer) switch
    {
        (ReviewSubject.NewHold, ReviewAnswer.KeepNew) => false,
        (ReviewSubject.NewHold, ReviewAnswer.NotAHold) => true,
        (ReviewSubject.NewHold, ReviewAnswer.Undo) => false,
        (ReviewSubject.LeftOut, ReviewAnswer.LeaveOut) => true,
        (ReviewSubject.LeftOut, ReviewAnswer.AddAsHold) => false,
        (ReviewSubject.LeftOut, ReviewAnswer.Undo) => true,
        (ReviewSubject.NewHold, _) => throw new UserFacingException("A new hold is answered \"keep\" or \"not a hold\"."),
        _ => throw new UserFacingException("A left-out detection is answered \"leave out\" or \"add as a hold\"."),
    };

    private static async Task SetCentreDiscardedAsync(BlocwerkDbContext db, Guid sessionId, Guid holdId, bool discarded)
    {
        // Local first: one transaction may answer the same detection twice before it saves.
        var row = db.WallUpdateHoldDecisions.Local.FirstOrDefault(d =>
                d.SessionId == sessionId && d.Kind == WallUpdateHoldDecisionKind.NewCentreHold && d.HoldId == holdId)
            ?? await db.WallUpdateHoldDecisions.FirstOrDefaultAsync(d =>
                d.SessionId == sessionId && d.Kind == WallUpdateHoldDecisionKind.NewCentreHold && d.HoldId == holdId);
        if (row is null)
        {
            row = new WallUpdateHoldDecision { SessionId = sessionId, Kind = WallUpdateHoldDecisionKind.NewCentreHold, HoldId = holdId };
            db.WallUpdateHoldDecisions.Add(row);
        }

        row.Discarded = discarded;
        row.UpdatedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>A neighbour detection is out while its panel's set has a removal row for it; a link of it goes with it.</summary>
    private static async Task SetNeighbourRemovedAsync(BlocwerkDbContext db, Guid sessionId, Guid panelId, Guid holdId, bool removed)
    {
        var stored = await db.WallUpdateNeighbourDecisions.Where(d => d.SessionId == sessionId && d.HoldId == holdId).ToListAsync();
        var rows = stored
            .Concat(db.WallUpdateNeighbourDecisions.Local.Where(d => d.SessionId == sessionId && d.HoldId == holdId))
            .Where(d => db.Entry(d).State != EntityState.Deleted)
            .Distinct()
            .ToList();
        var removal = rows.FirstOrDefault(d => d.Kind == WallUpdateNeighbourDecisionKind.Removed);
        if (!removed)
        {
            if (removal is not null)
            {
                db.WallUpdateNeighbourDecisions.Remove(removal);
            }

            return;
        }

        db.WallUpdateNeighbourDecisions.RemoveRange(rows.Where(d => d.Kind != WallUpdateNeighbourDecisionKind.Removed));
        if (removal is null)
        {
            db.WallUpdateNeighbourDecisions.Add(new WallUpdateNeighbourDecision
            {
                SessionId = sessionId, Kind = WallUpdateNeighbourDecisionKind.Removed, PanelId = panelId, HoldId = holdId,
            });
        }
    }

    private static async Task RecordAnswerAsync(
        BlocwerkDbContext db, Guid sessionId, Guid holdId, ReviewSubject subject, ReviewAnswer answer, Guid userId, DateTimeOffset now)
    {
        // A detection has one answer: one given while it was "new" goes when it is answered as "left out", and back.
        if (subject != ReviewSubject.NotFound)
        {
            var other = subject == ReviewSubject.NewHold ? ReviewSubject.LeftOut : ReviewSubject.NewHold;
            db.WallUpdateReviewAnswers.RemoveRange(await db.WallUpdateReviewAnswers
                .Where(a => a.SessionId == sessionId && a.HoldId == holdId && a.Subject == other).ToListAsync());
        }

        var row = db.WallUpdateReviewAnswers.Local.FirstOrDefault(a => a.SessionId == sessionId && a.HoldId == holdId && a.Subject == subject)
            ?? await db.WallUpdateReviewAnswers.FirstOrDefaultAsync(a => a.SessionId == sessionId && a.HoldId == holdId && a.Subject == subject);
        if (row is not null && db.Entry(row).State == EntityState.Deleted)
        {
            // Undone earlier in this transaction and answered again: the row comes back.
            db.Entry(row).State = EntityState.Modified;
        }

        if (answer == ReviewAnswer.Undo)
        {
            if (row is not null)
            {
                db.WallUpdateReviewAnswers.Remove(row);
            }

            return;
        }

        if (row is null)
        {
            row = new WallUpdateReviewAnswer { SessionId = sessionId, HoldId = holdId, Subject = subject };
            db.WallUpdateReviewAnswers.Add(row);
        }

        (row.Answer, row.ByUserId, row.At) = (answer, userId, now);
    }
}
