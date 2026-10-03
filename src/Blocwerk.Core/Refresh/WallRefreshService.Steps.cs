// <copyright file="WallRefreshService.Steps.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Refresh;

/// <summary>The user's three decisions: done uploading, start with these photos, apply.</summary>
public sealed partial class WallRefreshService
{
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

    public async Task StartAsync(Guid refreshId, IReadOnlyList<PanelChoice> choices)
    {
        var (db, refresh) = await OpenRefreshAsync(refreshId);
        await using (db)
        {
            if (refresh.Status != WallRefreshStatus.ReadyToStart)
            {
                throw new UserFacingException("This update has already been started.");
            }

            var picks = ApplyChoices(RefreshTimeline.Picks(refresh), choices);
            refresh.PanelPicksJson = RefreshTimeline.Write(picks);
            refresh.Status = WallRefreshStatus.Running;
            RefreshTimeline.Set(refresh, RefreshTimeline.Capture, RefreshStepState.Running, "Starting");
            await db.SaveChangesAsync();
        }

        queue.Enqueue(refreshId);
    }

    public async Task ApplyAsync(Guid refreshId, string? confirmedVersion = null)
    {
        var (db, refresh) = await OpenRefreshAsync(refreshId);
        await using (db)
        {
            if (refresh.Status != WallRefreshStatus.ReadyToApply)
            {
                throw new UserFacingException("There is nothing to apply yet.");
            }

            if (confirmedVersion is not null && RefreshTimeline.Summary(refresh)?.DecisionsVersion != confirmedVersion)
            {
                throw new UserFacingException("The summary changed while you were looking at it. Check it again, then apply.");
            }

            if (WallRefreshProcessor.IsRechecking(refresh, DateTimeOffset.UtcNow)
                || WallRefreshProcessor.NeedsRecheck(refresh, await WallRefreshProcessor.Ready3DModelAsync(db, refresh, CancellationToken.None), DateTimeOffset.UtcNow))
            {
                queue.Enqueue(refreshId);
                throw new UserFacingException("The update is being checked against the new 3D model. Try again in a moment.");
            }

            refresh.Status = WallRefreshStatus.Applying;
            refresh.Error = null;
            RefreshTimeline.Set(refresh, RefreshTimeline.Apply, RefreshStepState.Running, "Applying the panel update");
            await db.SaveChangesAsync();
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

    private static (int Col, int Row) TowardCentre(int col, int row) =>
        col != 0 ? (col - Math.Sign(col), row) : (col, row - Math.Sign(row));
}
