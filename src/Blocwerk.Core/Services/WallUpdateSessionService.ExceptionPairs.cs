// <copyright file="WallUpdateSessionService.ExceptionPairs.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Services;

/// <summary>
/// The detection of a card that pairs it with an old hold (an unsure match, a hand-placed merge): taken apart, it becomes a
/// new hold; put back (undo, or "the match is right" after all), it is the old hold's twin again and its own decision returns
/// to the update's default, so the summary is the one before the card was answered.
/// </summary>
public partial class WallUpdateSessionService
{
    /// <summary>Whether a card's answer has taken its pair apart.</summary>
    private static bool IsApart(UpdateExceptionKind kind, UpdateExceptionStatus status) =>
        status == UpdateExceptionStatus.NotSame || (kind == UpdateExceptionKind.MatchedToHandPlaced && status == UpdateExceptionStatus.Removed);

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

    /// <summary>
    /// The detection is a twin again: its own decision goes back to what the quick review records for a twin. On the centre
    /// that is none (a twin is never a new centre hold); on a neighbour, the "removed" the triage suggested for it, if any
    /// (<see cref="Hold.DiscardReason"/>, which the defaults read too).
    /// </summary>
    private static async Task RestoreDetectionDefaultAsync(BlocwerkDbContext db, Guid sessionId, Guid holdId)
    {
        var hold = await db.Holds.AsNoTracking()
            .Where(h => h.Id == holdId)
            .Select(h => new { h.WallPanelId, h.DiscardReason, OnCentre = h.WallPanel != null && h.WallPanel.Col == 0 && h.WallPanel.Row == 0 })
            .FirstOrDefaultAsync();
        if (hold is null)
        {
            return;
        }

        if (hold.OnCentre)
        {
            var rows = await db.WallUpdateHoldDecisions
                .Where(d => d.SessionId == sessionId && d.Kind == WallUpdateHoldDecisionKind.NewCentreHold && d.HoldId == holdId)
                .ToListAsync();
            db.WallUpdateHoldDecisions.RemoveRange(rows);
            return;
        }

        var removed = await db.WallUpdateNeighbourDecisions.AnyAsync(d =>
            d.SessionId == sessionId && d.Kind == WallUpdateNeighbourDecisionKind.Removed && d.HoldId == holdId);
        if (hold.DiscardReason is not null && !removed && hold.WallPanelId is { } panelId)
        {
            db.WallUpdateNeighbourDecisions.Add(new WallUpdateNeighbourDecision
            {
                SessionId = sessionId, Kind = WallUpdateNeighbourDecisionKind.Removed, PanelId = panelId, HoldId = holdId,
            });
        }
    }
}
