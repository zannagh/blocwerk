// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Data;
using Blocwerk.Core.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Services;

/// <summary>
/// <see cref="IChangeJournalAdminService"/>. The admin check is decided against the database like the administration
/// area (<see cref="AppAdminGuard"/>), on the root context because an administrator need not be a member of the wall;
/// the work itself is the exporter's, replayer's and reverter's, unchanged from the Development harness.
/// </summary>
public sealed class ChangeJournalAdminService(
    RootDbContextFactory rootFactory,
    ICurrentUserService currentUser,
    ChangeJournalExporter exporter,
    ChangeJournalReplayer replayer,
    ChangeJournalReverter reverter,
    ILogger<ChangeJournalAdminService> logger) : IChangeJournalAdminService
{
    public async Task<Guid> EnsureAdminAsync(CancellationToken ct)
    {
        var user = await currentUser.GetCurrentUserAsync()
                   ?? throw new UnauthorizedAccessException("Sign in first.");
        await using var db = rootFactory.CreateDbContext();
        await AppAdminGuard.EnsureAppAdminAsync(db, user.Id, ct);
        return user.Id;
    }

    public async Task<IReadOnlyList<ChangeJournalBatchSummary>> ListBatchesAsync(
        Guid wallId, bool includeUnscoped, DateTimeOffset? since, CancellationToken ct)
    {
        await EnsureAdminAsync(ct);
        await using var db = rootFactory.CreateDbContext();
        var batches = await ChangeJournalBatchSelection.ForWallAsync(db, wallId, includeUnscoped, since, null, ct);
        var updates = await ChangeJournalBatchSelection.WallUpdatesAsync(db, wallId, ct);
        var ids = batches.Select(b => b.Id).ToList();
        var counts = await db.ChangeJournalEntries.AsNoTracking()
            .Where(e => ids.Contains(e.BatchId))
            .GroupBy(e => e.BatchId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        return batches
            .Select(b => new ChangeJournalBatchSummary(
                b.Id, b.Label, b.ScopeKind, b.ScopeId, b.Status, b.Actor, b.CreatedAt, b.SealedAt, counts.GetValueOrDefault(b.Id),
                ChangeJournalBatchSelection.UpdateOf(b, updates)))
            .ToList();
    }

    public async Task<ChangeJournalPackage?> ExportWallUpdateAsync(Guid updateBatchId, CancellationToken ct)
    {
        await EnsureAdminAsync(ct);
        await using var db = rootFactory.CreateDbContext();
        var update = await db.ChangeJournalBatches.AsNoTracking().FirstOrDefaultAsync(
            b => b.Id == updateBatchId && b.Label == ChangeJournal.WallUpdateBatchLabel && b.ScopeId != null, ct);
        if (update is null)
        {
            return null;
        }

        // Everything created while the update was open, the adhoc batches included.
        return await ExportWallAsync(update.ScopeId!.Value, update.CreatedAt, update.SealedAt, includeUnscoped: true, ct);
    }

    public async Task<ChangeJournalPackage> ExportWallAsync(
        Guid wallId, DateTimeOffset? since, DateTimeOffset? until, bool includeUnscoped, CancellationToken ct)
    {
        await EnsureAdminAsync(ct);
        List<Guid> ids;
        await using (var db = rootFactory.CreateDbContext())
        {
            var batches = await ChangeJournalBatchSelection.ForWallAsync(db, wallId, includeUnscoped, since, until, ct);
            ids = batches.Where(ChangeJournalBatchSelection.IsReplayable).Select(b => b.Id).ToList();
        }

        return await ExportAsync(ids, ct);
    }

    public async Task<ChangeJournalPackage> ExportAsync(IReadOnlyCollection<Guid> batchIds, CancellationToken ct)
    {
        var userId = await EnsureAdminAsync(ct);
        var package = await exporter.ExportBatchesAsync(batchIds, ct);
        logger.LogInformation(
            "Journal export of {BatchCount} batch(es), {EntryCount} entries, by {UserId}",
            package.Batches.Count, package.Entries.Count, userId);
        return package;
    }

    public async Task<ChangeJournalReplayReport> ReplayAsync(
        ChangeJournalPackage package, bool dryRun, bool force, CancellationToken ct)
    {
        var userId = await EnsureAdminAsync(ct);

        // Schema parity is not something a force may skip: the recorded images name columns of THAT schema.
        await using (var db = rootFactory.CreateDbContext())
        {
            var target = await ChangeJournalMarkers.MigrationIdAsync(db, ct);
            if (package.MigrationId != target)
            {
                return new ChangeJournalReplayReport(
                    false, dryRun, [], [$"Schema parity mismatch: package migration '{package.MigrationId}' != target '{target}'."], null);
            }
        }

        var report = await replayer.ImportReplayAsync(package, dryRun, force, ct);
        logger.LogInformation(
            "Journal replay of {BatchCount} batch(es) by {UserId}: dryRun={DryRun}, force={Force}, applied={Applied}, conflicts={Conflicts}, fatal={Fatal}, replayBatch={ReplayBatchId}",
            package.Batches.Count, userId, dryRun, force, report.Applied,
            report.Rows.Count(r => r.Outcome == ChangeJournalReplayOutcome.Conflict), report.FatalErrors.Count, report.ReplayBatchId);
        return report;
    }

    public async Task<ChangeJournalRevertResult> RevertAsync(Guid batchId, bool dryRun, bool force, CancellationToken ct)
    {
        var userId = await EnsureAdminAsync(ct);
        var result = dryRun
            ? await reverter.DryRunRevertAsync(batchId, ct)
            : await reverter.RevertBatchAsync(batchId, force, ct);
        logger.LogInformation(
            "Journal revert of batch {BatchId} by {UserId}: dryRun={DryRun}, force={Force}, reverted={Reverted}, conflicts={Conflicts}, revertBatch={RevertBatchId}",
            batchId, userId, dryRun, force, result.Reverted, result.Conflicts.Count, result.RevertBatchId);
        return result;
    }
}
