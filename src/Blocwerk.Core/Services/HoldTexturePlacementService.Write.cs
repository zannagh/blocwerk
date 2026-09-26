using Blocwerk.Core.Data;
using Blocwerk.Core.Detection.Enrichment;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Geometry;
using Blocwerk.Core.Geometry.TextureRegistration;
using Blocwerk.Core.Geometry.View3D;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Services;

/// <summary>Writing a run: all photos registered first, then the holds batch by batch, each batch saved with the run record.</summary>
public sealed partial class HoldTexturePlacementService
{
    private async Task<HoldPlacementResult> ExecuteAsync(
        BlocwerkDbContext db, Guid wallId, Guid userId, string trigger, bool enqueue, CancellationToken ct, IReadOnlySet<Guid>? only = null)
    {
        var model = await LoadActiveModelAsync(db, wallId, ct);
        var live = await (await LiveHoldsQueryAsync(db, wallId, ct)).AsNoTracking().ToListAsync(ct);

        // The automatic run after a capture: only the holds not on the model yet (see UnplacedHoldIdsAsync).
        live = only is null ? live : live.Where(h => only.Contains(h.Id)).ToList();
        var noPanel = live.Count(h => h.WallPanelId is null);
        var plans = await PlanPanelsAsync(db, wallId, live, model, ct);

        // The run row exists before the first hold is written, and every batch updates its entry list in the SAME
        // SaveChanges as the holds, so whatever was written is always revertable, even if the run is cut short.
        var run = new HoldPlacementRun { WallId = wallId, GeometryModelId = model.Id, CreatedByUserId = userId, Trigger = trigger };
        db.HoldPlacementRuns.Add(run);
        await db.SaveChangesAsync(ct);

        var entries = new List<HoldPlacementEntry>();
        var panels = new List<HoldPlacementPanelSummary>();
        foreach (var plan in plans)
        {
            var changed = 0;
            foreach (var batch in plan.Placements.Chunk(BatchSize))
            {
                changed += await WriteBatchAsync(db, run, batch, model.Extents, entries, ct);
            }

            var s = plan.Summary;
            panels.Add(s with { Placed = s.Placed - changed, Skipped = s.Skipped + changed });
        }

        run.PlacedCount = entries.Count;
        run.SkippedCount = panels.Sum(p => p.Skipped) + noPanel;
        run.FailedCount = panels.Sum(p => p.Failed);
        run.PanelsJson = PanelSummaries.ToJson(panels);
        await db.SaveChangesAsync(ct);
        if (enqueue && refinementQueue is not null && entries.Count > 0)
        {
            // New facet positions: the multi-view footprints (and protrusion) can now be refined for these holds.
            refinementQueue.Enqueue(wallId, entries.Select(e => e.HoldId));
        }

        logger.LogInformation(
            "Hold placement {RunId} ({Trigger}) on wall {WallId} by {UserId}, model {ModelId}: {Placed} placed, {Skipped} skipped, "
            + "{Failed} failed over {Panels} panel photos and {Textures} facet textures ({Carried} placed holds carried over from an earlier model)",
            run.Id, trigger, wallId, userId, model.Id, run.PlacedCount, run.SkippedCount, run.FailedCount, plans.Count, model.Textures.Count,
            panels.Sum(p => p.Carried));
        return new HoldPlacementResult(run.Id, run.PlacedCount, run.SkippedCount, run.FailedCount, panels);
    }

    /// <summary>
    /// Re-reads the batch's holds tracked, skips any that changed since they were planned, writes the rest and
    /// saves them together with the run's grown entry list. A placement off its target facet's extent is never written
    /// (<see cref="Wall3DHoldGuard.PlacementOnFacet"/>). Returns the number skipped.
    /// </summary>
    private static async Task<int> WriteBatchAsync(
        BlocwerkDbContext db,
        HoldPlacementRun run,
        PlannedPlacement[] batch,
        IReadOnlyDictionary<string, PlaneRectMm> extents,
        List<HoldPlacementEntry> entries,
        CancellationToken ct)
    {
        var ids = batch.Select(p => p.Hold.Id).ToList();
        var holds = await db.Holds.Where(h => ids.Contains(h.Id)).ToDictionaryAsync(h => h.Id, ct);
        var skipped = 0;
        foreach (var placement in batch)
        {
            var fit = placement.Fit;
            if (!holds.TryGetValue(placement.Hold.Id, out var hold) || ChangedSincePlanned(hold, placement.Hold)
                || !OnFacet(fit, extents, null))
            {
                skipped++;
                continue;
            }

            entries.Add(Write(hold, placement, extents));
        }

        run.HoldsJson = HoldPlacementEntry.ToJson(entries);
        await db.SaveChangesAsync(ct);
        return skipped;
    }

    /// <summary>Whether the placement (and the outline drawn at it) sits on its target facet.</summary>
    private static bool OnFacet(HoldPlaneFit fit, IReadOnlyDictionary<string, PlaneRectMm> extents, IReadOnlyList<double[]>? outline) =>
        Wall3DHoldGuard.PlacementOnFacet(fit.FacetId, fit.PlaneAMm, fit.PlaneBMm, extents, outline);

    /// <summary>An edit landed between planning and writing: the plan no longer describes this hold.</summary>
    private static bool ChangedSincePlanned(Hold current, Hold planned) =>
        !HoldTexturePlacer.IsEligible(current)
        || current.WallPanelId != planned.WallPanelId
        || current.X != planned.X
        || current.Y != planned.Y
        || current.Radius != planned.Radius
        || current.ShapeDiffers(planned.ShapePoints)
        || HoldPlacementEntry.HashPlacement(current) != HoldPlacementEntry.HashPlacement(planned);

    /// <summary>
    /// Writes one placement: the facet, the plane position, the metric source and the size (with the
    /// fingerprint's rotation-free sizes, as every metric writer does), and drops a footprint that would be drawn off
    /// the facet at the new position. Nothing else is touched.
    /// </summary>
    private static HoldPlacementEntry Write(Hold hold, PlannedPlacement placement, IReadOnlyDictionary<string, PlaneRectMm> extents)
    {
        var entry = HoldPlacementEntry.Before(hold);
        if (placement.Metric is { } metric)
        {
            HoldMetricPlanner.Apply(hold, metric);
        }
        else
        {
            // Placed but not measurable through the mapping: the old size belonged to the old position.
            hold.WidthMm = null;
            hold.HeightMm = null;
            hold.AreaMm2 = null;
            hold.FacetId = placement.Fit.FacetId;
            hold.PlaneAMm = placement.Fit.PlaneAMm;
            hold.PlaneBMm = placement.Fit.PlaneBMm;
            hold.MetricSource = placement.Carried ? HoldMetric.TextureRegistrationCarried : HoldMetric.TextureRegistration;
        }

        // A footprint refined around the old position that would now be drawn off the facet goes (revertable via the entry).
        if (hold.FootprintMm is not null && !OnFacet(placement.Fit, extents, HoldFootprint.For(hold)?.Outline))
        {
            hold.FootprintMm = null;
        }

        return entry with
        {
            PlacementHash = HoldPlacementEntry.HashPlacement(hold),
            FingerprintHash = HoldPlacementEntry.HashFingerprint(hold.FingerprintJson),
        };
    }
}
