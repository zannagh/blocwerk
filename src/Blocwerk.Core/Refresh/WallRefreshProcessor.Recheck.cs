// <copyright file="WallRefreshProcessor.Recheck.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Refresh;

/// <summary>
/// "Review now or wait for 3D": the panel update is ready to check before this visit's 3D model is. Once the model is
/// the wall's active one and has its textures, the quick review is worked out again with it (the triage then sees which
/// detections are holds the wall already has, or lie off the wall) — unless the user changed the decisions in the full
/// review, before or during the check: those are theirs and are kept. Nothing is applied here, and a failed check never
/// costs the prepared update.
/// </summary>
public sealed partial class WallRefreshProcessor
{
    /// <summary>A check still marked running after this long was interrupted (a restart); Apply is allowed again.</summary>
    public static readonly TimeSpan RecheckStale = TimeSpan.FromMinutes(10);

    private const string CheckFailed = "The check against the 3D model did not work; the update is as prepared. Check the summary, then apply";

    /// <summary>This run's 3D model when it is the wall's active model and has textures: what the check can use.</summary>
    public static async Task<Guid?> Ready3DModelAsync(BlocwerkDbContext db, WallRefresh refresh, CancellationToken ct)
    {
        if (!refresh.CaptureStarted || refresh.CaptureId is not { } captureId)
        {
            return null;
        }

        var modelId = await db.WallCaptures.Where(c => c.Id == captureId).Select(c => c.GeometryModelId).FirstOrDefaultAsync(ct);
        if (modelId is not { } id)
        {
            return null;
        }

        var ready = await db.WallGeometryModels.AnyAsync(m => m.Id == id && m.IsActive, ct)
            && await db.WallGeometryTextures.AnyAsync(t => t.GeometryModelId == id, ct);
        return ready ? id : null;
    }

    /// <summary>Whether the check against the 3D model is running right now (Apply waits for it).</summary>
    public static bool IsRechecking(WallRefresh refresh, DateTimeOffset now) =>
        refresh.Status == WallRefreshStatus.ReadyToApply
        && RefreshTimeline.Steps(refresh).Any(s => s.Key == RefreshTimeline.Review && s.State == RefreshStepState.Running)
        && now - refresh.UpdatedAt < RecheckStale;

    /// <summary>Whether the quick review should be worked out again with <paramref name="readyModel"/> (not tried with it yet).</summary>
    public static bool NeedsRecheck(WallRefresh refresh, Guid? readyModel, DateTimeOffset now) =>
        refresh.Status == WallRefreshStatus.ReadyToApply && readyModel is { } model
        && RefreshTimeline.Summary(refresh) is { } summary && summary.Attempted3DModelId != model
        && !IsRechecking(refresh, now);

    /// <summary>Runs the check once per model. Never throws: on any failure the prepared update stays as it was.</summary>
    private async Task RecheckAsync(WallRefresh refresh, WallRefreshActors actors, CancellationToken ct)
    {
        Guid? model = null;
        try
        {
            await using (var db = dbContextFactory.CreateDbContext())
            {
                model = await Ready3DModelAsync(db, refresh, ct);
            }

            if (NeedsRecheck(refresh, model, DateTimeOffset.UtcNow) && RefreshTimeline.Summary(refresh) is { } summary)
            {
                await RecheckWithAsync(refresh, actors, summary, model!.Value, ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Wall refresh {RefreshId}: the check against the 3D model failed; the prepared update stays", refresh.Id);
            await MarkCheckFailedAsync(refresh, model);
        }
    }

    private async Task MarkCheckFailedAsync(WallRefresh refresh, Guid? model)
    {
        try
        {
            await SaveAsync(
                refresh,
                r =>
                {
                    var summary = RefreshTimeline.Summary(r);
                    Checked(r, summary is null ? null : summary with { Attempted3DModelId = model ?? summary.Attempted3DModelId }, CheckFailed);
                },
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            // Even the note could not be written: the row is still ReadyToApply, and a stuck "running" check expires.
            logger.LogWarning(ex, "Wall refresh {RefreshId}: could not note the failed 3D check", refresh.Id);
        }
    }

    private async Task RecheckWithAsync(WallRefresh refresh, WallRefreshActors actors, RefreshSummary summary, Guid model, CancellationToken ct)
    {
        const string kept = "The 3D model is ready; your own choices in the full review are kept. Check, then apply";
        var session = await actors.Sessions.GetOpenSessionAsync(refresh.WallId);
        var ours = session is not null && session.Id == refresh.UpdateSessionId;
        if (!ours || summary.DecisionsRecordedAt is not { } recorded || session!.UpdatedAt > recorded)
        {
            await SaveAsync(refresh, r => Checked(r, summary with { Attempted3DModelId = model }, kept), ct);
            return;
        }

        await StepAsync(refresh, RefreshTimeline.Review, RefreshStepState.Running, "Checking the new detections against the 3D model", ct);
        var byPanel = await StagedHoldsByPanelAsync(refresh.WallId, ct);
        var rechecked = await TriageAndRecordAsync(refresh, actors, byPanel, summary.Panels, recorded, ct);
        if (rechecked is null)
        {
            await SaveAsync(refresh, r => Checked(r, summary with { Attempted3DModelId = model }, kept), ct);
            return;
        }

        var detail = rechecked.CheckedWithModelId is null ? "Check the summary, then apply" : "Checked against the 3D model. Check the summary, then apply";
        await SaveAsync(refresh, r => Checked(r, rechecked with { Attempted3DModelId = model }, detail), ct);
        logger.LogInformation(
            "Wall refresh {RefreshId}: rechecked with 3D model {ModelId}: {Dropped} detections left out ({By3D} by the model), {New} new holds",
            refresh.Id, model, rechecked.DroppedDetections, rechecked.DroppedByThe3DModel, rechecked.NewHolds);
    }

    private static void Checked(WallRefresh refresh, RefreshSummary? summary, string detail)
    {
        if (summary is not null)
        {
            refresh.SummaryJson = RefreshTimeline.Write(summary);
        }

        RefreshTimeline.Set(refresh, RefreshTimeline.Review, RefreshStepState.Waiting, detail);
    }

    /// <summary>
    /// Matches, triages (with the wall's 3D model), records the quick review's default decisions on the update session in
    /// one go and sums them up for the confirm screen. With <paramref name="onlyIfUnchangedSince"/>, nothing is recorded
    /// (null is returned) when the session was changed after it — the user's choices win.
    /// </summary>
    private async Task<RefreshSummary?> TriageAndRecordAsync(
        WallRefresh refresh,
        WallRefreshActors actors,
        IReadOnlyDictionary<Guid, IReadOnlyList<Guid>> byPanel,
        IReadOnlyList<string> panels,
        DateTimeOffset? onlyIfUnchangedSince,
        CancellationToken ct)
    {
        Guid? model;
        await using (var db = dbContextFactory.CreateDbContext())
        {
            model = await Ready3DModelAsync(db, refresh, ct);
        }

        var matched = await actors.BigUpdate.ResumeAsync(refresh.WallId, use3DEvidence: true);
        var quick = QuickUpdateDefaults.Build(matched, byPanel);
        var cards = UpdateExceptionBuilder.Build(matched, quick);
        if (await RecordDecisionsAsync(refresh.WallId, actors.Sessions, quick, cards, onlyIfUnchangedSince) is not { } recordedAt)
        {
            return null;
        }

        // Summed up from the decisions as stored, the way Apply reads them, so the version below is what Apply checks.
        var promotable = await RefreshDecisions.LoadAsync(refresh.WallId, actors, matched, await StagedHoldsByPanelAsync(refresh.WallId, ct));
        var summary = await SummarizeAsync(refresh.WallId, RefreshDecisions.AsQuick(promotable), promotable.PendingRelocations, panels, ct, promotable.Moves);
        var dropped = promotable.Folded.RemovedNewCenterHoldIds.Concat(promotable.Folded.Neighbours.SelectMany(n => n.RemovedNeighbourHoldIds));
        var by3D = dropped.Count(id => matched.SuggestedNewDiscards?.GetValueOrDefault(id) is NewHoldDiscardReason.KnownHoldIn3D or NewHoldDiscardReason.OffWallIn3D);
        return RefreshDecisions.Stamp(summary, promotable) with
        {
            DroppedByThe3DModel = by3D,
            NewSeenIn3D = matched.SeenIn3DHoldIds?.Count ?? 0,
            CheckedWithModelId = model is not null && matched.Evidence3DModelId == model ? model : null,
            Attempted3DModelId = model,
            DecisionsRecordedAt = recordedAt,
        };
    }
}
