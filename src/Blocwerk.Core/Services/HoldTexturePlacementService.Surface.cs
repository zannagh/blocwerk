// <copyright file="HoldTexturePlacementService.Surface.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Capture;
using Blocwerk.Core.Data;
using Blocwerk.Core.Detection.Enrichment;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Feed;
using Blocwerk.Core.Geometry.Footprints;
using Blocwerk.Core.Geometry.Surface;
using Blocwerk.Core.Geometry.TextureRegistration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Services;

/// <summary>
/// "Place holds on the 3D surface", step 2 (<see cref="SurfaceHoldPlanner"/>): a dry run that only counts, and the run that
/// writes. Only the holds' 3D fields are written (facet, plane position, size, metric source, footprint; protrusion is dropped
/// for the refinement to measure again); panel position, size, outline, links and boulders are never touched. Recorded as a
/// <see cref="HoldPlacementTrigger.Surface"/> run, so the existing revert restores it exactly.
/// </summary>
public sealed partial class HoldTexturePlacementService
{
    /// <inheritdoc/>
    public async Task<SurfacePreview> PreviewSurfaceAsync(Guid wallId, CancellationToken ct = default)
    {
        var (db, _) = await OpenForAdminAsync(wallId, ct);
        await using (db)
        {
            // Serialised with placing: each reads the whole photo-real view (one at a time per wall).
            return await ExclusiveAsync(wallId, async () => SurfacePreview.Of((await PlanSurfaceAsync(db, wallId, ct)).Plan))
                   ?? throw new UserFacingException("Holds are already being placed on this wall. Try again in a minute.");
        }
    }

    /// <inheritdoc/>
    public async Task<HoldPlacementResult> PlaceOnSurfaceAsync(Guid wallId, CancellationToken ct = default)
    {
        var (db, userId) = await OpenForAdminAsync(wallId, ct);
        await using (db)
        {
            return await ExclusiveAsync(wallId, () => PlaceOnSurfaceAsync(db, wallId, userId, ct))
                   ?? throw new UserFacingException("Holds are already being placed on this wall. Try again in a minute.");
        }
    }

    private async Task<HoldPlacementResult> PlaceOnSurfaceAsync(BlocwerkDbContext db, Guid wallId, Guid userId, CancellationToken ct)
    {
        using var feedScope = FeedBulkScope.Begin(wallId, "hold-surface-placement");
        var (modelId, plan) = await PlanSurfaceAsync(db, wallId, ct);
        var textures = await TextureSetStamp.OfModelAsync(db, modelId, ct);
        var run = new HoldPlacementRun
        {
            WallId = wallId, GeometryModelId = modelId, TextureSetKey = textures?.Key, CreatedByUserId = userId, Trigger = HoldPlacementTrigger.Surface,
        };
        db.HoldPlacementRuns.Add(run);
        await db.SaveChangesAsync(ct);

        var entries = new List<HoldPlacementEntry>();
        foreach (var batch in plan.Fixes.Chunk(BatchSize))
        {
            await WriteSurfaceBatchAsync(db, run, batch, entries, ct);
        }

        var written = entries.Select(e => e.HoldId).ToHashSet();
        var panels = await SurfaceSummariesAsync(db, plan, written, ct);
        (run.PlacedCount, run.SkippedCount, run.FailedCount) = (written.Count, plan.Fixes.Count - written.Count, panels.Sum(p => p.Failed));
        run.PanelsJson = PanelSummaries.ToJson(panels);
        await db.SaveChangesAsync(ct);
        if (refinementQueue is not null && written.Count > 0)
        {
            // New positions: protrusion, volume placement and (multi-view) footprints are measured again there.
            refinementQueue.Enqueue(wallId, written);
        }

        var preview = SurfacePreview.Of(plan);
        logger.LogInformation(
            "Surface placement {RunId} on wall {WallId} by {UserId}, model {ModelId}: {Placed} placed ({Own} own view, {Shared} shared, "
            + "{Fallback} outlier fallback), {Missed} missed; linked pairs {Pairs}, gap median {Median} mm, p90 {P90} mm",
            run.Id, wallId, userId, modelId, written.Count, preview.OwnView, preview.SharedView, preview.Fallback, preview.Missed,
            preview.Pairs, preview.PairGapMedianMm, preview.PairGapP90Mm);
        return new HoldPlacementResult(run.Id, run.PlacedCount, run.SkippedCount, run.FailedCount, panels);
    }

    /// <summary>The active model, its photo-real surface, the panel poses, and the plan over the wall's live holds.</summary>
    private async Task<(Guid ModelId, SurfacePlan Plan)> PlanSurfaceAsync(BlocwerkDbContext db, Guid wallId, CancellationToken ct)
    {
        var model = await ActiveModelTextures.FindAsync(db, wallId, ct)
            ?? throw new UserFacingException("This wall has no active 3D model to place the holds on.");
        var job = gpuJobs is null ? null : await gpuJobs.LatestPanelsJobAsync(wallId, model.Id, ct);
        var poses = await CurrentPosesAsync(db, job, ct);
        if (poses.Count == 0)
        {
            throw new UserFacingException("Register the panel photos on a 3D runner first: no panel photo has a pose for this model yet.");
        }

        var surface = await LoadSurfaceAsync(db, wallId, ct);
        var (extents, frames) = Facets(model.Json);
        var facets = frames.Where(f => extents.ContainsKey(f.Key)).ToDictionary(f => f.Key, f => (f.Value, extents[f.Key]), StringComparer.Ordinal);
        var live = await (await LiveHoldsQueryAsync(db, wallId, ct)).AsNoTracking().ToListAsync(ct);
        var links = await db.HoldLinks.AsNoTracking()
            .Where(l => l.WallId == wallId && l.Kind == HoldLinkKind.Same)
            .Select(l => new HoldLinkPair(l.HoldAId, l.HoldBId))
            .ToListAsync(ct);
        return (model.Id, await Task.Run(() => SurfaceHoldPlanner.Plan(live, links, poses, facets, surface), ct));
    }

    /// <summary>The active photo-real view's splat centres in the wall world.</summary>
    private async Task<SplatSurface> LoadSurfaceAsync(BlocwerkDbContext db, Guid wallId, CancellationToken ct)
    {
        var splat = await WallGeometrySplats.FindActiveAsync(db, wallId, ct);
        var matrix = splat is null ? null : CaptureSplatDocuments.WorldMatrix(splat.FrameJson);
        var spz = splat is null || matrix is null || files is null ? null : await files.ReadAsync(splat.StoredPath, ct);
        if (spz is null)
        {
            throw new UserFacingException("This wall has no photo-real view to place the holds on.");
        }

        // The SPZ bytes and the point list are dropped once the surface holds its one compact copy.
        return await Task.Run(() => new SplatSurface(SpzPoints.Read(spz, matrix!)), ct);
    }

    /// <summary>In one transaction: claims the batch's holds (a newer edit wins and the hold is skipped) and writes each fix.</summary>
    private static async Task WriteSurfaceBatchAsync(
        BlocwerkDbContext db, HoldPlacementRun run, SurfaceFix[] batch, List<HoldPlacementEntry> entries, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var holds = await ClaimAndReadAsync(db, batch.Select(f => f.Hold), ct);
        foreach (var fix in batch)
        {
            if (holds.TryGetValue(fix.Hold.Id, out var hold) && !ChangedSincePlanned(hold, fix.Hold, h => !h.IsVirtual && h.WallPanelId is not null))
            {
                entries.Add(WriteSurface(hold, fix));
            }
        }

        run.HoldsJson = HoldPlacementEntry.ToJson(entries);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    /// <summary>Writes one fix's 3D fields; the protrusion measured at the old spot is dropped.</summary>
    private static HoldPlacementEntry WriteSurface(Hold hold, SurfaceFix fix)
    {
        var entry = HoldPlacementEntry.Before(hold) with { RestoresVolumePlacement = true, PrevVolumePlacementJson = hold.VolumePlacementJson };
        var source = fix.FromOutlierFallback ? HoldMetric.Consensus : HoldMetric.Surface;
        if (fix.Size is { } size)
        {
            HoldMetricPlanner.Apply(hold, size with { FacetId = fix.FacetId, PlaneAMm = fix.A, PlaneBMm = fix.B, MetricSource = source });
        }
        else
        {
            // No size at the new spot: the one measured at the old spot would be wrong.
            (hold.WidthMm, hold.HeightMm, hold.AreaMm2) = (null, null, null);
            (hold.FacetId, hold.PlaneAMm, hold.PlaneBMm, hold.MetricSource) = (fix.FacetId, fix.A, fix.B, source);
        }

        (hold.FootprintMm, hold.ProtrusionMm) = (fix.Footprint?.ToJson(), null);
        return entry with
        {
            PlacementHash = HoldPlacementEntry.HashPlacement(hold),
            FingerprintHash = HoldPlacementEntry.HashFingerprint(hold.FingerprintJson),
            GeometryHash = HoldPlacementEntry.HashGeometry(hold),
        };
    }

    /// <summary>The run's per-panel report: placed, left alone, missed.</summary>
    private static async Task<List<HoldPlacementPanelSummary>> SurfaceSummariesAsync(
        BlocwerkDbContext db, SurfacePlan plan, HashSet<Guid> written, CancellationToken ct)
    {
        var ids = plan.Panels.Select(p => p.PanelId).ToList();
        var cells = await db.WallPanels.AsNoTracking().Where(p => ids.Contains(p.Id))
            .Select(p => new { p.Id, p.Col, p.Row }).ToDictionaryAsync(p => p.Id, ct);
        return plan.Panels.Select(p =>
        {
            var placed = plan.Fixes.Count(f => f.Hold.WallPanelId == p.PanelId && written.Contains(f.Hold.Id));
            var cell = cells.GetValueOrDefault(p.PanelId);
            return new HoldPlacementPanelSummary(p.PanelId, cell?.Col ?? 0, cell?.Row ?? 0, placed, p.Checked - placed - p.Missed, p.Missed, [], null);
        }).ToList();
    }
}
