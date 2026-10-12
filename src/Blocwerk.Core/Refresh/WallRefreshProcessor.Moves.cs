// <copyright file="WallRefreshProcessor.Moves.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Enums;
using Blocwerk.Core.HoldMoves;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Refresh;

/// <summary>The confirm screen's "Moved holds" and "not found again" lists: what the plan will do to which boulders.</summary>
public sealed partial class WallRefreshProcessor
{
    /// <summary>
    /// Measures the carried holds again with their real 3D placements. Never fails the update; boulders whose verdict would
    /// change are marked for review, not changed.
    /// </summary>
    private async Task RemeasureMovesAsync(Guid wallId)
    {
        try
        {
            using var holdWrite = await Services.WallHoldWriteLock.AcquireAsync(wallId);
            await using var db = dbContextFactory.CreateDbContext();
            var marked = await HoldMoveRemeasurer.RunAsync(db, wallId, moveOptions ?? new HoldMoveOptions(), logger);
            logger.LogInformation("Wall {WallId}: hold moves measured again after placement, {Marked} boulders marked for review", wallId, marked);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Wall {WallId}: hold moves could not be measured again after placement", wallId);
        }
    }

    /// <summary>The moved holds of <paramref name="plan"/> with their names and the boulders using them; null when none moved.</summary>
    private async Task<IReadOnlyList<MovedHoldLine>?> MovedLinesAsync(HoldMovePlan? plan, CancellationToken ct)
    {
        var moved = plan?.Moves.Where(m => m.Outcome != HoldMoveOutcome.Stayed).ToList();
        if (moved is not { Count: > 0 })
        {
            return null;
        }

        var oldIds = moved.Select(m => m.OldHoldId).ToList();
        await using var db = dbContextFactory.CreateDbContext();
        var names = await db.Holds.AsNoTracking().Where(h => oldIds.Contains(h.Id)).Select(h => new { h.Id, h.Name }).ToDictionaryAsync(h => h.Id, h => h.Name, ct);
        var uses = (await db.BoulderHolds.AsNoTracking()
                .Where(bh => oldIds.Contains(bh.HoldId) && !bh.Boulder.IsArchived && !bh.Boulder.IsHistoric)
                .Select(bh => new { bh.HoldId, bh.Boulder.Name })
                .ToListAsync(ct))
            .GroupBy(x => x.HoldId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.Select(x => x.Name).Order(StringComparer.OrdinalIgnoreCase).ToList());
        return moved
            .OrderByDescending(m => m.Outcome)
            .ThenByDescending(m => m.Measure.DistanceMm)
            .ThenBy(m => m.OldHoldId)
            .Select(m => new MovedHoldLine(
                m.OldHoldId,
                m.NewHoldId,
                names.GetValueOrDefault(m.OldHoldId),
                Math.Round(m.Measure.DistanceMm, 1),
                m.Measure.Source,
                m.Outcome,
                HoldMovePolicy.Describe(m.Measure.DistanceMm, m.Measure.RotationDeg, m.Outcome).Replace("this boulder", "its boulders", StringComparison.Ordinal),
                uses.GetValueOrDefault(m.OldHoldId) ?? []))
            .ToList();
    }

    /// <summary>
    /// The holds of <paramref name="plan"/> that were not found again and are used by a live boulder, with those boulders,
    /// and how many boulders they mark in all. A hold no boulder uses marks nothing and is not listed.
    /// </summary>
    private async Task<(IReadOnlyList<NotFoundHoldLine>? Lines, int Boulders)> NotFoundLinesAsync(HoldMovePlan? plan, CancellationToken ct)
    {
        if (plan?.NotFound is not { Count: > 0 } notFound)
        {
            return (null, 0);
        }

        var oldIds = notFound.Keys.ToList();
        await using var db = dbContextFactory.CreateDbContext();
        var uses = await db.BoulderHolds.AsNoTracking()
            .Where(bh => oldIds.Contains(bh.HoldId) && !bh.Boulder.IsArchived && !bh.Boulder.IsHistoric)
            .Select(bh => new { bh.HoldId, bh.BoulderId, bh.Boulder.Name, HoldName = bh.Hold.Name, bh.Hold.Color })
            .ToListAsync(ct);
        var lines = uses
            .GroupBy(u => u.HoldId)
            .OrderByDescending(g => notFound[g.Key])
            .ThenBy(g => g.Key)
            .Select(g => new NotFoundHoldLine(
                g.Key,
                g.First().HoldName ?? g.First().Color,
                notFound[g.Key],
                HoldMovePolicy.NotFoundText(notFound[g.Key]) ?? string.Empty,
                g.Select(u => u.Name).Order(StringComparer.OrdinalIgnoreCase).ToList()))
            .ToList();
        return (lines.Count == 0 ? null : lines, uses.Select(u => u.BoulderId).Distinct().Count());
    }
}
