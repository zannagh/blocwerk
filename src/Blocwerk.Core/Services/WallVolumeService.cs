// <copyright file="WallVolumeService.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Diagnostics;
using System.Text.Json;
using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Capture;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Geometry;
using Blocwerk.Core.Geometry.View3D;
using Blocwerk.Core.Geometry.Volumes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Services;

/// <summary>
/// <see cref="IWallVolumeService"/>: loads the active model, its splat (surface-like centres incl. the wide flat
/// splats of smooth faces) and the live holds, runs <see cref="VolumeDetector"/>, replaces the model's
/// <see cref="WallVolume"/> rows (a volume found again keeps its hidden flag) and places the holds
/// (<see cref="HoldVolumePlacer"/>). Writes only <see cref="WallVolume"/> rows and <see cref="Hold.VolumePlacementJson"/>.
/// </summary>
public sealed partial class WallVolumeService(
    IDbContextFactory<BlocwerkDbContext> dbContextFactory,
    ICurrentUserService currentUserService,
    ILogger<WallVolumeService> logger,
    ICaptureFileStore? files = null,
    IKioskContext? kioskContext = null) : IWallVolumeService
{
    private const string KioskRefusal = "Finding volumes";

    /// <summary>A re-detected volume keeps the hidden flag of an old one whose footprint centre is this near, mm.</summary>
    private const double SameVolumeMm = 120;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <inheritdoc />
    public async Task<WallVolumeRunResult?> DetectFromPipelineAsync(Guid wallId, CancellationToken ct = default)
    {
        try
        {
            return await RunAsync(wallId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Volume detection on wall {WallId} failed", wallId);
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<WallVolumeRunResult> DetectAsync(Guid wallId, CancellationToken ct = default)
    {
        await EnsureAdminAsync(wallId, ct);
        return await RunAsync(wallId, ct)
            ?? throw new UserFacingException("This wall has no active 3D model with a photo-real scene to find volumes in.");
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<WallVolumeSummary>> ListAsync(Guid wallId, CancellationToken ct = default)
    {
        await EnsureAdminAsync(wallId, ct);
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        var modelId = await ActiveModelIdAsync(db, wallId, ct);
        return await db.WallVolumes.AsNoTracking()
            .Where(v => v.GeometryModelId == modelId)
            .OrderBy(v => v.Index)
            .Select(v => new WallVolumeSummary(v.Id, v.Index, v.FacetId, v.AreaM2, v.HeightMm, v.Confidence, v.HoldCount, v.IsHidden))
            .ToListAsync(ct);
    }

    /// <inheritdoc />
    public async Task<WallVolumeRunResult> SetHiddenAsync(Guid wallId, Guid volumeId, bool hidden, CancellationToken ct = default)
    {
        await EnsureAdminAsync(wallId, ct);
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        var volume = await db.WallVolumes.FirstOrDefaultAsync(v => v.Id == volumeId && v.WallId == wallId, ct)
            ?? throw new UserFacingException("That volume does not exist on this wall.");
        volume.IsHidden = hidden;
        await db.SaveChangesAsync(ct);
        var (placed, changed) = await PlaceHoldsAsync(db, wallId, volume.GeometryModelId, ct);
        var total = await db.WallVolumes.CountAsync(v => v.GeometryModelId == volume.GeometryModelId, ct);
        return new WallVolumeRunResult(total, 0, placed, changed);
    }

    private static Task<Guid?> ActiveModelIdAsync(BlocwerkDbContext db, Guid wallId, CancellationToken ct) =>
        db.WallGeometryModels.AsNoTracking().Where(m => m.WallId == wallId && m.IsActive).Select(m => (Guid?)m.Id).FirstOrDefaultAsync(ct);

    private async Task EnsureAdminAsync(Guid wallId, CancellationToken ct)
    {
        var user = await currentUserService.GetCurrentUserAsync();
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        db.CurrentUserId = user.Id;
        KioskGuard.EnsureNotKiosk(kioskContext, db, KioskRefusal);
        await WallAdminGuard.EnsureWallAdminAsync(db, wallId, user.Id, ct);
    }

    private async Task<WallVolumeRunResult?> RunAsync(Guid wallId, CancellationToken ct)
    {
        if (files is null)
        {
            return null;
        }

        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        var model = await db.WallGeometryModels.AsNoTracking()
            .Where(m => m.WallId == wallId && m.IsActive).Select(m => new { m.Id, m.Json }).FirstOrDefaultAsync(ct);
        var splat = await WallGeometrySplats.FindActiveAsync(db, wallId, ct);
        var matrix = splat is null ? null : CaptureSplatDocuments.WorldMatrix(splat.FrameJson);
        var spz = splat is null ? null : await files.ReadAsync(splat.StoredPath, ct);
        if (model is null || matrix is null || spz is null)
        {
            return null;
        }

        var watch = Stopwatch.StartNew();
        var doc = WallGeometryDocument.Parse(model.Json);
        var (frames, extents) = FacetsOf(doc);
        var live = await (await LiveWallHolds.QueryAsync(db, wallId, ct)).AsNoTracking()
            .Where(h => h.FacetId != null && h.PlaneAMm != null && h.PlaneBMm != null).ToListAsync(ct);
        var holds = KnownHolds(live, frames);
        var found = await Task.Run(() => VolumeDetector.Detect(SpzPoints.Read(spz, matrix, includeFlat: true), frames, extents, holds), ct);
        var accepted = found.Where(v => v.IsAccepted).ToList();
        await ReplaceVolumesAsync(db, wallId, model.Id, accepted, ct);
        var (placed, changed) = await PlaceHoldsAsync(db, wallId, model.Id, ct);
        logger.LogInformation(
            "Volumes on wall {WallId}: {Accepted} found ({Rejected} raised candidates rejected), {Placed} holds on them ({Changed} changed), {Ms} ms",
            wallId, accepted.Count, found.Count - accepted.Count, placed, changed, watch.ElapsedMilliseconds);
        return new WallVolumeRunResult(accepted.Count, found.Count - accepted.Count, placed, changed);
    }

    /// <summary>The model's facet frames and extents.</summary>
    private static (Dictionary<string, FacetFrame> Frames, Dictionary<string, PlaneRectMm> Extents) FacetsOf(WallGeometryDocument doc)
    {
        var frames = new Dictionary<string, FacetFrame>(StringComparer.Ordinal);
        var extents = new Dictionary<string, PlaneRectMm>(StringComparer.Ordinal);
        foreach (var f in doc.Segments.SelectMany(s => s.Facets))
        {
            if (!string.IsNullOrEmpty(f.Id) && FacetFrame.From(f) is { } frame && f.ExtentMm is { } extent)
            {
                frames[f.Id] = frame;
                extents[f.Id] = extent;
            }
        }

        return (frames, extents);
    }

    private static Dictionary<string, List<KnownHoldEllipse>> KnownHolds(List<Hold> live, Dictionary<string, FacetFrame> frames)
    {
        const double fallbackMm = 60;
        return live.Where(h => frames.ContainsKey(h.FacetId!))
            .GroupBy(h => h.FacetId!, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => g.Select(h => new KnownHoldEllipse(
                    h.PlaneAMm!.Value, h.PlaneBMm!.Value, (h.WidthMm ?? fallbackMm) / 2, (h.HeightMm ?? fallbackMm) / 2)).ToList(),
                StringComparer.Ordinal);
    }

    private async Task ReplaceVolumesAsync(BlocwerkDbContext db, Guid wallId, Guid modelId, List<DetectedVolume> accepted, CancellationToken ct)
    {
        var old = await db.WallVolumes.Where(v => v.GeometryModelId == modelId).ToListAsync(ct);
        var hidden = old.Where(v => v.IsHidden).Select(v => (v.FacetId, Centre: Centre(Footprint(v.FootprintJson)))).ToList();
        db.WallVolumes.RemoveRange(old);
        var index = 0;
        foreach (var v in accepted.OrderBy(v => v.FacetId, StringComparer.Ordinal).ThenBy(v => v.Footprint.Average(p => p.A)))
        {
            var centre = Centre(v.Footprint);
            db.WallVolumes.Add(new WallVolume
            {
                WallId = wallId,
                GeometryModelId = modelId,
                FacetId = v.FacetId,
                Index = ++index,
                FootprintJson = JsonSerializer.Serialize(v.Footprint.Select(p => new[] { Math.Round(p.A, 1), Math.Round(p.B, 1) }), Json),
                SurfaceJson = v.Surface!.ToJson(),
                AreaM2 = v.AreaM2,
                HeightMm = v.HeightMm,
                Confidence = v.Confidence,
                IsHidden = hidden.Any(h => h.FacetId == v.FacetId && Distance(h.Centre, centre) < SameVolumeMm),
            });
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>A stored footprint; empty when malformed.</summary>
    private static List<(double A, double B)> Footprint(string json)
    {
        try
        {
            return (JsonSerializer.Deserialize<double[][]>(json, Json) ?? [])
                .Where(p => p.Length == 2).Select(p => (p[0], p[1])).ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static (double A, double B) Centre(IReadOnlyList<(double A, double B)> ring) =>
        ring.Count == 0 ? (0, 0) : (ring.Average(p => p.A), ring.Average(p => p.B));

    private static double Distance((double A, double B) p, (double A, double B) q) =>
        Math.Sqrt(((p.A - q.A) * (p.A - q.A)) + ((p.B - q.B) * (p.B - q.B)));
}
