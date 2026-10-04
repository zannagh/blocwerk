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
/// latest update again with the real placements, the same differential way as the plan (each hold against its unmoved
/// neighbourhood), and only acts on a CONFIDENT change:
/// <list type="bullet">
/// <item>a hold that now clearly moved (more than it was told) marks its boulders for review and records the new distance
/// next to what they were told; memberships are never changed here;</item>
/// <item>a hold that now clearly stayed clears the review reason an earlier, rougher measurement set, when nothing else
/// about the boulder asks for a look.</item>
/// </list>
/// An unsure measurement (too few or too noisy neighbours) changes nothing.
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
        var links = (await db.HoldGenerationLinks
                .Include(l => l.OldHold)
                .Include(l => l.NewHold)
                .Where(l => l.WallId == wallId && l.ToGeneration == generation && l.OldHoldId != null && l.NewHoldId != null)
                .ToListAsync())
            .Where(l => l.NewHold is not null && l.OldHold is not null && HoldTexturePlacer.IsTexturePlaced(l.NewHold))
            .ToList();
        var measures = DifferentialDisplacement.Measure(links.Select(l => (l.OldHold!, l.NewHold!)).ToList(), options);
        var marked = 0;
        var cleared = new List<Guid>();
        foreach (var link in links.Where(l => measures.ContainsKey(l.OldHoldId!.Value)))
        {
            var measure = measures[link.OldHoldId!.Value];
            if (!measure.Confident)
            {
                continue;
            }

            var outcome = HoldMovePolicy.Classify(measure, options, link.Kind == HoldGenerationLinkKind.Changed);
            var before = link.MoveOutcome ?? HoldMoveOutcome.Stayed;
            if (link.MoveSource == HoldMoveSource.ThreeD && Rank(before) == Rank(outcome))
            {
                continue;
            }

            var told = (link.MoveDistanceMm ?? 0, link.MoveSource ?? HoldMoveSource.TwoD);
            (link.MoveDistanceMm, link.MoveSource, link.MoveOutcome) = (Math.Round(measure.DistanceMm, 1), HoldMoveSource.ThreeD, outcome);
            if (Rank(outcome) > Rank(before))
            {
                marked += await EscalateAsync(db, link, told, measure, outcome, generation, logger);
            }
            else if (Rank(outcome) < Rank(before))
            {
                await SettleAsync(db, link, measure, outcome, generation, cleared, logger);
            }
        }

        await db.SaveChangesAsync();
        await ClearSettledBouldersAsync(db, cleared, generation, logger);
        return marked;
    }

    private static int Rank(HoldMoveOutcome o) => o == HoldMoveOutcome.Stayed ? 0 : 1;

    private static async Task<int> EscalateAsync(
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
        }

        return marked;
    }

    private static async Task SettleAsync(
        BlocwerkDbContext db, HoldGenerationLink link, HoldMoveMeasure measure, HoldMoveOutcome outcome, int generation, List<Guid> cleared, ILogger logger)
    {
        var rows = await db.BoulderHoldMoves
            .Where(m => m.OldHoldId == link.OldHoldId && m.NewHoldId == link.NewHoldId && m.ToGeneration == generation && m.Outcome != HoldMoveOutcome.Removed)
            .ToListAsync();
        foreach (var row in rows)
        {
            (row.RemeasuredDistanceMm, row.RemeasuredOutcome) = (Math.Round(measure.DistanceMm, 1), outcome);
            cleared.Add(row.BoulderId);
            logger.LogInformation(
                "Hold move re-measured in 3D: boulder {BoulderId} hold {HoldId} stayed ({Mm} mm); the earlier move is withdrawn",
                row.BoulderId, link.NewHoldId, Math.Round(measure.DistanceMm));
        }
    }

    /// <summary>Clears the review mark of a boulder whose every move was withdrawn, when nothing else asks for a look.</summary>
    private static async Task ClearSettledBouldersAsync(BlocwerkDbContext db, List<Guid> candidates, int generation, ILogger logger)
    {
        foreach (var boulderId in candidates.Distinct())
        {
            var boulder = await db.Boulders.FirstOrDefaultAsync(b => b.Id == boulderId && b.NeedsReview && b.Generation == generation && !b.IsHistoric);
            var open = boulder is null
                || await db.BoulderHoldMoves.AnyAsync(m => m.BoulderId == boulderId && m.ToGeneration == generation && (m.Outcome == HoldMoveOutcome.Removed || m.RemeasuredOutcome != HoldMoveOutcome.Stayed))
                || await db.BoulderHolds.AnyAsync(bh => bh.BoulderId == boulderId
                    && (bh.Hold.NeedsReview || db.HoldGenerationLinks.Any(l => l.NewHoldId == bh.HoldId && l.Kind == HoldGenerationLinkKind.Changed)));
            if (!open)
            {
                boulder!.NeedsReview = false;
                logger.LogInformation("Boulder {BoulderId}: review mark cleared, the moved hold was measured again and stayed", boulderId);
            }
        }

        await db.SaveChangesAsync();
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
