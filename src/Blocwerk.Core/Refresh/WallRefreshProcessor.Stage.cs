// <copyright file="WallRefreshProcessor.Stage.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Refresh;

/// <summary>
/// The panel update in quick mode: stage and detect (<see cref="IWallBigUpdateService.StageAsync"/>), match
/// straight away (no pre-match touch-up), record the default decisions on the update session exactly as the
/// step-by-step review would (<see cref="QuickUpdateDefaults"/>), and stop at the confirm screen. Nothing goes
/// live until the user confirms; the full review can still be opened on the same session.
/// </summary>
public sealed partial class WallRefreshProcessor
{
    private async Task StagePanelsAsync(
        WallRefresh refresh, WallRefreshActors actors, IReadOnlyList<BigUpdatePhoto> photos, CancellationToken ct)
    {
        // Whether the open session is this run's is decided on the row as stored, BEFORE this pass marks Detect running:
        // the adoption rule reads "Detect already started" as "a previous pass of this run staged it".
        var owned = await OwnedSessionIdAsync(refresh, actors);
        await StepAsync(refresh, RefreshTimeline.Detect, RefreshStepState.Running, $"Looking for holds on {photos.Count} new panel photos", ct);
        var (staged, sessionId, unchangedSince) = await StageOrAdoptAsync(refresh, actors, photos, owned);
        await SaveAsync(refresh, r => r.UpdateSessionId = sessionId, ct);

        var byPanel = await StagedHoldsByPanelAsync(refresh.WallId, ct);
        await SaveAsync(
            refresh,
            r =>
            {
                RefreshTimeline.Set(r, RefreshTimeline.Detect, RefreshStepState.Done, $"{byPanel.Values.Sum(v => v.Count)} holds found on {staged.Neighbours.Count + 1} {(staged.Neighbours.Count == 0 ? "panel" : "panels")}");
                RefreshTimeline.Set(r, RefreshTimeline.Match, RefreshStepState.Running, refresh.FullRevision ? "Starting over: nothing old is matched" : "Finding the old holds on the new photos");
            },
            ct);

        var panels = photos.Select(p => PanelPositionName.Describe(p.Col, p.Row)).ToList();

        // Only over a session nobody has written since it was staged (or adopted): a person's choices made meanwhile, in
        // the full review, win over the defaults, and the summary is then made from those choices instead.
        var summary = await TriageAndRecordAsync(refresh, actors, byPanel, panels, unchangedSince, ct, summarizeIfRefused: true)
            ?? throw new InvalidOperationException("The quick review's decisions could not be recorded.");
        if (summary.EditedInFullReview)
        {
            logger.LogInformation("Wall refresh {RefreshId}: the update session was changed by a person; their choices are kept", refresh.Id);
        }

        await SaveAsync(
            refresh,
            r =>
            {
                r.SummaryJson = RefreshTimeline.Write(summary);
                r.Status = WallRefreshStatus.ReadyToApply;
                RefreshTimeline.Set(r, RefreshTimeline.Match, RefreshStepState.Done, RefreshTimeline.MatchDetail(summary));
                RefreshTimeline.Set(r, RefreshTimeline.Review, RefreshStepState.Waiting, "Check the summary, then apply");
            },
            ct);
    }

    /// <summary>
    /// Stages the photos, or picks up the staging this run already made (<paramref name="owned"/>). Returns the session and
    /// the stamp the defaults may be written over: an adopted session only while nobody has written it since it was opened
    /// (its creation), a fresh or recorded one only while nobody has written it since this pass read it.
    /// </summary>
    private static async Task<(BigUpdateSession Staged, Guid? SessionId, DateTimeOffset? UnchangedSince)> StageOrAdoptAsync(
        WallRefresh refresh, WallRefreshActors actors, IReadOnlyList<BigUpdatePhoto> photos, Guid? owned)
    {
        var adopted = owned is not null && refresh.UpdateSessionId is null;
        var staged = owned is not null
            ? await actors.BigUpdate.GetStagedAsync(refresh.WallId)
            : await actors.BigUpdate.StageAsync(refresh.WallId, photos, fullRevision: refresh.FullRevision);
        var open = await actors.Sessions.GetOpenSessionAsync(refresh.WallId);
        if (open is null || (owned is { } id && open.Id != id))
        {
            return (staged, owned ?? open?.Id, null);
        }

        return (staged, open.Id, adopted ? open.CreatedAt : open.UpdatedAt);
    }

    /// <summary>
    /// The wall's open update session when it is this run's: the one it recorded, or, when a restart hit the gap
    /// between staging and recording it, the session its admin opened while this run was running and had none.
    /// </summary>
    private static async Task<Guid?> OwnedSessionIdAsync(WallRefresh refresh, WallRefreshActors actors)
    {
        var open = await actors.Sessions.GetOpenSessionAsync(refresh.WallId);
        if (open is null)
        {
            return null;
        }

        if (refresh.UpdateSessionId is { } recorded)
        {
            return open.Id == recorded ? recorded : null;
        }

        var adoptable = refresh.Status == WallRefreshStatus.Running
            && open.CreatedByUserId == refresh.CreatedByUserId
            && open.CreatedAt >= refresh.CreatedAt
            && RefreshTimeline.Steps(refresh).Any(s => s.Key == RefreshTimeline.Detect && s.State != RefreshStepState.Pending);
        return adoptable ? open.Id : null;
    }

    /// <summary>
    /// Records the quick review's decisions and the confirm screen's cards in one transaction and returns the session's
    /// stamp as committed with them; null when the user changed the session after <paramref name="onlyIfUnchangedSince"/>.
    /// </summary>
    private static Task<DateTimeOffset?> RecordDecisionsAsync(
        WallRefresh refresh,
        IWallUpdateSessionService sessions,
        QuickDecisions quick,
        IReadOnlyList<UpdateExceptionDraft> cards,
        DateTimeOffset? onlyIfUnchangedSince) =>
        sessions.SaveDefaultDecisionsAsync(
            refresh.WallId,
            new DefaultDecisions(
                quick.Carryover, quick.AcceptedNewCentreHoldIds, quick.RemovedNewCentreHoldIds, quick.Neighbours, WallUpdatePhase.Carryover, cards),
            onlyIfUnchangedSince,
            refresh.UpdateSessionId);

    private async Task<Dictionary<Guid, IReadOnlyList<Guid>>> StagedHoldsByPanelAsync(Guid wallId, CancellationToken ct)
    {
        await using var db = dbContextFactory.CreateDbContext();
        var stagedGen = await db.Walls.Where(w => w.Id == wallId).Select(w => w.CurrentGeneration + 1).FirstAsync(ct);
        var holds = await db.Holds.AsNoTracking()
            .Where(h => h.WallId == wallId && h.Generation == stagedGen && h.WallPanelId != null)
            .Select(h => new { h.Id, PanelId = h.WallPanelId!.Value })
            .ToListAsync(ct);
        return holds.GroupBy(h => h.PanelId).ToDictionary(g => g.Key, g => (IReadOnlyList<Guid>)g.Select(h => h.Id).ToList());
    }

    private async Task<RefreshSummary> SummarizeAsync(
        Guid wallId, QuickDecisions quick, int relocations, IReadOnlyList<string> panels, CancellationToken ct, HoldMoves.HoldMovePlan? moves = null)
    {
        var kept = quick.Carryover.Where(d => d.Kind == CarryKind.Carried && d.NewHoldId is null).Select(d => d.OldHoldId).ToList();
        var linkedNew = quick.Neighbours.SelectMany(n => n.Links).Select(l => l.NewHoldId).ToHashSet();
        var consumed = quick.Carryover.Where(d => d.NewHoldId is not null).Select(d => d.NewHoldId!.Value).ToHashSet();
        var neighbourNew = await CountNeighbourNewAsync(wallId, quick, consumed, linkedNew, ct);
        await using var db = dbContextFactory.CreateDbContext();
        var boulders = await db.BoulderHolds.AsNoTracking()
            .Where(bh => kept.Contains(bh.HoldId) && !bh.Boulder.IsHistoric)
            .Select(bh => bh.BoulderId)
            .Distinct()
            .CountAsync(ct);
        var notFound = await NotFoundLinesAsync(moves, ct);
        return new RefreshSummary(
            quick.Carryover.Count - kept.Count,
            kept.Count,
            relocations,
            quick.AcceptedNewCentreHoldIds.Count + neighbourNew,
            quick.RemovedNewCentreHoldIds.Count + quick.Neighbours.Sum(n => n.RemovedNeighbourHoldIds.Count),
            quick.Carryover.Count(d => d.Kind == CarryKind.Removed),
            linkedNew.Count,
            quick.OverlapsLeftOut,
            boulders,
            panels,
            MovedHolds: await MovedLinesAsync(moves, ct),
            NotFoundOnBoulders: notFound.Lines?.Count ?? 0,
            BouldersMarkedNotFound: notFound.Boulders,
            NotFoundHolds: notFound.Lines,
            FullRevision: await FullRevisionFactsAsync(wallId, quick, ct));
    }

    private async Task<int> CountNeighbourNewAsync(
        Guid wallId, QuickDecisions quick, IReadOnlySet<Guid> consumed, IReadOnlySet<Guid> linked, CancellationToken ct)
    {
        var byPanel = await StagedHoldsByPanelAsync(wallId, ct);
        return quick.Neighbours.Sum(n =>
        {
            var removed = n.RemovedNeighbourHoldIds.ToHashSet();
            return (byPanel.GetValueOrDefault(n.PanelId) ?? [])
                .Count(id => !removed.Contains(id) && !consumed.Contains(id) && !linked.Contains(id));
        });
    }
}
