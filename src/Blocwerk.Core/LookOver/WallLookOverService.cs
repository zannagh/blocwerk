// <copyright file="WallLookOverService.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Capture;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
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
    IKioskContext? kioskContext = null,
    IChangeJournal? journal = null) : IWallLookOverService
{
    /// <summary>The journal batch label of an answer, an undo or a bulk answer.</summary>
    public const string BatchLabel = "look-over-answer";

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

        // Counted, not loaded: the wall page and every boulder page ask this.
        var round = CurrentRound(db, wallId, wall.LookOverPendingSince);
        var open = await round.CountAsync(f => f.Status == LookOverFindingStatus.Open, ct);
        var total = await round.CountAsync(ct);
        var completedBy = wall.LookOverCompletedByUserId is { } by
            ? await db.Users.IgnoreQueryFilters().Where(u => u.Id == by).Select(u => u.DisplayName).FirstOrDefaultAsync(ct)
            : null;
        var kiosk = KioskGuard.ScopedWallId(kioskContext, db) is not null;
        var canLookOver = !kiosk && (appAdmin || await WallAdminGuard.IsWallModeratorOrAboveAsync(db, wallId, user.Id, ct));
        // The boulder counts are for the notice of whoever may look over the wall; nobody else is shown them.
        var pending = wall.LookOverPendingSince is not null;
        var (marked, more) = pending && canLookOver ? await BoulderCountsAsync(db, wallId, ct) : (0, 0);
        return new WallLookOverState(
            pending, wall.LookOverPendingSince, wall.LookOverGeneration, open, total, wall.LookOverCompletedAt, completedBy, canLookOver, marked, more);
    }

    /// <inheritdoc/>
    public async Task FinishAsync(Guid wallId, bool leaveOpen, CancellationToken ct = default)
    {
        var (db, access) = await OpenAsync(wallId, ct);
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
            wall.LookOverCompletedByUserId = access.UserId;
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Look-over of wall {WallId} finished by {UserId} with {Open} findings left open", wallId, access.UserId, open);
        }
    }

    /// <inheritdoc/>
    public async Task<bool> HasFindingAsync(Guid wallId, Guid findingId, CancellationToken ct = default)
    {
        var (db, _) = await OpenAsync(wallId, ct);
        await using (db)
        {
            return await db.WallLookOverFindings.AnyAsync(f => f.Id == findingId && f.WallId == wallId, ct);
        }
    }

    /// <inheritdoc/>
    public async Task<byte[]?> GetCropAsync(Guid wallId, Guid findingId, CheckCropView view, CancellationToken ct = default)
    {
        var (db, _) = await OpenAsync(wallId, ct);
        await db.DisposeAsync();
        return await LookOverCrops.GetAsync(dbContextFactory, files, wallId, findingId, view, ct);
    }

    /// <summary>
    /// The findings of the current round: everything open, and what was answered since the wall was flagged. A round
    /// starts with the first update that leaves a finding and ends with Finish, so what an earlier round settled never
    /// comes back with the next update; while the wall is not flagged only what was left open is listed.
    /// </summary>
    private static IQueryable<WallLookOverFinding> CurrentRound(BlocwerkDbContext db, Guid wallId, DateTimeOffset? pendingSince)
    {
        var ofWall = db.WallLookOverFindings.AsNoTracking().Where(f => f.WallId == wallId);
        return pendingSince is { } since
            ? ofWall.Where(f => f.Status == LookOverFindingStatus.Open || (f.Status == LookOverFindingStatus.Settled && f.AnsweredAt >= since))
            : ofWall.Where(f => f.Status == LookOverFindingStatus.Open);
    }

    /// <summary>
    /// What the wall page says next to the findings: the current boulders that are marked for review, and those that
    /// are not but use a hold an open finding is about (an unsure match marks the hold, not its boulders).
    /// </summary>
    private static async Task<(int Marked, int More)> BoulderCountsAsync(BlocwerkDbContext db, Guid wallId, CancellationToken ct)
    {
        var current = db.Boulders.Where(b => b.WallId == wallId && !b.IsArchived && !b.IsHistoric);
        var marked = await current.CountAsync(b => b.NeedsReview, ct);
        var more = await current.CountAsync(
            b => !b.NeedsReview && db.BoulderHolds.Any(bh => bh.BoulderId == b.Id
                && db.WallLookOverFindings.Any(f => f.WallId == wallId && f.Status == LookOverFindingStatus.Open && f.HoldId == bh.HoldId)),
            ct);
        return (marked, more);
    }

    private IDisposable? BeginBatch(Guid wallId) => journal?.BeginBatch(BatchLabel, ChangeJournalScopeKind.Wall, wallId);

    /// <summary>A context for the wall's owner, an admin or a moderator of it, or an installation admin; never a kiosk tablet.</summary>
    private async Task<(BlocwerkDbContext Db, LookOverAccess Access)> OpenAsync(Guid wallId, CancellationToken ct)
    {
        var user = await currentUserService.GetCurrentUserAsync();
        var db = await dbContextFactory.CreateDbContextAsync(ct);
        try
        {
            db.CurrentUserId = user.Id;
            KioskGuard.EnsureNotKiosk(kioskContext, db, Action);
            var canMerge = await WallAdminGuard.IsWallAdminAsync(db, wallId, user.Id, ct);
            var canEdit = canMerge || await WallAdminGuard.IsWallModeratorOrAboveAsync(db, wallId, user.Id, ct);
            if (!canEdit && !await AppAdminGuard.IsAppAdminAsync(db, user.Id, ct))
            {
                throw new UnauthorizedAccessException($"User {user.Id} may not look over wall {wallId}.");
            }

            return (db, new LookOverAccess(user.Id, canEdit, canMerge));
        }
        catch
        {
            await db.DisposeAsync();
            throw;
        }
    }
}
