// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Text.Json;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Services;

/// <summary>
/// Which journal batches belong to a wall, and to which wall update: the selection behind the admin listing and the
/// whole-update export. A batch belongs to a wall update when it was created while that update's <c>wall-update</c>
/// batch was open (from its creation to its seal): the nested batches a promote or a touch-up spawned, and the adhoc
/// ones written in between.
/// </summary>
internal static class ChangeJournalBatchSelection
{
    /// <summary>The unscoped-batch search reads entry text; without a bound it would scan the whole journal.</summary>
    public static readonly TimeSpan DefaultUnscopedWindow = TimeSpan.FromDays(7);

    /// <summary>The wall's batches created in [<paramref name="since"/>, <paramref name="until"/>], oldest first.</summary>
    public static async Task<List<ChangeJournalBatch>> ForWallAsync(
        BlocwerkDbContext db, Guid wallId, bool includeUnscoped, DateTimeOffset? since, DateTimeOffset? until, CancellationToken ct)
    {
        var ids = await db.ChangeJournalBatches.AsNoTracking()
            .Where(b => b.ScopeKind == ChangeJournalScopeKind.Wall && b.ScopeId == wallId)
            .Where(b => since == null || b.CreatedAt >= since)
            .Where(b => until == null || b.CreatedAt <= until)
            .Select(b => b.Id)
            .ToListAsync(ct);
        if (includeUnscoped)
        {
            ids.AddRange(await UnscopedBatchIdsAsync(db, wallId, since ?? DateTimeOffset.UtcNow - DefaultUnscopedWindow, until, ct));
        }

        var batches = await db.ChangeJournalBatches.AsNoTracking().Where(b => ids.Contains(b.Id)).ToListAsync(ct);
        return batches.OrderBy(b => b.CreatedAt).ToList();
    }

    /// <summary>Every wall-update batch of the wall, oldest first (few: one per update).</summary>
    public static async Task<List<ChangeJournalBatch>> WallUpdatesAsync(BlocwerkDbContext db, Guid wallId, CancellationToken ct)
    {
        var updates = await db.ChangeJournalBatches.AsNoTracking()
            .Where(b => b.ScopeKind == ChangeJournalScopeKind.Wall && b.ScopeId == wallId && b.Label == ChangeJournal.WallUpdateBatchLabel)
            .ToListAsync(ct);
        return updates.OrderBy(b => b.CreatedAt).ToList();
    }

    /// <summary>The wall update <paramref name="batch"/> belongs to: itself, or the one open when it was created.</summary>
    public static Guid? UpdateOf(ChangeJournalBatch batch, IReadOnlyList<ChangeJournalBatch> updates)
    {
        if (batch.Label == ChangeJournal.WallUpdateBatchLabel)
        {
            return batch.Id;
        }

        return updates.LastOrDefault(u => u.CreatedAt <= batch.CreatedAt && (u.SealedAt is null || batch.CreatedAt <= u.SealedAt))?.Id;
    }

    /// <summary>
    /// False for what a replay must not carry: a batch reverted here, a revert, or an earlier replay (its rows came from
    /// another instance's package in the first place).
    /// </summary>
    public static bool IsReplayable(ChangeJournalBatch batch) =>
        batch.Status != ChangeJournalStatus.Reverted
        && batch.Label != "replay"
        && !batch.Label.StartsWith("revert:", StringComparison.Ordinal);

    /// <summary>
    /// Unscoped batches in the window that touched the wall: an entry names the wall's id in its images (an insert or
    /// delete carries <c>WallId</c>), or its key is one of the wall's holds, panels or boulders (an update carries only the
    /// changed columns). Rows deleted since are only found through the first test.
    /// </summary>
    private static async Task<List<Guid>> UnscopedBatchIdsAsync(
        BlocwerkDbContext db, Guid wallId, DateTimeOffset since, DateTimeOffset? until, CancellationToken ct)
    {
        // Guids are journalled in their default ("D", lower-case) JSON form.
        var needle = wallId.ToString();
        var window = db.ChangeJournalBatches.AsNoTracking()
            .Where(b => b.ScopeId == null && b.CreatedAt >= since && (until == null || b.CreatedAt <= until))
            .Select(b => b.Id);
        var entries = await db.ChangeJournalEntries.AsNoTracking()
            .Where(e => window.Contains(e.BatchId))
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
