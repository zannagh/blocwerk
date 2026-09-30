// <copyright file="WallRefreshProcessor.Run.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Capture;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Refresh;

/// <summary>
/// The run after "Start": read the chosen panel photos, start the 3D capture with every photo (when 3D is set up
/// for the wall), then prepare the panel update (<see cref="StagePanelsAsync"/>). Without 3D this is simply the
/// panel update; a capture that refuses to start is reported and the panel update still goes ahead.
/// </summary>
public sealed partial class WallRefreshProcessor
{
    private const string CaptureNote = "Update panels + 3D";

    private async Task RunAsync(WallRefresh refresh, WallRefreshActors actors, CancellationToken ct)
    {
        var captureId = refresh.CaptureId ?? throw new InvalidOperationException("This update has no photos.");
        var assignments = RefreshTimeline.Picks(refresh)
            .Where(p => p.PhotoId is not null)
            .Select(p => new CapturePanelAssignment(p.PhotoId!.Value, p.Col, p.Row))
            .ToList();
        var panelPhotos = assignments.Count == 0 ? [] : await actors.PanelPhotos.PrepareWallUpdateAsync(captureId, assignments, ct);

        if (!refresh.CaptureStarted)
        {
            await StartCaptureAsync(refresh, actors, captureId, ct);
        }

        if (panelPhotos.Count == 0)
        {
            await FinishWithoutPanelsAsync(refresh, actors, captureId, ct);
            return;
        }

        await StagePanelsAsync(refresh, actors, panelPhotos.Select(p => p.Photo).ToList(), ct);
        if (!refresh.CaptureStarted)
        {
            await actors.Capture.DiscardDraftAsync(captureId);
        }
    }

    /// <summary>
    /// Starts the 3D capture once. Nothing in here can stop the panel update: a refusal or an error (a video file
    /// gone after a restart, a compute hiccup) is reported on the 3D step and the run goes on. A restart after the
    /// capture had already started only records that.
    /// </summary>
    private async Task StartCaptureAsync(WallRefresh refresh, WallRefreshActors actors, Guid captureId, CancellationToken ct)
    {
        var step = RefreshTimeline.Steps(refresh).First(s => s.Key == RefreshTimeline.Capture);
        if (step.State is RefreshStepState.Done or RefreshStepState.Failed or RefreshStepState.Skipped)
        {
            return;
        }

        if (!await CaptureAvailableAsync(refresh.WallId, actors, ct))
        {
            await StepAsync(
                refresh, RefreshTimeline.Capture, RefreshStepState.Skipped,
                "3D is not set up for this wall, so only the panel photos are used.", ct);
            return;
        }

        try
        {
            await StepAsync(refresh, RefreshTimeline.Capture, RefreshStepState.Running, "Starting the 3D capture", ct);
            await StartCaptureOnceAsync(refresh, actors, captureId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Wall refresh {RefreshId}: the 3D capture could not start", refresh.Id);
            var reason = ex is UserFacingException or InvalidOperationException or IOException ? ex.Message : UserFacingException.GenericMessage;
            await StepAsync(
                refresh, RefreshTimeline.Capture, RefreshStepState.Failed,
                $"The 3D capture could not start: {reason} The panel update goes ahead.", ct);
        }
    }

    private async Task StartCaptureOnceAsync(WallRefresh refresh, WallRefreshActors actors, Guid captureId, CancellationToken ct)
    {
        if (await actors.Capture.GetCaptureAsync(captureId) is { Status: not WallCaptureStatus.Draft })
        {
            await MarkCaptureStartedAsync(refresh, "3D capture started", ct);
            return;
        }

        var videoNote = await AttachVideosAsync(refresh, actors, captureId, ct);
        var declarations = await actors.Capture.SuggestDeclarationsAsync(captureId);
        var problems = await actors.Capture.StartAsync(captureId, declarations, CaptureNote);
        if (problems.Count > 0)
        {
            await StepAsync(
                refresh, RefreshTimeline.Capture, RefreshStepState.Failed,
                $"The 3D capture could not start: {string.Join(" ", problems)} The panel update goes ahead.", ct);
            return;
        }

        await MarkCaptureStartedAsync(refresh, videoNote is null ? "3D capture started" : $"3D capture started. {videoNote}", ct);
    }

    private Task MarkCaptureStartedAsync(WallRefresh refresh, string detail, CancellationToken ct) =>
        SaveAsync(
            refresh,
            r =>
            {
                r.CaptureStarted = true;
                RefreshTimeline.Set(r, RefreshTimeline.Capture, RefreshStepState.Done, detail);
            },
            ct);

    /// <summary>3D needs a geometry service, and printed markers on the wall or the marker-free path.</summary>
    private async Task<bool> CaptureAvailableAsync(Guid wallId, WallRefreshActors actors, CancellationToken ct)
    {
        if (!actors.Capture.IsComputeConfigured)
        {
            return false;
        }

        await using var db = dbContextFactory.CreateDbContext();
        var glyphs = await db.Walls.Where(w => w.Id == wallId).Select(w => w.GlyphsEnabled).FirstOrDefaultAsync(ct);
        return glyphs || await actors.Capture.IsMarkerlessAvailableAsync();
    }

    private async Task FinishWithoutPanelsAsync(WallRefresh refresh, WallRefreshActors actors, Guid captureId, CancellationToken ct)
    {
        if (!refresh.CaptureStarted)
        {
            await actors.Capture.DiscardDraftAsync(captureId);
            throw new InvalidOperationException(
                "No photo matched a panel and the 3D capture did not start, so there is nothing to update.");
        }

        const string none = "No panel got a new photo";
        await SaveAsync(
            refresh,
            r =>
            {
                foreach (var key in new[] { RefreshTimeline.Detect, RefreshTimeline.Match, RefreshTimeline.Review, RefreshTimeline.Apply })
                {
                    RefreshTimeline.Set(r, key, RefreshStepState.Skipped, none);
                }

                RefreshTimeline.Set(r, RefreshTimeline.Place, RefreshStepState.Skipped, "Done by the 3D capture when it finishes");
                r.Status = WallRefreshStatus.Done;
                r.CompletedAt = DateTimeOffset.UtcNow;
            },
            ct);
    }
}
