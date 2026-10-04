// <copyright file="HoldMoveRemeasurer.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Geometry.TextureRegistration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.HoldMoves;

/// <summary>
/// After the new holds were placed on the 3D model (the post-promote placement run), measures every carried pair of the
/// latest update again with the real placements. The promote's measurement may have been a photo estimate; the placement
/// is the better number. Boulders are never silently changed by it: where the verdict would differ, the boulder is marked
/// for review and the new distance is recorded next to what it was told (<see cref="BoulderHoldMove.RemeasuredDistanceMm"/>).
/// The lineage link keeps the better measurement.
/// </summary>
public static class HoldMoveRemeasurer
{
    /// <summary>Re-measures the wall's links into its current generation and saves; returns how many boulders were marked.</summary>
    /// <param name="db">The context (saved here).</param>
    /// <param name="wallId">The wall.</param>
    /// <param name="options">The thresholds.</param>
    /// <param name="logger">Where the changed verdicts are logged.</param>
    /// <returns>The number of boulders newly marked for review.</returns>
    public static async Task<int> RunAsync(BlocwerkDbContext db, Guid wallId, HoldMoveOptions options, ILogger logger)
    {
        var generation = await db.Walls.Where(w => w.Id == wallId).Select(w => w.CurrentGeneration).FirstOrDefaultAsync();
        var links = await db.HoldGenerationLinks
            .Include(l => l.OldHold)
            .Include(l => l.NewHold)
            .Where(l => l.WallId == wallId && l.ToGeneration == generation && l.OldHoldId != null && l.NewHoldId != null)
            .ToListAsync();
        var marked = 0;
        foreach (var link in links.Where(l => l.NewHold is not null && l.OldHold is not null && HoldTexturePlacer.IsTexturePlaced(l.NewHold)))
        {
            if (HoldMoveCalculator.Distance3D(link.OldHold!, link.NewHold!) is not { } distance)
            {
                continue;
            }

            var measure = new HoldMoveMeasure(distance, HoldMoveSource.ThreeD, link.MoveRotationDeg);
            var outcome = HoldMovePolicy.Classify(measure, options);
            var before = link.MoveOutcome ?? HoldMoveOutcome.Stayed;
            if (link.MoveSource == HoldMoveSource.ThreeD && before == outcome)
            {
                continue;
            }

            var told = (link.MoveDistanceMm ?? 0, link.MoveSource ?? HoldMoveSource.TwoD);
            (link.MoveDistanceMm, link.MoveSource, link.MoveOutcome) = (Math.Round(distance, 1), HoldMoveSource.ThreeD, outcome);
            if (before != outcome)
            {
                marked += await FlagBouldersAsync(db, link, told, measure, outcome, generation, logger);
            }
        }

        if (db.ChangeTracker.HasChanges())
        {
            await db.SaveChangesAsync();
        }

        return marked;
    }

    private static async Task<int> FlagBouldersAsync(
        BlocwerkDbContext db, HoldGenerationLink link, (double Mm, HoldMoveSource Source) told, HoldMoveMeasure measure, HoldMoveOutcome outcome, int generation, ILogger logger)
    {
        var rows = await db.BoulderHoldMoves
            .Where(m => m.OldHoldId == link.OldHoldId && m.NewHoldId == link.NewHoldId && m.ToGeneration == generation)
            .ToListAsync();
        var members = await db.BoulderHolds
            .Where(bh => bh.HoldId == link.NewHoldId && !bh.Boulder.IsArchived && !bh.Boulder.IsHistoric && bh.Boulder.Generation == generation)
            .Include(bh => bh.Boulder)
            .ToListAsync();
        var marked = 0;
        foreach (var member in members)
        {
            var row = rows.FirstOrDefault(r => r.BoulderId == member.BoulderId);
            if (row is null)
            {
                row = NewRow(link, member, told, generation);
                db.BoulderHoldMoves.Add(row);
            }

            (row.RemeasuredDistanceMm, row.RemeasuredOutcome) = (Math.Round(measure.DistanceMm, 1), outcome);
            marked += member.Boulder.NeedsReview ? 0 : 1;
            member.Boulder.NeedsReview = true;
            logger.LogInformation(
                "Hold move re-measured in 3D: boulder {BoulderId} hold {HoldId} is now {Mm} mm ({Outcome}), marked for review",
                member.BoulderId, link.NewHoldId, Math.Round(measure.DistanceMm), outcome);
        }

        // A boulder the promote took the hold off keeps that decision; it already needs review and shows the new number.
        foreach (var row in rows.Where(r => r.Outcome == HoldMoveOutcome.Removed))
        {
            (row.RemeasuredDistanceMm, row.RemeasuredOutcome) = (Math.Round(measure.DistanceMm, 1), outcome);
            logger.LogInformation(
                "Hold move re-measured in 3D: boulder {BoulderId} lost hold {HoldId}, which is now {Mm} mm ({Outcome})",
                row.BoulderId, link.OldHoldId, Math.Round(measure.DistanceMm), outcome);
        }

        return marked;
    }

    private static BoulderHoldMove NewRow(HoldGenerationLink link, BoulderHold member, (double Mm, HoldMoveSource Source) told, int generation) => new()
    {
        WallId = link.WallId,
        BoulderId = member.BoulderId,
        OldHoldId = link.OldHoldId,
        NewHoldId = link.NewHoldId,
        Type = member.Type,
        Usage = member.Usage,
        DistanceMm = told.Mm,
        Source = told.Source,
        Outcome = HoldMoveOutcome.Kept,
        FromGeneration = link.FromGeneration,
        ToGeneration = generation,
    };
}
