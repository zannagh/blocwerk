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
    IKioskContext? kioskContext = null,
    WallRefreshLocks? wallLocks = null) : IWallRefreshService
{
    /// <summary>Apply's refusal while the update is checked against this visit's new 3D model.</summary>
    public const string BeingChecked = "The update is being checked against the new 3D model. Try again in a moment.";

    private const string AdminAction = "Updating panels and 3D";
    private readonly WallRefreshLocks locks = wallLocks ?? new WallRefreshLocks();
    private static readonly TimeSpan ShownAfterFinish = TimeSpan.FromDays(1);

    private WallCapturePipelineOptions Options => pipelineOptions ?? new WallCapturePipelineOptions();

    public Task<WallRefreshView?> GetCurrentAsync(Guid wallId) => ReadCurrentAsync(wallId, sideEffects: true);

    public Task<WallRefreshView?> PeekCurrentAsync(Guid wallId) => ReadCurrentAsync(wallId, sideEffects: false);

    public async Task<RefreshRecheckOutcome> RecheckAsync(Guid refreshId)
    {
        var (db, refresh) = await OpenRefreshAsync(refreshId);
        await using (db)
        {
            if (WallRefreshProcessor.IsRechecking(refresh, DateTimeOffset.UtcNow))
            {
                return RefreshRecheckOutcome.Running;
            }

            return await Check3DPendingAsync(db, refresh, enqueue: true) ? RefreshRecheckOutcome.Queued : RefreshRecheckOutcome.NotDue;
        }
    }

    public async Task<Guid?> GetWallIdAsync(Guid refreshId)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync();
        return await db.WallRefreshes.Where(r => r.Id == refreshId).Select(r => (Guid?)r.WallId).FirstOrDefaultAsync();
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

    private async Task<WallRefreshView?> ReadCurrentAsync(Guid wallId, bool sideEffects)
    {
        var (db, _) = await OpenForAdminAsync(wallId);
        await using (db)
        {
            var refresh = await CurrentAsync(db, wallId, sideEffects);
            if (refresh is null)
            {
                return null;
            }

            var pending = await Check3DPendingAsync(db, refresh, enqueue: sideEffects);
            return await ToViewAsync(db, refresh) with { Check3DPending = pending };
        }
    }

    /// <summary>
    /// Whether the ready update waits for (or is in) its check against this visit's new 3D model; with
    /// <paramref name="enqueue"/>, starts that check.
    /// </summary>
    private async Task<bool> Check3DPendingAsync(BlocwerkDbContext db, WallRefresh refresh, bool enqueue)
    {
        var now = DateTimeOffset.UtcNow;
        if (WallRefreshProcessor.IsRechecking(refresh, now))
        {
            return true;
        }

        var ready = refresh.Status == WallRefreshStatus.ReadyToApply ? await WallRefreshProcessor.Ready3DModelAsync(db, refresh, CancellationToken.None) : null;
        if (!WallRefreshProcessor.NeedsRecheck(refresh, ready, now))
        {
            return false;
        }

        if (enqueue)
        {
            queue.Enqueue(refresh.Id);
        }

        return true;
    }

    private static bool IsFinished(WallRefreshStatus status) =>
        status is WallRefreshStatus.Done or WallRefreshStatus.Failed or WallRefreshStatus.Discarded;

    private static async Task<WallRefresh?> CurrentAsync(BlocwerkDbContext db, Guid wallId, bool sideEffects = true)
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
            if (!sideEffects)
            {
                // A read-only caller sees it as gone; the page's next read (or a write) records that.
                return null;
            }

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
