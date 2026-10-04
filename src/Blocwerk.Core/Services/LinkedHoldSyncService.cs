// <copyright file="LinkedHoldSyncService.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Services;

/// <summary><see cref="ILinkedHoldSyncService"/>: the Wall Settings action and the winner-panel setting.</summary>
public sealed class LinkedHoldSyncService : ILinkedHoldSyncService
{
    /// <summary>The journal batch label of an admin sync.</summary>
    public const string BatchLabel = "linked-hold-sync";

    private readonly IDbContextFactory<BlocwerkDbContext> dbContextFactory;
    private readonly ICurrentUserService currentUserService;
    private readonly IChangeJournal journal;
    private readonly ChangeJournalReverter reverter;
    private readonly ILogger<LinkedHoldSyncService> logger;
    private readonly IKioskContext? kioskContext;

    /// <summary>Initializes a new instance of the <see cref="LinkedHoldSyncService"/> class.</summary>
    /// <param name="dbContextFactory">Context factory.</param>
    /// <param name="currentUserService">The acting user.</param>
    /// <param name="journal">Opens the named journal batch.</param>
    /// <param name="reverter">Reverts a batch.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="kioskContext">The kiosk context, when the host has one.</param>
    public LinkedHoldSyncService(
        IDbContextFactory<BlocwerkDbContext> dbContextFactory,
        ICurrentUserService currentUserService,
        IChangeJournal journal,
        ChangeJournalReverter reverter,
        ILogger<LinkedHoldSyncService> logger,
        IKioskContext? kioskContext = null)
    {
        this.dbContextFactory = dbContextFactory;
        this.currentUserService = currentUserService;
        this.journal = journal;
        this.reverter = reverter;
        this.logger = logger;
        this.kioskContext = kioskContext;
    }

    /// <inheritdoc/>
    public async Task<LinkedHoldSyncStatus> GetStatusAsync(Guid wallId, CancellationToken ct = default)
    {
        var (db, _) = await OpenForAdminAsync(wallId, ct);
        await using (db)
        {
            var wall = await db.Walls.IgnoreQueryFilters().AsNoTracking().FirstAsync(w => w.Id == wallId, ct);
            var live = (await db.WallPanels.AsNoTracking()
                    .Where(p => p.WallId == wallId && p.Photo != null)
                    .Select(p => new { p.Col, p.Row }).ToListAsync(ct))
                .Select(p => (p.Col, p.Row)).Distinct().OrderBy(p => p.Row).ThenBy(p => p.Col).ToList();
            var options = live
                .Select((p, i) => new LinkedHoldPanelOption(p.Col, p.Row, $"Panel {i + 1} (column {p.Col}, row {p.Row})"))
                .ToList();
            var linkCount = await db.HoldLinks.AsNoTracking()
                .CountAsync(l => l.WallId == wallId && l.HoldA.Generation == wall.CurrentGeneration, ct);
            var batches = await db.ChangeJournalBatches.AsNoTracking()
                .Where(b => b.Label == BatchLabel && b.ScopeId == wallId && b.Status == ChangeJournalStatus.Recorded)
                .ToListAsync(ct);
            return new LinkedHoldSyncStatus(
                options,
                LinkedHoldSync.ResolveWinnerCell(wall.WinnerCell(), live),
                linkCount,
                batches.MaxBy(b => b.CreatedAt)?.Id);
        }
    }

    /// <inheritdoc/>
    public async Task SetWinnerPanelAsync(Guid wallId, (int Col, int Row)? cell, CancellationToken ct = default)
    {
        var (db, userId) = await OpenForAdminAsync(wallId, ct);
        await using (db)
        {
            if (cell is { } c && !await db.WallPanels.AnyAsync(p => p.WallId == wallId && p.Col == c.Col && p.Row == c.Row, ct))
            {
                throw new UserFacingException("That panel does not belong to this wall.");
            }

            var wall = await db.Walls.IgnoreQueryFilters().FirstAsync(w => w.Id == wallId, ct);
            wall.LinkedHoldWinnerCol = cell?.Col;
            wall.LinkedHoldWinnerRow = cell?.Row;
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Wall {WallId}: panel that wins for linked holds set to {Cell} by {UserId}", wallId, cell, userId);
        }
    }

    /// <inheritdoc/>
    public async Task<LinkedHoldSyncResult> SyncAsync(Guid wallId, CancellationToken ct = default)
    {
        var (db, userId) = await OpenForAdminAsync(wallId, ct);
        await using (db)
        {
            using var wallLock = WallHoldWriteLock.TryAcquire(
                wallId, "Another update of this wall's holds is running. Try again in a moment.");
            var report = await LinkedHoldSync.ReconcileAsync(db, wallId, ct: ct);
            Guid? batchId = null;
            if (report.Any)
            {
                using var scope = journal.BeginBatch(BatchLabel, ChangeJournalScopeKind.Wall, wallId);
                batchId = ((ChangeJournalBatchScope)scope).BatchId;
                await db.SaveChangesAsync(ct);
            }

            LinkedHoldSyncLog.Write(logger, wallId, report, $"sync by {userId}");
            return new LinkedHoldSyncResult(report, batchId);
        }
    }

    /// <inheritdoc/>
    public async Task<ChangeJournalRevertResult> RevertAsync(Guid wallId, Guid batchId, CancellationToken ct = default)
    {
        var (db, _) = await OpenForAdminAsync(wallId, ct);
        await using (db)
        {
            var known = await db.ChangeJournalBatches.AsNoTracking()
                .AnyAsync(b => b.Id == batchId && b.Label == BatchLabel && b.ScopeId == wallId, ct);
            if (!known)
            {
                throw new UserFacingException("That sync was not found on this wall.");
            }
        }

        using var wallLock = WallHoldWriteLock.TryAcquire(
            wallId, "Another update of this wall's holds is running. Try again in a moment.");
        return await reverter.RevertBatchAsync(batchId, force: false, ct);
    }

    private async Task<(BlocwerkDbContext Db, Guid UserId)> OpenForAdminAsync(Guid wallId, CancellationToken ct)
    {
        var user = await currentUserService.GetCurrentUserAsync();
        var db = await dbContextFactory.CreateDbContextAsync(ct);
        try
        {
            db.CurrentUserId = user.Id;
            KioskGuard.EnsureNotKiosk(kioskContext, db, "Syncing linked holds");
            await WallAdminGuard.EnsureWallAdminAsync(db, wallId, user.Id, ct);
            return (db, user.Id);
        }
        catch
        {
            await db.DisposeAsync();
            throw;
        }
    }
}
