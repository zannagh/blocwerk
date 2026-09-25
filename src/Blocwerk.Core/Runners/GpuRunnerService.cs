// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Runners;

/// <summary>
/// <see cref="IGpuRunnerService"/>. Creating a runner and listing a wall's runners is a wall-admin
/// action; changing a runner is its owner's (or a site admin's). Everything is refused from a kiosk
/// tablet, like minting API keys.
/// </summary>
public sealed partial class GpuRunnerService(
    IDbContextFactory<BlocwerkDbContext> dbContextFactory,
    ICurrentUserService currentUserService,
    GpuJobQueue queue,
    ILogger<GpuRunnerService> logger,
    IKioskContext? kioskContext = null) : IGpuRunnerService
{
    private const string AdminAction = "Managing 3D runners";

    public bool Enabled => queue.Options.Mode != GpuRunnerMode.Off;

    public async Task<GpuRunnerCreated> CreateAsync(Guid wallId, string name)
    {
        if (!Enabled)
        {
            throw new UserFacingException("3D runners are turned off on this server.");
        }

        var (db, userId) = await OpenForWallAdminAsync(wallId);
        await using (db)
        {
            var max = queue.Options.MaxRunnersPerUser;
            if (await db.GpuRunners.CountAsync(r => r.OwnerUserId == userId && r.RevokedAt == null) >= max)
            {
                throw new UserFacingException($"You already have {max} 3D runners; revoke one before creating another.");
            }

            var clean = GpuJobQueue.Clip(name, 100) ?? "3D runner";
            var (token, prefix) = GpuRunnerTokens.Create();
            var runner = new GpuRunner { Name = clean, OwnerUserId = userId, KeyHash = GpuRunnerTokens.Hash(token), KeyPrefix = prefix };
            db.GpuRunners.Add(runner);

            // Serves this wall and every wall the creator owns (they can narrow it down afterwards).
            var owned = await db.Walls.IgnoreQueryFilters().Where(w => w.OwnerId == userId).Select(w => w.Id).ToListAsync();
            foreach (var id in owned.Append(wallId).Distinct())
            {
                db.GpuRunnerWalls.Add(new GpuRunnerWall { RunnerId = runner.Id, WallId = id });
            }

            await db.SaveChangesAsync();
            logger.LogInformation(
                "3D runner {RunnerId} ({Name}, key {Prefix}…) created by {UserId} from wall {WallId}", runner.Id, clean, prefix, userId, wallId);
            var info = (await ProjectAsync(db, db.GpuRunners.Where(r => r.Id == runner.Id), userId, wallId)).Single();
            return new GpuRunnerCreated(info, token);
        }
    }

    public async Task RevokeAsync(Guid runnerId)
    {
        var (db, userId, runner) = await OpenRunnerAsync(runnerId, allowAppAdmin: true);
        await using (db)
        {
            runner.RevokedAt ??= DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
            logger.LogInformation("3D runner {RunnerId} ({Name}) revoked by {UserId}", runner.Id, runner.Name, userId);
        }

        await queue.ReleaseRunnerAsync(runnerId, CancellationToken.None);
    }

    public async Task<IReadOnlyList<GpuRunnerWallChoice>> GetWallChoicesAsync(Guid runnerId)
    {
        var (db, userId, runner) = await OpenRunnerAsync(runnerId, allowAppAdmin: false);
        await using (db)
        {
            var served = await db.GpuRunnerWalls.Where(rw => rw.RunnerId == runner.Id).Select(rw => rw.WallId).ToListAsync();
            var walls = await db.Walls.IgnoreQueryFilters()
                .Where(w => w.OwnerId == userId || w.Members.Any(m => m.UserId == userId && m.Role == WallRole.Admin))
                .OrderBy(w => w.Name).Select(w => new { w.Id, w.Name }).ToListAsync();
            return walls.Select(w => new GpuRunnerWallChoice(w.Id, w.Name, served.Contains(w.Id))).ToList();
        }
    }

    public async Task SetServesWallAsync(Guid runnerId, Guid wallId, bool serves)
    {
        var (db, userId, runner) = await OpenRunnerAsync(runnerId, allowAppAdmin: false);
        await using (db)
        {
            await WallAdminGuard.EnsureWallAdminAsync(db, wallId, userId, CancellationToken.None);
            var row = await db.GpuRunnerWalls.FirstOrDefaultAsync(rw => rw.RunnerId == runner.Id && rw.WallId == wallId);
            if (serves && row is null)
            {
                db.GpuRunnerWalls.Add(new GpuRunnerWall { RunnerId = runner.Id, WallId = wallId });
            }
            else if (!serves && row is not null)
            {
                db.GpuRunnerWalls.Remove(row);
            }

            await db.SaveChangesAsync();
            logger.LogInformation("3D runner {RunnerId} serves wall {WallId}: {Serves} (by {UserId})", runner.Id, wallId, serves, userId);
        }
    }

    public async Task<bool> CancelCaptureJobAsync(Guid captureId)
    {
        Guid wallId;
        await using (var lookup = await dbContextFactory.CreateDbContextAsync())
        {
            wallId = await lookup.WallCaptures.Where(c => c.Id == captureId).Select(c => (Guid?)c.WallId).FirstOrDefaultAsync()
                     ?? throw new UserFacingException("Capture not found");
        }

        var (db, userId) = await OpenForWallAdminAsync(wallId);
        await using (db)
        {
            logger.LogInformation("GPU job of capture {CaptureId} cancelled by {UserId}", captureId, userId);
        }

        return await queue.CancelForCaptureAsync(captureId, "cancelled by an admin", CancellationToken.None);
    }

    private async Task<(BlocwerkDbContext Db, Guid UserId)> OpenForWallAdminAsync(Guid wallId)
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

    /// <summary>A context and the runner, after the owner (or, when allowed, site-admin) check.</summary>
    private async Task<(BlocwerkDbContext Db, Guid UserId, GpuRunner Runner)> OpenRunnerAsync(Guid runnerId, bool allowAppAdmin)
    {
        var user = await currentUserService.GetCurrentUserAsync();
        var db = await dbContextFactory.CreateDbContextAsync();
        try
        {
            db.CurrentUserId = user.Id;
            KioskGuard.EnsureNotKiosk(kioskContext, db, AdminAction);
            var runner = await db.GpuRunners.FirstOrDefaultAsync(r => r.Id == runnerId && r.RevokedAt == null)
                         ?? throw new UserFacingException("Runner not found");
            if (runner.OwnerUserId != user.Id
                && !(allowAppAdmin && await AppAdminGuard.IsAppAdminAsync(db, user.Id, CancellationToken.None)))
            {
                throw new UnauthorizedAccessException("Only the runner's owner can change it.");
            }

            return (db, user.Id, runner);
        }
        catch
        {
            await db.DisposeAsync();
            throw;
        }
    }
}
