// <copyright file="WallRefreshProcessor.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Capture;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Refresh;

/// <summary>
/// Moves a <see cref="WallRefresh"/> forward on the background worker: sorting the photos to panels, then
/// starting the 3D capture and preparing the panel update in quick mode, then (after the user's confirm)
/// applying it and placing the holds on the 3D model. Each step reads the row's status, so a restart resumes.
/// Runs of different walls proceed side by side; the steps of one wall never overlap.
/// </summary>
public sealed partial class WallRefreshProcessor(
    RootDbContextFactory dbContextFactory,
    IWallRefreshActorFactory actorFactory,
    PanelPhotoPicker picker,
    ICaptureFileStore files,
    ILogger<WallRefreshProcessor> logger,
    ICaptureVideoJoiner? videoJoiner = null,
    WallRefreshLocks? locks = null,
    HoldMoves.HoldMoveOptions? moveOptions = null)
{
    private readonly WallRefreshLocks wallLocks = locks ?? new WallRefreshLocks();

    public async Task ProcessAsync(Guid refreshId, CancellationToken ct)
    {
        var wallId = (await LoadAsync(refreshId, ct))?.WallId;
        if (wallId is null)
        {
            return;
        }

        // Shared with the user's Apply: a re-check and an accepted apply of the run never interleave.
        using (await wallLocks.AcquireAsync(wallId.Value, ct))
        {
            // Read again under the wall's lock: an earlier step of this wall (or an Apply) may have moved it on.
            if (await LoadAsync(refreshId, ct) is { } refresh)
            {
                await ProcessLockedAsync(refresh, ct);
            }
        }
    }

    private async Task ProcessLockedAsync(WallRefresh refresh, CancellationToken ct)
    {
        await using var scope = await actorFactory.CreateAsync(refresh.CreatedByUserId, ct);
        try
        {
            switch (refresh.Status)
            {
                case WallRefreshStatus.Sorting:
                    await SortAsync(refresh, ct);
                    break;
                case WallRefreshStatus.Running:
                    await RunAsync(refresh, scope.Actors, ct);
                    break;
                case WallRefreshStatus.Applying:
                    await ApplyAsync(refresh, scope.Actors, ct);
                    break;
                case WallRefreshStatus.ReadyToApply when HasSummaryRequest(refresh):
                    await ResummarizeAsync(refresh, scope.Actors, ct);
                    break;
                case WallRefreshStatus.ReadyToApply:
                    await RecheckAsync(refresh, scope.Actors, ct);
                    break;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Wall refresh {RefreshId} stopped at {Status}", refresh.Id, refresh.Status);
            await FailAsync(refresh, ex, scope.Actors);
        }
    }

    private async Task<WallRefresh?> LoadAsync(Guid refreshId, CancellationToken ct)
    {
        await using var db = dbContextFactory.CreateDbContext();
        return await db.WallRefreshes.AsNoTracking().FirstOrDefaultAsync(r => r.Id == refreshId, ct);
    }

    /// <summary>Applies <paramref name="change"/> to the stored row and to the caller's copy.</summary>
    private async Task SaveAsync(WallRefresh refresh, Action<WallRefresh> change, CancellationToken ct)
    {
        await using var db = dbContextFactory.CreateDbContext();
        var row = await db.WallRefreshes.FirstAsync(r => r.Id == refresh.Id, ct);
        change(row);
        change(refresh);
        row.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    private Task StepAsync(WallRefresh refresh, string key, RefreshStepState state, string? detail, CancellationToken ct) =>
        SaveAsync(refresh, r => RefreshTimeline.Set(r, key, state, detail), ct);

    /// <summary>
    /// A failed apply goes back to the confirm screen (nothing was changed, it can be retried). A failed 3D re-check of a
    /// prepared update keeps it as it is. Anything else stops
    /// the run with the reason on the running step and discards the panel update it staged, so starting over does
    /// not collide with it; its videos are no longer referenced and the capture sweep removes them.
    /// </summary>
    private async Task FailAsync(WallRefresh refresh, Exception ex, WallRefreshActors actors)
    {
        if (refresh.Status == WallRefreshStatus.ReadyToApply)
        {
            // Only the 3D re-check runs on a prepared update: its failure never releases what the user is about to check.
            await MarkCheckFailedAsync(refresh, null);
            return;
        }

        var applying = refresh.Status == WallRefreshStatus.Applying;
        if (!applying && ex is not WallUpdateSessionConflictException)
        {
            refresh.UpdateSessionId = await OwnedSessionIdAsync(refresh, actors);
            await RefreshCleanup.ReleaseAsync(refresh, actors, files, logger);
        }

        var message = ex is UserFacingException or InvalidOperationException ? ex.Message : UserFacingException.GenericMessage;
        var running = RefreshTimeline.Steps(refresh).FirstOrDefault(s => s.State == RefreshStepState.Running)?.Key;
        await SaveAsync(
            refresh,
            r =>
            {
                r.Error = message.Length > 2000 ? message[..2000] : message;
                if (running is not null)
                {
                    RefreshTimeline.Set(r, running, RefreshStepState.Failed, message);
                }

                r.Status = applying ? WallRefreshStatus.ReadyToApply : WallRefreshStatus.Failed;
                if (applying)
                {
                    r.ConfirmedDecisionsVersion = null;
                    RefreshTimeline.Set(r, RefreshTimeline.Review, RefreshStepState.Waiting, "Could not apply the update; try again.");
                }
            },
            CancellationToken.None);
    }
}
