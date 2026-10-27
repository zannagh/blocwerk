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
/// <item>a hold a person confirmed as moved that now clearly moved more marks its boulders for review and records the new
/// distance next to what they were told; memberships are never changed here;</item>
/// <item>a hold nobody confirmed as moved that now clearly moved marks nothing: it is asked about in the look-over
/// (<see cref="LookOverFindingKind.SuspectedMove"/>), and only a person's "it moved" marks its boulders. A hold the update's
/// review already asked about (<see cref="HoldMoveOutcome.Suspected"/>) is not asked again;</item>
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
            if (!measure.Confident || ReOutlineHolds(link, measure, options))
            {
                continue;
            }

            var confirmed = link.Kind == HoldGenerationLinkKind.Changed;
            var outcome = HoldMovePolicy.Unconfirmed(HoldMovePolicy.Classify(measure, options, confirmed), confirmed);
            var before = link.MoveOutcome ?? HoldMoveOutcome.Stayed;
            if (link.MoveSource == HoldMoveSource.ThreeD && Rank(before) == Rank(outcome))
            {
                continue;
            }

            var told = (link.MoveDistanceMm ?? 0, link.MoveSource ?? HoldMoveSource.TwoD);
            (link.MoveDistanceMm, link.MoveSource, link.MoveOutcome) = (Math.Round(measure.DistanceMm, 1), HoldMoveSource.ThreeD, outcome);
            if (Rank(outcome) > Rank(before) && !confirmed)
            {
                await AskAboutMoveAsync(db, link, generation, logger);
            }
            else if (Rank(outcome) > Rank(before))
            {
                marked += await EscalateAsync(db, link, told, measure, outcome, generation, logger);
            }
            else if (Rank(outcome) < Rank(before))
            {
                await SettleAsync(db, link, measure, outcome, generation, cleared, logger);
            }
        }

        await db.SaveChangesAsync();
        // A boulder with an open look-over finding on one of its holds is not done, whatever was measured.
        await ClearSettledBouldersAsync(db, cleared, generation, logger, boulderId => HasOpenFindingAsync(db, wallId, boulderId, null, default));
        return marked;
    }

    /// <summary>
    /// Clears the review mark of a boulder whose every reason was withdrawn, when nothing else asks for a look. A reason
    /// is withdrawn when its row says "stayed" after all: a move the 3D measurement took back, or a hold not found again
    /// that a person saw is still there (the look-over's "Still there"). A hold taken off the boulder or replaced by
    /// another is never withdrawn.
    /// </summary>
    /// <param name="db">The context (saved here).</param>
    /// <param name="candidates">The boulders one of whose reasons was just withdrawn.</param>
    /// <param name="generation">The generation of the update that gave the reasons.</param>
    /// <param name="logger">Where a cleared mark is logged.</param>
    /// <param name="alsoOpen">A further reason to keep a boulder marked, asked last (the look-over's other findings).</param>
    /// <returns>The boulders whose mark was cleared.</returns>
    internal static async Task<List<Guid>> ClearSettledBouldersAsync(
        BlocwerkDbContext db, IReadOnlyCollection<Guid> candidates, int generation, ILogger logger, Func<Guid, Task<bool>>? alsoOpen = null)
    {
        var cleared = new List<Guid>();
        foreach (var boulderId in candidates.Distinct())
        {
            var boulder = await db.Boulders.FirstOrDefaultAsync(b => b.Id == boulderId && b.NeedsReview && b.Generation == generation && !b.IsHistoric);
            var open = boulder is null
                || await db.BoulderHoldMoves.AnyAsync(m => m.BoulderId == boulderId && m.ToGeneration == generation && (m.Outcome == HoldMoveOutcome.Removed || m.Outcome == HoldMoveOutcome.Replaced || m.RemeasuredOutcome != HoldMoveOutcome.Stayed))
                || await db.BoulderHolds.AnyAsync(bh => bh.BoulderId == boulderId
                    && (bh.Hold.NeedsReview || db.HoldGenerationLinks.Any(l => l.NewHoldId == bh.HoldId && l.Kind == HoldGenerationLinkKind.Changed)))
                || (alsoOpen is not null && await alsoOpen(boulderId));
            if (!open)
            {
                boulder!.NeedsReview = false;
                cleared.Add(boulderId);
                logger.LogInformation("Boulder {BoulderId}: review mark cleared, every reason the update gave for it was withdrawn", boulderId);
            }
        }

        await db.SaveChangesAsync();
        return cleared;
    }

    /// <summary>An open look-over finding is about a hold the boulder uses: somebody still has to look at that hold.</summary>
    /// <param name="db">The context.</param>
    /// <param name="wallId">The wall.</param>
    /// <param name="boulderId">The boulder.</param>
    /// <param name="exceptFindingId">A finding that does not count (the one being settled).</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>True when there is one.</returns>
    internal static Task<bool> HasOpenFindingAsync(BlocwerkDbContext db, Guid wallId, Guid boulderId, Guid? exceptFindingId, CancellationToken ct) =>
        db.WallLookOverFindings.AnyAsync(
            f => f.WallId == wallId && f.Id != exceptFindingId && f.Status == LookOverFindingStatus.Open && f.HoldId != null
                && db.BoulderHolds.Any(bh => bh.BoulderId == boulderId && bh.HoldId == f.HoldId),
            ct);

    private static int Rank(HoldMoveOutcome o) => o == HoldMoveOutcome.Stayed ? 0 : 1;

    /// <summary>
    /// A move nobody confirmed, seen only now in 3D: a look-over finding on the hold, unless one is already open about it.
    /// Nothing on the wall or its boulders changes.
    /// </summary>
    private static async Task AskAboutMoveAsync(BlocwerkDbContext db, HoldGenerationLink link, int generation, ILogger logger)
    {
        var hold = link.NewHold!;
        if (await db.WallLookOverFindings.AnyAsync(f => f.WallId == link.WallId && f.HoldId == hold.Id && f.Status == LookOverFindingStatus.Open))
        {
            return;
        }

        var boulders = await db.BoulderHolds.CountAsync(bh => bh.HoldId == hold.Id && !bh.Boulder.IsArchived && !bh.Boulder.IsHistoric);
        db.WallLookOverFindings.Add(new WallLookOverFinding
        {
            WallId = link.WallId,
            Generation = generation,
            Kind = LookOverFindingKind.SuspectedMove,
            OldHoldId = link.OldHoldId,
            HoldId = hold.Id,
            PanelId = hold.WallPanelId,
            X = hold.X,
            Y = hold.Y,
            Radius = hold.Radius,
            ShapePoints = hold.ShapePoints?.Select(p => new ShapePoint { Dx = p.Dx, Dy = p.Dy }).ToList(),
            BouldersAtApply = boulders,
        });
        var wall = await db.Walls.IgnoreQueryFilters().FirstAsync(w => w.Id == link.WallId);
        wall.LookOverPendingSince ??= DateTimeOffset.UtcNow;
        wall.LookOverGeneration = generation;

        logger.LogInformation(
            "Hold move re-measured in 3D: hold {HoldId} may have moved {Mm} mm; nobody confirmed a move, so it is asked about in the look-over",
            hold.Id, link.MoveDistanceMm);
    }

    /// <summary>
    /// A pair the plan judged a re-outline keeps that verdict only while the 3D measure stays under the rule's own bar for an
    /// unexplained shift (<see cref="HoldMoveOptions.NoiseMm2D"/>); from there on the placements say the hold moved, and it
    /// is escalated as any other (25 to 100 mm: kept and its boulders marked).
    /// </summary>
    private static bool ReOutlineHolds(HoldGenerationLink link, HoldMoveMeasure measure, HoldMoveOptions options) =>
        link.MoveSource == HoldMoveSource.ReOutline && measure.DistanceMm < options.NoiseMm2D;

    /// <summary>A row that records a hold not found again or replaced, not a measured move: never settled or escalated.</summary>
    private static bool IsNotFound(HoldMoveOutcome o) => o is HoldMoveOutcome.NotFound or HoldMoveOutcome.Replaced;

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
            if (row is not null && IsNotFound(row.Outcome))
            {
                // Not a move: the boulder was told the hold was not found again (or replaced) and is marked for that.
                continue;
            }

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
            .Where(m => m.OldHoldId == link.OldHoldId && m.NewHoldId == link.NewHoldId && m.ToGeneration == generation && m.Outcome != HoldMoveOutcome.Removed
                && m.Outcome != HoldMoveOutcome.NotFound && m.Outcome != HoldMoveOutcome.Replaced)
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
