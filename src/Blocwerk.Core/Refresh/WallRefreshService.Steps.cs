// <copyright file="WallRefreshService.Steps.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Refresh;

/// <summary>The user's three decisions: done uploading, start with these photos, apply.</summary>
public sealed partial class WallRefreshService
{
    /// <summary>The 3D step's note for a panels-only run.</summary>
    public const string KeptModelNote = "Panels only: the current 3D model is kept as it is.";

    public async Task SortAsync(Guid refreshId)
    {
        var (db, refresh) = await OpenRefreshAsync(refreshId);
        await using (db)
        {
            EnsureUploading(refresh);
            var photos = await db.WallCapturePhotos.CountAsync(p => p.CaptureId == refresh.CaptureId);
            if (photos == 0)
            {
                throw new UserFacingException("Drop at least one photo first.");
            }

            var videos = RefreshTimeline.Videos(refresh).Count;
            RefreshTimeline.Set(refresh, RefreshTimeline.Upload, RefreshStepState.Done, videos == 0 ? $"{photos} photos" : $"{photos} photos, {videos} videos");
            RefreshTimeline.Set(refresh, RefreshTimeline.Sort, RefreshStepState.Running, "Comparing the photos with the current panel photos");
            refresh.Status = WallRefreshStatus.Sorting;
            await db.SaveChangesAsync();
        }

        queue.Enqueue(refreshId);
    }

    public async Task StartAsync(Guid refreshId, IReadOnlyList<PanelChoice> choices, bool keepModel = false)
    {
        var (db, refresh) = await OpenRefreshAsync(refreshId);
        await using (db)
        {
            if (refresh.Status != WallRefreshStatus.ReadyToStart)
            {
                throw new UserFacingException("This update has already been started.");
            }

            var picks = ApplyChoices(RefreshTimeline.Picks(refresh), choices);
            if (keepModel && picks.All(p => p.PhotoId is null))
            {
                throw new UserFacingException("Choose a new photo for at least one panel to update the panels only.");
            }

            refresh.PanelPicksJson = RefreshTimeline.Write(picks);
            refresh.Status = WallRefreshStatus.Running;
            if (keepModel)
            {
                // A Skipped capture step is what the processor reads as "do not start a capture".
                RefreshTimeline.Set(refresh, RefreshTimeline.Capture, RefreshStepState.Skipped, KeptModelNote);
            }
            else
            {
                RefreshTimeline.Set(refresh, RefreshTimeline.Capture, RefreshStepState.Running, "Starting");
            }

            await db.SaveChangesAsync();
        }

        queue.Enqueue(refreshId);
    }

    public async Task ApplyAsync(Guid refreshId, string? confirmedVersion = null)
    {
        var (db, refresh) = await OpenRefreshAsync(refreshId);
        await using (db)
        {
            // Under the wall's lock, so no background step (a 3D re-check rewriting the decisions) runs meanwhile.
            using (await locks.AcquireForApplyAsync(refresh.WallId))
            {
                await db.Entry(refresh).ReloadAsync();
                await AcceptApplyAsync(db, refresh, confirmedVersion);
            }
        }

        queue.Enqueue(refreshId);
    }

    /// <summary>
    /// Lays the user's choices over the proposal and checks them: a photo serves one panel, and the panels getting a
    /// new photo must be closed toward the centre (the update's centre-first rule), so a refusal comes here rather
    /// than in the background.
    /// </summary>
    public static IReadOnlyList<PanelPick> ApplyChoices(IReadOnlyList<PanelPick> picks, IReadOnlyList<PanelChoice> choices)
    {
        var chosen = choices.ToDictionary(c => (c.Col, c.Row), c => c.PhotoId);
        var result = picks
            .Select(p => chosen.TryGetValue((p.Col, p.Row), out var photo) && photo != p.PhotoId ? p with { PhotoId = photo } : p)
            .ToList();
        var used = result.Where(p => p.PhotoId is not null).ToList();
        if (used.Select(p => p.PhotoId).Distinct().Count() != used.Count)
        {
            throw new UserFacingException("A photo can only be the new photo of one panel.");
        }

        var positions = used.Select(p => (p.Col, p.Row)).ToHashSet();
        foreach (var (col, row) in positions.Where(p => p != (0, 0)))
        {
            var toward = TowardCentre(col, row);
            if (!positions.Contains(toward))
            {
                throw new UserFacingException(
                    $"{PanelPositionName.Describe(col, row)} can only get a new photo together with "
                    + $"{PanelPositionName.InSentence(toward.Col, toward.Row)}, which is closer to the centre.");
            }
        }

        return result;
    }

    private async Task AcceptApplyAsync(BlocwerkDbContext db, WallRefresh refresh, string? confirmedVersion)
    {
        if (refresh.Status != WallRefreshStatus.ReadyToApply)
        {
            throw new UserFacingException("There is nothing to apply yet.");
        }

        var shown = RefreshTimeline.Summary(refresh)?.DecisionsVersion;
        if (confirmedVersion is not null && shown != confirmedVersion)
        {
            throw new UserFacingException("The summary changed while you were looking at it. Check it again, then apply.");
        }

        if (WallRefreshProcessor.HasSummaryRequest(refresh))
        {
            // Asked again either way; a request that went stale (a lost job) no longer holds Apply back: the version
            // check below and the background apply's own check keep it from promoting an unseen summary.
            queue.Enqueue(refresh.Id);
            if (WallRefreshProcessor.IsResummarizing(refresh, DateTimeOffset.UtcNow))
            {
                throw new UserFacingException("The summary is being updated with your answers. Try again in a moment.");
            }

            refresh.SummaryRequestedAt = null;
        }

        if (WallRefreshProcessor.IsRechecking(refresh, DateTimeOffset.UtcNow)
            || WallRefreshProcessor.NeedsRecheck(refresh, await WallRefreshProcessor.Ready3DModelAsync(db, refresh, CancellationToken.None), DateTimeOffset.UtcNow))
        {
            queue.Enqueue(refresh.Id);
            throw new UserFacingException(BeingChecked);
        }

        // The background apply promotes only decisions with exactly this version.
        refresh.ConfirmedDecisionsVersion = confirmedVersion ?? shown;
        refresh.Status = WallRefreshStatus.Applying;
        refresh.Error = null;
        RefreshTimeline.Set(refresh, RefreshTimeline.Apply, RefreshStepState.Running, "Applying the panel update");
        await db.SaveChangesAsync();
    }

    private static (int Col, int Row) TowardCentre(int col, int row) =>
        col != 0 ? (col - Math.Sign(col), row) : (col, row - Math.Sign(row));
}
