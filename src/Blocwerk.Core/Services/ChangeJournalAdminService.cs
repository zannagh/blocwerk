// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Text.Json;
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
    /// <summary>The unscoped-batch search reads entry text; without a bound it would scan the whole journal.</summary>
    public static readonly TimeSpan DefaultUnscopedWindow = TimeSpan.FromDays(7);

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
        db.CurrentUserId = Guid.Empty;

        var ids = await db.ChangeJournalBatches.AsNoTracking()
            .Where(b => b.ScopeKind == ChangeJournalScopeKind.Wall && b.ScopeId == wallId)
            .Where(b => since == null || b.CreatedAt >= since)
            .Select(b => b.Id)
            .ToListAsync(ct);
        if (includeUnscoped)
        {
            ids.AddRange(await UnscopedBatchIdsAsync(db, wallId, since ?? DateTimeOffset.UtcNow - DefaultUnscopedWindow, ct));
        }

        var counts = await db.ChangeJournalEntries.AsNoTracking()
            .Where(e => ids.Contains(e.BatchId))
            .GroupBy(e => e.BatchId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        var batches = await db.ChangeJournalBatches.AsNoTracking().Where(b => ids.Contains(b.Id)).ToListAsync(ct);
        return batches
            .OrderBy(b => b.CreatedAt)
            .Select(b => new ChangeJournalBatchSummary(
                b.Id, b.Label, b.ScopeKind, b.ScopeId, b.Status, b.Actor, b.CreatedAt, b.SealedAt, counts.GetValueOrDefault(b.Id)))
            .ToList();
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

    /// <summary>
    /// Unscoped batches since <paramref name="since"/> that touched the wall: an entry names the wall's id in its
    /// images (an insert or delete carries <c>WallId</c>), or its key is one of the wall's holds, panels or boulders (an
    /// update carries only the changed columns). Rows deleted since are only found through the first test.
    /// </summary>
    private static async Task<List<Guid>> UnscopedBatchIdsAsync(
        BlocwerkDbContext db, Guid wallId, DateTimeOffset since, CancellationToken ct)
    {
        // Guids are journalled in their default ("D", lower-case) JSON form.
        var needle = wallId.ToString();
        var recent = db.ChangeJournalBatches.AsNoTracking()
            .Where(b => b.ScopeId == null && b.CreatedAt >= since)
            .Select(b => b.Id);
        var entries = await db.ChangeJournalEntries.AsNoTracking()
            .Where(e => recent.Contains(e.BatchId))
            .Select(e => new
            {
                e.BatchId,
                e.KeyJson,
                Mentions = (e.AfterJson != null && e.AfterJson.Contains(needle)) || (e.BeforeJson != null && e.BeforeJson.Contains(needle)),
            })
            .ToListAsync(ct);
        if (entries.Count == 0)
        {
            return [];
        }

        var owned = await WallRowIdsAsync(db, wallId, ct);
        return entries
            .Where(e => e.Mentions || KeyGuids(e.KeyJson).Any(id => id == wallId || owned.Contains(id)))
            .Select(e => e.BatchId)
            .Distinct()
            .ToList();
    }

    private static async Task<HashSet<Guid>> WallRowIdsAsync(BlocwerkDbContext db, Guid wallId, CancellationToken ct)
    {
        var holds = await db.Holds.AsNoTracking().Where(h => h.WallId == wallId).Select(h => h.Id).ToListAsync(ct);
        var panels = await db.WallPanels.AsNoTracking().Where(p => p.WallId == wallId).Select(p => p.Id).ToListAsync(ct);
        var boulders = await db.Boulders.AsNoTracking().Where(b => b.WallId == wallId).Select(b => b.Id).ToListAsync(ct);
        return holds.Concat(panels).Concat(boulders).ToHashSet();
    }

    /// <summary>Every Guid value in a journalled key (<c>{"Id":"…"}</c>, or the parts of a composite key).</summary>
    private static IEnumerable<Guid> KeyGuids(string keyJson)
    {
        using var doc = JsonDocument.Parse(keyJson);
        var ids = new List<Guid>();
        foreach (var part in doc.RootElement.EnumerateObject())
        {
            if (part.Value.ValueKind == JsonValueKind.String && part.Value.TryGetGuid(out var id))
            {
                ids.Add(id);
            }
        }

        return ids;
    }
}
