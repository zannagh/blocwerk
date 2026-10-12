// <copyright file="WallLookOverService.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Capture;
using Blocwerk.Core.Data;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Refresh;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.LookOver;

/// <summary>
/// <see cref="IWallLookOverService"/>. The findings are written by the promote
/// (<c>WallBigUpdateService.RecordLookOverAsync</c>); this reads them, answers them (the Answers part) with the hold
/// operations that already exist (mark as changed, delete, merge), and finishes the look-over.
/// </summary>
public sealed partial class WallLookOverService(
    IDbContextFactory<BlocwerkDbContext> dbContextFactory,
    ICurrentUserService currentUserService,
    IWallService walls,
    IHoldDuplicateService duplicates,
    ICaptureFileStore files,
    ILogger<WallLookOverService> logger,
    IKioskContext? kioskContext = null) : IWallLookOverService
{
    private const string Action = "Looking over the wall";

    /// <inheritdoc/>
    public async Task<WallLookOverState?> GetStateAsync(Guid wallId, CancellationToken ct = default)
    {
        var user = await currentUserService.GetCurrentUserAsync();
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        db.CurrentUserId = user.Id;
        if (WallAdminGuard.IsForeignKioskWall(db, wallId))
        {
            return null;
        }

        // An installation admin sees every wall's state; everybody else the walls they are a member of.
        var appAdmin = await AppAdminGuard.IsAppAdminAsync(db, user.Id, ct);
        var visible = appAdmin ? db.Walls.IgnoreQueryFilters() : db.Walls;
        var wall = await visible.AsNoTracking()
            .Where(w => w.Id == wallId)
            .Select(w => new { w.LookOverPendingSince, w.LookOverGeneration, w.LookOverCompletedAt, w.LookOverCompletedByUserId })
            .FirstOrDefaultAsync(ct);
        if (wall is null)
        {
            return null;
        }

        var rows = await CurrentRoundAsync(db, wallId, wall.LookOverCompletedAt, ct);
        var completedBy = wall.LookOverCompletedByUserId is { } by
            ? await db.Users.IgnoreQueryFilters().Where(u => u.Id == by).Select(u => u.DisplayName).FirstOrDefaultAsync(ct)
            : null;
        var kiosk = kioskContext?.IsKiosk == true || db.KioskWallId is not null;
        var canLookOver = !kiosk && (appAdmin || await WallAdminGuard.IsWallModeratorOrAboveAsync(db, wallId, user.Id, ct));
        return new WallLookOverState(
            wall.LookOverPendingSince is not null,
            wall.LookOverPendingSince,
            wall.LookOverGeneration,
            rows.Count(f => f.Status == LookOverFindingStatus.Open),
            rows.Count,
            wall.LookOverCompletedAt,
            completedBy,
            canLookOver);
    }

    /// <inheritdoc/>
    public async Task FinishAsync(Guid wallId, bool leaveOpen, CancellationToken ct = default)
    {
        var (db, userId, _) = await OpenAsync(wallId, ct);
        await using (db)
        {
            var wall = await db.Walls.IgnoreQueryFilters().FirstOrDefaultAsync(w => w.Id == wallId, ct)
                ?? throw new UserFacingException("This wall no longer exists.");
            if (wall.LookOverPendingSince is null)
            {
                return;
            }

            var open = await db.WallLookOverFindings.CountAsync(f => f.WallId == wallId && f.Status == LookOverFindingStatus.Open, ct);
            if (open > 0 && !leaveOpen)
            {
                throw new UserFacingException($"{open} findings are still open. Answer them, or finish anyway.");
            }

            wall.LookOverPendingSince = null;
            wall.LookOverCompletedAt = DateTimeOffset.UtcNow;
            wall.LookOverCompletedByUserId = userId;
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Look-over of wall {WallId} finished by {UserId} with {Open} findings left open", wallId, userId, open);
        }
    }

    /// <inheritdoc/>
    public async Task<byte[]?> GetCropAsync(Guid wallId, Guid findingId, CheckCropView view, CancellationToken ct = default)
    {
        var (db, _, _) = await OpenAsync(wallId, ct);
        await db.DisposeAsync();
        return await LookOverCrops.GetAsync(dbContextFactory, files, wallId, findingId, view, ct);
    }

    /// <summary>
    /// The findings of the current round: everything open, and what was answered since the last finished look-over.
    /// Filtered in memory (a wall has a few hundred at most, and SQLite cannot compare a DateTimeOffset).
    /// </summary>
    private static async Task<List<Entities.WallLookOverFinding>> CurrentRoundAsync(
        BlocwerkDbContext db, Guid wallId, DateTimeOffset? completedAt, CancellationToken ct)
    {
        var rows = await db.WallLookOverFindings.AsNoTracking()
            .Where(f => f.WallId == wallId && f.Status != LookOverFindingStatus.Superseded)
            .ToListAsync(ct);
        return rows
            .Where(f => f.Status == LookOverFindingStatus.Open || completedAt is not { } done || f.AnsweredAt > done)
            .ToList();
    }

    /// <summary>A context for the wall's owner, an admin or a moderator of it, or an installation admin; never a kiosk tablet.</summary>
    private async Task<(BlocwerkDbContext Db, Guid UserId, bool IsWallAdmin)> OpenAsync(Guid wallId, CancellationToken ct)
    {
        var user = await currentUserService.GetCurrentUserAsync();
        var db = await dbContextFactory.CreateDbContextAsync(ct);
        try
        {
            db.CurrentUserId = user.Id;
            KioskGuard.EnsureNotKiosk(kioskContext, db, Action);
            var wallAdmin = await WallAdminGuard.IsWallAdminAsync(db, wallId, user.Id, ct);
            if (!wallAdmin
                && !await WallAdminGuard.IsWallModeratorOrAboveAsync(db, wallId, user.Id, ct)
                && !await AppAdminGuard.IsAppAdminAsync(db, user.Id, ct))
            {
                throw new UnauthorizedAccessException($"User {user.Id} may not look over wall {wallId}.");
            }

            return (db, user.Id, wallAdmin);
        }
        catch
        {
            await db.DisposeAsync();
            throw;
        }
    }
}
