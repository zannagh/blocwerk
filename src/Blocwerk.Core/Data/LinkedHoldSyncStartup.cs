// <copyright file="LinkedHoldSyncStartup.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Data;

/// <summary>Logs a reconciliation summary the same way from the startup pass and the admin action.</summary>
public static class LinkedHoldSyncLog
{
    /// <summary>Writes the per-property counts and every settled conflict.</summary>
    /// <param name="logger">Logger.</param>
    /// <param name="wallId">The wall.</param>
    /// <param name="report">What changed.</param>
    /// <param name="origin">Who ran it.</param>
    public static void Write(ILogger logger, Guid wallId, HoldSyncReport report, string origin)
    {
        logger.LogInformation("Linked-hold {Origin} on wall {WallId}: {Summary}", origin, wallId, report.Summary());
        foreach (var c in report.Conflicts)
        {
            logger.LogWarning(
                "Linked-hold conflict on wall {WallId}: {Property} kept '{Kept}' (hold {Hold}) over {Replaced}",
                wallId, c.Property, c.Kept, c.KeptHoldId, string.Join(", ", c.Replaced.Select(r => $"'{r}'")));
        }
    }
}

/// <summary>
/// The one-time startup reconciliation of existing linked holds, so data linked before the sync existed converges
/// without a wall update. Per wall, guarded by <see cref="Wall.LinkedHoldSyncVersion"/> against
/// <see cref="LinkedHoldSync.Version"/>, so it runs once; one journal batch per wall that changed, so it reverts.
/// </summary>
public static class LinkedHoldSyncStartup
{
    /// <summary>The journal batch label of a startup pass.</summary>
    public const string BatchLabel = "linked-hold-sync-startup";

    /// <summary>Reconciles every wall that has not had this version yet.</summary>
    /// <param name="factory">Context factory (the journalling one in production).</param>
    /// <param name="journal">Opens the per-wall batch.</param>
    /// <param name="logger">Logger.</param>
    /// <returns>A task.</returns>
    public static async Task RunIfNeededAsync(IDbContextFactory<BlocwerkDbContext> factory, IChangeJournal journal, ILogger logger)
    {
        await using var list = await factory.CreateDbContextAsync();
        var wallIds = await list.Walls.IgnoreQueryFilters().AsNoTracking()
            .Where(w => w.LinkedHoldSyncVersion == null || w.LinkedHoldSyncVersion < LinkedHoldSync.Version)
            .Select(w => w.Id)
            .ToListAsync();

        foreach (var wallId in wallIds)
        {
            try
            {
                await ReconcileWallAsync(factory, journal, logger, wallId);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Linked-hold startup sync failed for wall {WallId}; it is retried on the next start.", wallId);
            }
        }
    }

    private static async Task ReconcileWallAsync(
        IDbContextFactory<BlocwerkDbContext> factory, IChangeJournal journal, ILogger logger, Guid wallId)
    {
        using var wallLock = await WallHoldWriteLock.AcquireAsync(wallId);
        await using var db = await factory.CreateDbContextAsync();
        var report = await LinkedHoldSync.ReconcileAsync(db, wallId);
        if (report.Any)
        {
            using var scope = journal.BeginBatch(BatchLabel, ChangeJournalScopeKind.Wall, wallId);
            await db.SaveChangesAsync();
        }

        LinkedHoldSyncLog.Write(logger, wallId, report, "startup sync");

        // Outside the journal: undoing the sync must not make the next start redo it.
        await db.Walls.IgnoreQueryFilters().Where(w => w.Id == wallId)
            .ExecuteUpdateAsync(s => s.SetProperty(w => w.LinkedHoldSyncVersion, LinkedHoldSync.Version));
    }
}
