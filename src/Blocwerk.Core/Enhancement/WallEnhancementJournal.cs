// <copyright file="WallEnhancementJournal.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Text.Json;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Enhancement;

/// <summary>A hold row an enhancement step's batch updated.</summary>
/// <param name="BatchId">The batch.</param>
/// <param name="HoldId">The hold.</param>
/// <param name="KeyJson">The journalled key (what a per-row revert matches on).</param>
public sealed record WallEnhancementHoldEntry(Guid BatchId, Guid HoldId, string KeyJson);

/// <summary>Reading the enhancement's own journal batches: which they are, and which holds they changed.</summary>
internal static class WallEnhancementJournal
{
    /// <summary>
    /// The wall-scoped batches a step wrote: its own label and those its service opens itself, created since the step
    /// first started, by the run's admin (or by nobody, for a save with no acting user), not reverted. Oldest first.
    /// </summary>
    public static async Task<List<Guid>> StepBatchesAsync(
        BlocwerkDbContext db, Guid wallId, Guid userId, IWallEnhancementStep step, DateTimeOffset since, CancellationToken ct)
    {
        var labels = step.OwnBatchLabels.Append(WallEnhancementKinds.BatchLabel(step.Key)).ToList();
        var actor = userId.ToString();
        var rows = await db.ChangeJournalBatches.AsNoTracking()
            .Where(b => b.ScopeKind == ChangeJournalScopeKind.Wall && b.ScopeId == wallId && labels.Contains(b.Label)
                        && b.Status == ChangeJournalStatus.Recorded)
            .Select(b => new { b.Id, b.CreatedAt, b.Actor })
            .ToListAsync(ct);

        // Compared in memory: SQLite cannot compare DateTimeOffset columns. A second of slack for clock rounding.
        var from = since.AddSeconds(-1);
        return rows.Where(b => b.CreatedAt >= from && (b.Actor is null || b.Actor == actor))
            .OrderBy(b => b.CreatedAt)
            .Select(b => b.Id)
            .ToList();
    }

    /// <summary>The hold rows the batches updated (one entry per batch and hold).</summary>
    public static async Task<List<WallEnhancementHoldEntry>> HoldUpdatesAsync(
        BlocwerkDbContext db, IReadOnlyCollection<Guid> batchIds, CancellationToken ct)
    {
        if (batchIds.Count == 0)
        {
            return [];
        }

        var ids = batchIds.ToList();
        var rows = await db.ChangeJournalEntries.AsNoTracking()
            .Where(e => ids.Contains(e.BatchId) && e.EntityType == nameof(Hold) && e.Op == ChangeJournalOp.Update)
            .Select(e => new { e.BatchId, e.KeyJson })
            .ToListAsync(ct);
        return rows
            .Select(r => HoldIdOf(r.KeyJson) is { } id ? new WallEnhancementHoldEntry(r.BatchId, id, r.KeyJson) : null)
            .OfType<WallEnhancementHoldEntry>()
            .Distinct()
            .ToList();
    }

    /// <summary>The hold id of a journalled key, or null for any other key shape.</summary>
    /// <param name="keyJson">The key.</param>
    /// <returns>The id.</returns>
    public static Guid? HoldIdOf(string keyJson)
    {
        try
        {
            using var key = JsonDocument.Parse(keyJson);
            return key.RootElement.ValueKind == JsonValueKind.Object
                   && key.RootElement.TryGetProperty(nameof(Hold.Id), out var id)
                   && id.TryGetGuid(out var guid)
                ? guid
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
