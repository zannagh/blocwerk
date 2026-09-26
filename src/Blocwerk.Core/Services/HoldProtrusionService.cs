// <copyright file="HoldProtrusionService.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Capture;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Geometry;
using Blocwerk.Core.Geometry.Footprints;
using Blocwerk.Core.Geometry.View3D;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Services;

/// <summary>
/// Runs <see cref="HoldProtrusionEstimator"/> on a wall: its live placed holds (outlines as the 3D view
/// draws them), its active model's facets and its active splat (centres moved into the wall world by the
/// frame's matrix, fine alignment included); without a splat, the sparse points of the model's capture
/// (<see cref="SparseWorldPoints"/>, coarser). Writes only <see cref="Hold.ProtrusionMm"/>.
/// </summary>
public sealed class HoldProtrusionService(
    IDbContextFactory<BlocwerkDbContext> dbContextFactory,
    ILogger<HoldProtrusionService> logger,
    ICaptureFileStore? files = null) : IHoldProtrusionService
{
    /// <inheritdoc />
    public Task<HoldProtrusionRunResult?> MeasureFromPipelineAsync(Guid wallId, CancellationToken ct = default) =>
        RunSafelyAsync(wallId, null, ct);

    /// <inheritdoc />
    public Task<HoldProtrusionRunResult?> MeasureHoldsFromPipelineAsync(Guid wallId, IReadOnlyCollection<Guid> holdIds, CancellationToken ct = default) =>
        RunSafelyAsync(wallId, holdIds.ToHashSet(), ct);

    private async Task<HoldProtrusionRunResult?> RunSafelyAsync(Guid wallId, IReadOnlySet<Guid>? only, CancellationToken ct)
    {
        try
        {
            return await RunAsync(wallId, only, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Hold protrusion measurement on wall {WallId} failed", wallId);
            return null;
        }
    }

    private async Task<HoldProtrusionRunResult?> RunAsync(Guid wallId, IReadOnlySet<Guid>? only, CancellationToken ct)
    {
        if (files is null)
        {
            return null;
        }

        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        var model = await db.WallGeometryModels.AsNoTracking()
            .Where(m => m.WallId == wallId && m.IsActive).Select(m => new { m.Id, m.Json }).FirstOrDefaultAsync(ct);
        if (model is null || await EvidenceAsync(db, wallId, model.Id, model.Json, ct) is not { } evidence)
        {
            return null;
        }

        var json = model.Json;
        var doc = WallGeometryDocument.Parse(json);
        var frames = doc.Segments.SelectMany(s => s.Facets)
            .Select(f => (f.Id, Frame: FacetFrame.From(f)))
            .Where(f => !string.IsNullOrEmpty(f.Id) && f.Frame is not null)
            .ToDictionary(f => f.Id!, f => f.Frame!);
        var live = await (await LiveWallHolds.QueryAsync(db, wallId, ct)).AsNoTracking()
            .Where(h => h.FacetId != null && h.PlaneAMm != null && h.PlaneBMm != null)
            .ToListAsync(ct);
        var markers = await Wall3DPhotoMarkerLoader.LoadAsync(db, wallId, ct);
        var projector = HoldPlaneProjector.Create(live.Where(h => frames.ContainsKey(h.FacetId!)), doc, markers);
        var targets = live.Where(h => frames.ContainsKey(h.FacetId!) && (only is null || only.Contains(h.Id))).Select(h => new ProtrusionHold(
            h.Id, h.FacetId!, h.PlaneAMm!.Value, h.PlaneBMm!.Value, OutlineOf(h, projector), HoldFootprint.KeyOf(h))).ToList();
        var cameras = SolvedCamera.ParseAll(json).Select(c => c.Centre).ToList();
        var extents = doc.Segments.SelectMany(s => s.Facets)
            .Where(f => !string.IsNullOrEmpty(f.Id) && f.ExtentMm is not null)
            .ToDictionary(f => f.Id!, f => f.ExtentMm!.Value);
        var measured = await Task.Run(() => HoldProtrusionEstimator.Measure(evidence.Points, frames, targets, cameras, extents, evidence.Tuning), ct);
        var sparse = evidence.Tuning.Source == HoldProtrusionSource.Sparse;
        var written = await WriteAsync(db, wallId, measured, sparse, ct);
        var result = new HoldProtrusionRunResult(
            measured.Values.Count(p => p.Source == evidence.Tuning.Source),
            measured.Values.Count(p => p.Source == HoldProtrusionSource.Estimate),
            measured.Values.Count(p => p.OnVolume),
            written,
            sparse);
        logger.LogInformation(
            "Hold protrusion on wall {WallId} ({Source}): {Measured} measured, {Estimated} estimated, {OnVolumes} on volumes ({Moved} moved onto them), {Written} written",
            wallId, sparse ? "sparse points" : "photo-real view", result.Measured, result.Estimated, result.OnVolumes,
            measured.Values.Count(p => p.ShiftA != 0 || p.ShiftB != 0), result.Written);
        return result;
    }

    /// <summary>
    /// The points to measure in: the active photo-real view's splat centres when there is one (preferred), else the sparse
    /// points of the model's capture (<see cref="SparseWorldPoints"/>, coarser); null when there is neither.
    /// </summary>
    private async Task<(IReadOnlyList<(float X, float Y, float Z)> Points, HoldProtrusionTuning Tuning)?> EvidenceAsync(
        BlocwerkDbContext db, Guid wallId, Guid modelId, string json, CancellationToken ct)
    {
        var splat = await WallGeometrySplats.FindActiveAsync(db, wallId, ct);
        var matrix = splat is null ? null : CaptureSplatDocuments.WorldMatrix(splat.FrameJson);
        var spz = splat is null || matrix is null ? null : await files!.ReadAsync(splat.StoredPath, ct);
        if (matrix is not null && spz is not null)
        {
            return (SpzPoints.Read(spz, matrix), HoldProtrusionTuning.Splat);
        }

        return await SparseWorldPoints.LoadAsync(db, files!, modelId, json, logger, ct) is { } sparse
            ? (sparse.Points, HoldProtrusionTuning.Sparse)
            : null;
    }

    /// <summary>The outline the 3D view draws: the stored footprint, else the photo outline projected onto the facet.</summary>
    private static IReadOnlyList<double[]> OutlineOf(Hold hold, HoldPlaneProjector projector)
    {
        var isFoot = hold.Category == Enums.HoldCategory.Foot;
        var fallback = isFoot ? Wall3DViewBuilder.DefaultFootSizeMm : Wall3DViewBuilder.DefaultHandSizeMm;
        var shape = HoldShapeProjector.FromFootprint(HoldFootprint.For(hold))
            ?? HoldShapeProjector.Project(hold, hold.WidthMm ?? fallback, hold.HeightMm ?? fallback, projector.For(hold));
        return shape.Outline;
    }

    /// <summary>
    /// Stores the measurements on holds whose outline still matches. A coarser sparse-point run never replaces a value measured
    /// in a photo-real view for the same outline. Returns how many changed.
    /// </summary>
    private static async Task<int> WriteAsync(
        BlocwerkDbContext db, Guid wallId, Dictionary<Guid, HoldProtrusion> measured, bool sparse, CancellationToken ct)
    {
        var ids = measured.Keys.ToList();
        var holds = await db.Holds.Where(h => h.WallId == wallId && ids.Contains(h.Id)).ToListAsync(ct);
        var written = 0;
        foreach (var hold in holds)
        {
            var p = measured[hold.Id];
            var json = p.ToJson();
            var keep = sparse && HoldProtrusion.For(hold)?.Source == HoldProtrusionSource.Splat;
            if (!keep && p.OutlineKey == HoldFootprint.KeyOf(hold) && hold.ProtrusionMm != json)
            {
                hold.ProtrusionMm = json;
                written++;
            }
        }

        await db.SaveChangesAsync(ct);
        return written;
    }
}
