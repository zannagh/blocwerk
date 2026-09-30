// <copyright file="WallRefreshService.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Capture;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Refresh;

/// <summary>
/// The page-facing half of "Update panels + 3D": opening a run, taking the files, and the user's decisions
/// (sort done, start, apply, discard). The work itself runs on <see cref="WallRefreshWorker"/>.
/// </summary>
public sealed partial class WallRefreshService(
    IDbContextFactory<BlocwerkDbContext> dbContextFactory,
    ICurrentUserService currentUserService,
    IWallCaptureService captures,
    WallRefreshQueue queue,
    IWallRefreshActorFactory actorFactory,
    ICaptureFileStore files,
    ILogger<WallRefreshService> logger,
    WallCapturePipelineOptions? pipelineOptions = null,
    IKioskContext? kioskContext = null) : IWallRefreshService
{
    private const string AdminAction = "Updating panels and 3D";
    private static readonly TimeSpan ShownAfterFinish = TimeSpan.FromDays(1);

    private WallCapturePipelineOptions Options => pipelineOptions ?? new WallCapturePipelineOptions();

    public async Task<WallRefreshView?> GetCurrentAsync(Guid wallId)
    {
        var (db, _) = await OpenForAdminAsync(wallId);
        await using (db)
        {
            var refresh = await CurrentAsync(db, wallId);
            return refresh is null ? null : await ToViewAsync(db, refresh);
        }
    }

    public async Task<WallRefreshView> BeginAsync(Guid wallId)
    {
        var (db, userId) = await OpenForAdminAsync(wallId);
        await using (db)
        {
            var current = await CurrentAsync(db, wallId);
            if (current is not null && !IsFinished(current.Status))
            {
                return await ToViewAsync(db, current);
            }

            var draft = new WallCapture { WallId = wallId, CreatedByUserId = userId, Stage = "Uploading photos", Notes = "Update panels + 3D" };
            var refresh = new WallRefresh { WallId = wallId, CreatedByUserId = userId, CaptureId = draft.Id };
            refresh.StepsJson = RefreshTimeline.Write(RefreshTimeline.Initial());
            db.WallCaptures.Add(draft);
            db.WallRefreshes.Add(refresh);
            try
            {
                await db.SaveChangesAsync();
            }
            catch (DbUpdateException)
            {
                // Another admin opened a run at the same moment (one open run per wall, a unique index): join it.
                return await OpenedMeanwhileAsync(wallId);
            }

            logger.LogInformation("Wall refresh {RefreshId} opened on wall {WallId} by {UserId}", refresh.Id, wallId, userId);
            return await ToViewAsync(db, refresh);
        }
    }

    public async Task DiscardAsync(Guid refreshId)
    {
        var (db, refresh) = await OpenRefreshAsync(refreshId);
        await using (db)
        {
            if (refresh.Status is WallRefreshStatus.Sorting or WallRefreshStatus.Running or WallRefreshStatus.Applying)
            {
                throw new UserFacingException("Wait until the current step has finished, then discard.");
            }

            if (refresh.Status is WallRefreshStatus.Done or WallRefreshStatus.Discarded)
            {
                return;
            }

            await using (var scope = await actorFactory.CreateAsync(refresh.CreatedByUserId, CancellationToken.None))
            {
                await RefreshCleanup.ReleaseAsync(refresh, scope.Actors, files, logger);
            }

            refresh.Status = WallRefreshStatus.Discarded;
            refresh.CompletedAt = DateTimeOffset.UtcNow;
            refresh.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }
    }

    private static bool IsFinished(WallRefreshStatus status) =>
        status is WallRefreshStatus.Done or WallRefreshStatus.Failed or WallRefreshStatus.Discarded;

    private static async Task<WallRefresh?> CurrentAsync(BlocwerkDbContext db, Guid wallId)
    {
        // Few rows per wall; ordered in memory because SQLite cannot ORDER BY a DateTimeOffset.
        var rows = await db.WallRefreshes.Where(r => r.WallId == wallId && r.Status != WallRefreshStatus.Discarded).ToListAsync();
        var latest = rows.MaxBy(r => r.CreatedAt);
        if (latest is null || (IsFinished(latest.Status) && latest.UpdatedAt < DateTimeOffset.UtcNow - ShownAfterFinish))
        {
            return null;
        }

        // An unstarted run whose draft the capture sweep removed (left for a day) has lost its photos.
        if (latest.Status is WallRefreshStatus.Uploading or WallRefreshStatus.ReadyToStart
            && !await db.WallCaptures.AnyAsync(c => c.Id == latest.CaptureId))
        {
            latest.Status = WallRefreshStatus.Discarded;
            latest.CompletedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
            return null;
        }

        return latest;
    }

    private async Task<WallRefreshView> OpenedMeanwhileAsync(Guid wallId)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync();
        var current = await CurrentAsync(db, wallId);
        return current is not null && !IsFinished(current.Status)
            ? await ToViewAsync(db, current)
            : throw new UserFacingException("Another update was opened at the same moment; reload the page.");
    }

    private async Task<(BlocwerkDbContext Db, Guid UserId)> OpenForAdminAsync(Guid wallId)
    {
        var user = await currentUserService.GetCurrentUserAsync();
        var db = await dbContextFactory.CreateDbContextAsync();
        try
        {
            db.CurrentUserId = user.Id;
            KioskGuard.EnsureNotKiosk(kioskContext, db, AdminAction);
            await WallAdminGuard.EnsureWallAdminAsync(db, wallId, user.Id, CancellationToken.None);
            return (db, user.Id);
        }
        catch
        {
            await db.DisposeAsync();
            throw;
        }
    }

    private async Task<(BlocwerkDbContext Db, WallRefresh Refresh)> OpenRefreshAsync(Guid refreshId)
    {
        Guid wallId;
        await using (var lookup = await dbContextFactory.CreateDbContextAsync())
        {
            wallId = await lookup.WallRefreshes.Where(r => r.Id == refreshId).Select(r => (Guid?)r.WallId).FirstOrDefaultAsync()
                     ?? throw new UserFacingException("This update no longer exists.");
        }

        var (db, _) = await OpenForAdminAsync(wallId);
        return (db, await db.WallRefreshes.FirstAsync(r => r.Id == refreshId));
    }
}
