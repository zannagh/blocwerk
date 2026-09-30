// <copyright file="WallRefreshProcessor.Sort.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Refresh;

/// <summary>Sorting: which uploaded photo becomes which panel's new photo (<see cref="PanelPhotoPicker"/>).</summary>
public sealed partial class WallRefreshProcessor
{
    private const int ProgressEvery = 10;

    private async Task SortAsync(WallRefresh refresh, CancellationToken ct)
    {
        var (panels, photos) = await LoadSortInputAsync(refresh, ct);
        await StepAsync(refresh, RefreshTimeline.Sort, RefreshStepState.Running, $"Comparing {photos.Count} photos with {panels.Count} panels", ct);
        var paths = photos.ToDictionary(p => p.Id, p => p.StoredPath);
        var reported = 0;
        async Task<byte[]?> ReadAsync(Guid id, CancellationToken token)
        {
            var count = Interlocked.Increment(ref reported);
            if (count % ProgressEvery == 0)
            {
                await StepAsync(refresh, RefreshTimeline.Sort, RefreshStepState.Running, $"Comparing photo {count} of {photos.Count}", token);
            }

            return await files.ReadAsync(paths[id], token);
        }

        var picks = await picker.PickAsync(
            panels, photos.Select(p => new PhotoToSort(p.Id, p.Sharpness)).ToList(), ReadAsync, null, ct);
        var assigned = picks.Count(p => p.PhotoId is not null);
        await SaveAsync(
            refresh,
            r =>
            {
                r.PanelPicksJson = RefreshTimeline.Write(picks);
                r.Status = WallRefreshStatus.ReadyToStart;
                RefreshTimeline.Set(r, RefreshTimeline.Sort, RefreshStepState.Done, SortDetail(assigned, panels.Count, photos.Count));
            },
            ct);
    }

    private static string SortDetail(int assigned, int panels, int photos) =>
        $"{assigned} of {panels} panels got a new photo; {photos - assigned} photos go to the 3D model only";

    private async Task<(IReadOnlyList<PanelPhotoTarget> Panels, IReadOnlyList<WallCapturePhoto> Photos)> LoadSortInputAsync(
        WallRefresh refresh, CancellationToken ct)
    {
        await using var db = dbContextFactory.CreateDbContext();
        var live = (await db.WallPanels.AsNoTracking()
                .Where(p => p.WallId == refresh.WallId && p.Photo != null)
                .Select(p => new { p.Id, p.Col, p.Row, p.Generation, p.Photo })
                .ToListAsync(ct))
            .GroupBy(p => (p.Col, p.Row))
            .Select(g => g.OrderByDescending(p => p.Generation).First())
            .OrderBy(p => Math.Abs(p.Col) + Math.Abs(p.Row)).ThenBy(p => p.Row).ThenBy(p => p.Col)
            .Select(p => new PanelPhotoTarget(p.Col, p.Row, p.Photo!, p.Id))
            .ToList();
        var photos = await db.WallCapturePhotos.AsNoTracking()
            .Where(p => p.CaptureId == refresh.CaptureId)
            .OrderBy(p => p.Index)
            .ToListAsync(ct);
        return (live, photos);
    }
}
