// <copyright file="DiscardedDetectionBackfill.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Text.Json;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.LookOver;

/// <summary>
/// The startup step for a wall whose current generation was applied before discarded detections were kept: the
/// change journal's wall-update batch still has every staged detection that update deleted, with its outline, so they
/// are brought back as findings (<see cref="DiscardedDetections"/>). Only detections on a photo that is still live are
/// taken, and only journal rows written before the discard reason was recorded on the hold (their JSON has no such
/// field): from then on the promote keeps the detections itself, and a deleted staged hold without a reason is one a
/// person removed. The reason is therefore unknown for all of them. A wall that has such findings for its current
/// generation is left alone, so the step runs once per wall.
/// </summary>
public static class DiscardedDetectionBackfill
{
    private const string ReasonField = "\"DiscardReason\"";

    /// <summary>Backfills every wall that needs it; a wall that fails is retried on the next start.</summary>
    /// <param name="factory">Context factory.</param>
    /// <param name="logger">Logger.</param>
    /// <returns>A task.</returns>
    public static async Task RunIfNeededAsync(IDbContextFactory<BlocwerkDbContext> factory, ILogger logger)
    {
        await using var list = await factory.CreateDbContextAsync();
        var wallIds = await list.Walls.IgnoreQueryFilters().AsNoTracking()
            .Where(w => !list.WallLookOverFindings.Any(
                f => f.WallId == w.Id && f.Generation == w.CurrentGeneration && f.Kind == LookOverFindingKind.DiscardedDetection))
            .Select(w => w.Id)
            .ToListAsync();
        foreach (var wallId in wallIds)
        {
            try
            {
                await using var db = await factory.CreateDbContextAsync();
                var added = await BackfillWallAsync(db, wallId, CancellationToken.None);
                if (added > 0)
                {
                    logger.LogInformation("Discarded-detection backfill on wall {WallId}: {Count} detections of its last update brought back from the journal", wallId, added);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Discarded-detection backfill failed for wall {WallId}; it is retried on the next start.", wallId);
            }
        }
    }

    /// <summary>Brings back the detections the wall's last update deleted; flags the wall when one is likely a missed hold.</summary>
    /// <param name="db">The context (saved here).</param>
    /// <param name="wallId">The wall.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>How many findings were added.</returns>
    public static async Task<int> BackfillWallAsync(BlocwerkDbContext db, Guid wallId, CancellationToken ct)
    {
        var wall = await db.Walls.IgnoreQueryFilters().FirstAsync(w => w.Id == wallId, ct);
        var live = (await LiveWallHolds.LoadPanelIdsAsync(db, wallId, ct)).ToHashSet();
        var deleted = await db.ChangeJournalEntries.AsNoTracking()
            .Where(e => e.EntityType == nameof(Hold) && e.Op == ChangeJournalOp.Delete && e.BeforeJson != null && !e.BeforeJson.Contains(ReasonField)
                && db.ChangeJournalBatches.Any(b => b.Id == e.BatchId && b.Label == ChangeJournal.WallUpdateBatchLabel
                    && b.ScopeId == wallId && b.Status == ChangeJournalStatus.Recorded))
            .Select(e => e.BeforeJson!)
            .ToListAsync(ct);
        var findings = deleted
            .Select(Read)
            .OfType<Hold>()
            .Where(h => h.WallId == wallId && h.Generation == wall.CurrentGeneration && h is { IsAutoDetected: true, IsVirtual: false, WallPanelId: { } panel } && live.Contains(panel))
            .Select(DiscardedDetections.From)
            .ToList();
        if (findings.Count == 0)
        {
            return 0;
        }

        db.WallLookOverFindings.AddRange(findings);
        await db.SaveChangesAsync(ct);
        await DiscardedDetections.FlagIfLikelyMissedAsync(db, wall, wall.CurrentGeneration, ct);
        return findings.Count;
    }

    /// <summary>The hold a journal row describes, as far as a detection needs it; null for a row that cannot be read.</summary>
    private static Hold? Read(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var row = document.RootElement;
            return new Hold
            {
                WallId = row.GetProperty(nameof(Hold.WallId)).GetGuid(),
                WallPanelId = row.TryGetProperty(nameof(Hold.WallPanelId), out var panel) && panel.ValueKind == JsonValueKind.String ? panel.GetGuid() : null,
                Generation = row.GetProperty(nameof(Hold.Generation)).GetInt32(),
                X = row.GetProperty(nameof(Hold.X)).GetDouble(),
                Y = row.GetProperty(nameof(Hold.Y)).GetDouble(),
                Radius = row.GetProperty(nameof(Hold.Radius)).GetDouble(),
                Confidence = row.GetProperty(nameof(Hold.Confidence)).GetDouble(),
                IsAutoDetected = row.GetProperty(nameof(Hold.IsAutoDetected)).GetBoolean(),
                IsVirtual = row.GetProperty(nameof(Hold.IsVirtual)).GetBoolean(),
                ShapePoints = Shape<List<ShapePoint>>(row, nameof(Hold.ShapePoints)),
                ShapeHoles = Shape<List<List<ShapePoint>>>(row, nameof(Hold.ShapeHoles)),
            };
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    /// <summary>An outline column of a journal row: written as JSON, or as the column's JSON text.</summary>
    private static T? Shape<T>(JsonElement row, string name)
        where T : class
    {
        if (!row.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Array => value.Deserialize<T>(),
            JsonValueKind.String when value.GetString() is { Length: > 0 } text => JsonSerializer.Deserialize<T>(text),
            _ => null,
        };
    }
}
