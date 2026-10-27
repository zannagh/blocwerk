// <copyright file="HoldTexturePlacementService.Outliers.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Data;
using Blocwerk.Core.Detection.Enrichment;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Feed;
using Blocwerk.Core.Geometry.TextureRegistration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Services;

/// <summary>
/// "Re-place outlier holds in 3D" (<see cref="HoldOutlierPlanner"/>): a dry run that only counts, and the run that writes.
/// Only the holds' 3D fields are written (facet, plane position, size, metric source; footprint and protrusion are dropped
/// for the refinement to measure again); panel position, size, outline, links and boulders are never touched. Recorded as
/// a <see cref="HoldPlacementTrigger.Outliers"/> run, so the existing revert restores it exactly.
/// </summary>
public sealed partial class HoldTexturePlacementService
{
    /// <inheritdoc/>
    public async Task<HoldOutlierPreview> PreviewOutliersAsync(Guid wallId, CancellationToken ct = default)
    {
        var (db, _) = await OpenForAdminAsync(wallId, ct);
        await using (db)
        {
            var (_, plan) = await PlanOutliersAsync(db, wallId, ct);
            return HoldOutlierPreview.Of(plan);
        }
    }

    /// <inheritdoc/>
    public async Task<HoldPlacementResult> ReplaceOutliersAsync(Guid wallId, CancellationToken ct = default)
    {
        var (db, userId) = await OpenForAdminAsync(wallId, ct);
        await using (db)
        {
            return await ExclusiveAsync(wallId, () => ReplaceOutliersAsync(db, wallId, userId, ct))
                   ?? throw new UserFacingException("Holds are already being placed on this wall. Try again in a minute.");
        }
    }

    private async Task<HoldPlacementResult> ReplaceOutliersAsync(BlocwerkDbContext db, Guid wallId, Guid userId, CancellationToken ct)
    {
        using var feedScope = FeedBulkScope.Begin(wallId, "hold-outlier-placement");
        var (modelId, plan) = await PlanOutliersAsync(db, wallId, ct);
        var textures = await TextureSetStamp.OfModelAsync(db, modelId, ct);
        var run = new HoldPlacementRun
        {
            WallId = wallId, GeometryModelId = modelId, TextureSetKey = textures?.Key, CreatedByUserId = userId, Trigger = HoldPlacementTrigger.Outliers,
        };
        db.HoldPlacementRuns.Add(run);
        await db.SaveChangesAsync(ct);

        var entries = new List<HoldPlacementEntry>();
        foreach (var batch in plan.Fixes.Chunk(BatchSize))
        {
            await WriteOutlierBatchAsync(db, run, batch, entries, ct);
        }

        var written = entries.Select(e => e.HoldId).ToHashSet();
        var panels = await OutlierSummariesAsync(db, plan, written, ct);
        (run.PlacedCount, run.SkippedCount, run.FailedCount) = (written.Count, panels.Sum(p => p.Skipped), panels.Sum(p => p.Failed));
        run.PanelsJson = PanelSummaries.ToJson(panels);
        await db.SaveChangesAsync(ct);
        if (refinementQueue is not null && written.Count > 0)
        {
            // New positions: footprint, protrusion and volume placement are measured again there.
            refinementQueue.Enqueue(wallId, written);
        }

        logger.LogInformation(
            "Outlier placement {RunId} on wall {WallId} by {UserId}, model {ModelId}: {Placed} re-placed ({Twins} onto a linked copy), "
            + "{Planned} planned, {Undecided} undecided",
            run.Id, wallId, userId, modelId, written.Count, plan.Fixes.Count(f => f.Twin is not null && written.Contains(f.Hold.Id)),
            plan.Fixes.Count, run.FailedCount);
        return new HoldPlacementResult(run.Id, run.PlacedCount, run.SkippedCount, run.FailedCount, panels);
    }

    /// <summary>The active model and the plan over the wall's live holds and "same hold" links.</summary>
    private async Task<(Guid ModelId, HoldOutlierPlan Plan)> PlanOutliersAsync(BlocwerkDbContext db, Guid wallId, CancellationToken ct)
    {
        var model = await ActiveModelTextures.FindAsync(db, wallId, ct)
            ?? throw new UserFacingException("This wall has no active 3D model to check the holds against.");
        var live = await (await LiveHoldsQueryAsync(db, wallId, ct)).AsNoTracking().ToListAsync(ct);
        var links = await db.HoldLinks.AsNoTracking()
            .Where(l => l.WallId == wallId && l.Kind == HoldLinkKind.Same)
            .Select(l => new HoldLinkPair(l.HoldAId, l.HoldBId))
            .ToListAsync(ct);
        return (model.Id, HoldOutlierPlanner.Plan(live, links, Facets(model.Json).Extents));
    }

    /// <summary>
    /// In one transaction: claims the batch's holds (a newer edit wins and the hold is skipped), writes each fix's 3D fields
    /// and saves them with the run's grown entry list.
    /// </summary>
    private static async Task WriteOutlierBatchAsync(
        BlocwerkDbContext db, HoldPlacementRun run, HoldOutlierFix[] batch, List<HoldPlacementEntry> entries, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var holds = await ClaimAndReadAsync(db, batch.Select(f => f.Hold), ct);
        foreach (var fix in batch)
        {
            if (holds.TryGetValue(fix.Hold.Id, out var hold) && !ChangedSincePlanned(hold, fix.Hold, HoldOutlierPlanner.IsPlaced))
            {
                entries.Add(WriteOutlier(hold, fix));
            }
        }

        run.HoldsJson = HoldPlacementEntry.ToJson(entries);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    /// <summary>Writes one fix: facet, plane position, size and source; drops the footprint and protrusion measured at the old spot.</summary>
    private static HoldPlacementEntry WriteOutlier(Hold hold, HoldOutlierFix fix)
    {
        var entry = HoldPlacementEntry.Before(hold) with { RestoresVolumePlacement = true, PrevVolumePlacementJson = hold.VolumePlacementJson };
        if (fix.Size is { } size)
        {
            HoldMetricPlanner.Apply(hold, size with { FacetId = fix.FacetId, PlaneAMm = fix.PlaneAMm, PlaneBMm = fix.PlaneBMm, MetricSource = HoldMetric.Consensus });
        }
        else
        {
            (hold.WidthMm, hold.HeightMm, hold.AreaMm2) = (null, null, null);
            (hold.FacetId, hold.PlaneAMm, hold.PlaneBMm, hold.MetricSource) = (fix.FacetId, fix.PlaneAMm, fix.PlaneBMm, HoldMetric.Consensus);
        }

        (hold.FootprintMm, hold.ProtrusionMm) = (null, null);
        return entry with
        {
            PlacementHash = HoldPlacementEntry.HashPlacement(hold),
            FingerprintHash = HoldPlacementEntry.HashFingerprint(hold.FingerprintJson),
            GeometryHash = HoldPlacementEntry.HashGeometry(hold),
        };
    }

    /// <summary>The run's per-panel report: re-placed, left alone (agreeing), undecided.</summary>
    private static async Task<List<HoldPlacementPanelSummary>> OutlierSummariesAsync(
        BlocwerkDbContext db, HoldOutlierPlan plan, HashSet<Guid> written, CancellationToken ct)
    {
        var ids = plan.Panels.Select(p => p.PanelId).ToList();
        var cells = await db.WallPanels.AsNoTracking().Where(p => ids.Contains(p.Id))
            .Select(p => new { p.Id, p.Col, p.Row }).ToDictionaryAsync(p => p.Id, ct);
        return plan.Panels.Select(p =>
        {
            var placed = plan.Fixes.Count(f => f.Hold.WallPanelId == p.PanelId && written.Contains(f.Hold.Id));
            var cell = cells.GetValueOrDefault(p.PanelId);
            return new HoldPlacementPanelSummary(
                p.PanelId, cell?.Col ?? 0, cell?.Row ?? 0, placed, p.Checked - placed - p.Undecided, p.Undecided, [], null);
        }).ToList();
    }
}
