// <copyright file="CaptureCoverageService.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Text.Json;
using System.Text.Json.Nodes;
using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Data;
using Blocwerk.Core.Geometry;
using Blocwerk.Core.Geometry.Volumes;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Capture.Coverage;

/// <summary>
/// Loads a capture's model (solved photo cameras), its photo-real frame (registered video frames and their poses,
/// when reported), the model's visible volumes and the placed holds, computes the coverage report and stores it on
/// the capture. Reading it is for wall admins only.
/// </summary>
public sealed class CaptureCoverageService(
    IDbContextFactory<BlocwerkDbContext> dbContextFactory,
    ICurrentUserService currentUserService,
    ILogger<CaptureCoverageService> logger,
    IKioskContext? kioskContext = null,
    TimeProvider? clock = null) : ICaptureCoverageService
{
    private const string AdminAction = "Reading a capture's coverage";

    /// <inheritdoc />
    public async Task<CaptureCoverageReport?> ComputeFromPipelineAsync(Guid captureId, CancellationToken ct = default)
    {
        var inputs = await LoadAsync(captureId, ct);
        if (inputs is null)
        {
            return null;
        }

        var now = (clock ?? TimeProvider.System).GetUtcNow();
        var report = await Task.Run(() => CaptureCoverageAnalyzer.Analyze(inputs, now), ct);
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        var capture = await db.WallCaptures.FirstAsync(c => c.Id == captureId, ct);
        capture.CoverageJson = report.ToJson();
        await db.SaveChangesAsync(ct);
        logger.LogInformation(
            "Capture {CaptureId}: coverage from {Photos} photos and {Frames} video frames, {Advice} tips for the next capture",
            captureId, report.PhotoViews, report.VideoViews, report.Advice.Count);
        return report;
    }

    /// <inheritdoc />
    public async Task<CaptureCoverageLookup> GetAsync(Guid wallId, Guid captureId, CancellationToken ct = default)
    {
        var user = await currentUserService.GetCurrentUserAsync();
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        db.CurrentUserId = user.Id;
        KioskGuard.EnsureNotKiosk(kioskContext, db, AdminAction);
        await WallAdminGuard.EnsureWallAdminAsync(db, wallId, user.Id, ct);
        var row = await db.WallCaptures.AsNoTracking()
            .Where(c => c.Id == captureId && c.WallId == wallId)
            .Select(c => new { c.CoverageJson, c.Status, c.GeometryModelId })
            .FirstOrDefaultAsync(ct);
        if (row is null)
        {
            return new CaptureCoverageLookup(false, null);
        }

        // A capture done before the report existed gets it on first read (seconds, derived data only).
        var report = CaptureCoverageReport.Parse(row.CoverageJson);
        if (report is null && row.GeometryModelId is not null && IsDone(row.Status))
        {
            report = await ComputeSafelyAsync(captureId, ct);
        }

        return new CaptureCoverageLookup(true, report);
    }

    /// <summary>The frames the photo-real stage placed, from its <c>frame.json</c> stats; null when not reported.</summary>
    /// <param name="frameJson">The frame JSON.</param>
    /// <returns>The count.</returns>
    public static int? RegisteredFrames(string? frameJson)
    {
        try
        {
            return string.IsNullOrWhiteSpace(frameJson) ? null
                : JsonNode.Parse(frameJson)?["stats"]?["videoFramesRegistered"] is JsonValue v && v.TryGetValue<int>(out var n) ? n : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<CaptureCoverageReport?> ComputeSafelyAsync(Guid captureId, CancellationToken ct)
    {
        try
        {
            return await ComputeFromPipelineAsync(captureId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Capture {CaptureId}: the coverage report could not be computed", captureId);
            return null;
        }
    }

    private static bool IsDone(Entities.WallCaptureStatus status) =>
        status is Entities.WallCaptureStatus.Succeeded or Entities.WallCaptureStatus.SucceededWithoutTextures
            or Entities.WallCaptureStatus.SucceededWithoutSplat;

    private async Task<CoverageInputs?> LoadAsync(Guid captureId, CancellationToken ct)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(ct);
        var capture = await db.WallCaptures.AsNoTracking()
            .Where(c => c.Id == captureId)
            .Select(c => new { c.WallId, c.GeometryModelId, c.VideoFileName, c.VideoStoredPath, c.VideoFramesJson })
            .FirstOrDefaultAsync(ct);
        var modelJson = capture?.GeometryModelId is { } modelId
            ? await db.WallGeometryModels.AsNoTracking().Where(m => m.Id == modelId).Select(m => m.Json).FirstOrDefaultAsync(ct)
            : null;
        if (capture is null || modelJson is null)
        {
            return null;
        }

        var model = capture.GeometryModelId!.Value;
        var frameJson = await db.WallGeometrySplats.AsNoTracking()
            .Where(s => s.GeometryModelId == model)
            .OrderByDescending(s => s.CreatedAt)
            .Select(s => s.FrameJson)
            .FirstOrDefaultAsync(ct);
        var frames = CaptureVideoFiles.Frames(capture.VideoFramesJson).Count;
        var video = new CoverageVideoInput(
            capture.VideoFileName is not null || capture.VideoStoredPath is not null || frames > 0, frames, RegisteredFrames(frameJson));
        return new CoverageInputs(
            captureId,
            model,
            WallGeometryDocument.Parse(modelJson),
            CoverageCamera.FromModel(modelJson),
            CoverageCamera.FromSplatFrame(frameJson),
            await VolumesAsync(db, model, ct),
            await HoldBoundsAsync(db, capture.WallId, ct),
            video);
    }

    private static async Task<IReadOnlyList<CoverageVolume>> VolumesAsync(BlocwerkDbContext db, Guid modelId, CancellationToken ct)
    {
        var rows = await db.WallVolumes.AsNoTracking()
            .Where(v => v.GeometryModelId == modelId && !v.IsHidden && !v.IsRemoved)
            .OrderBy(v => v.Index)
            .Select(v => new { v.Index, v.FacetId, v.SurfaceJson, v.FootprintJson })
            .ToListAsync(ct);
        return rows
            .Select(v => VolumeSurface.FromJson(v.SurfaceJson) is { } surface
                ? new CoverageVolume(v.Index, v.FacetId, surface, Footprint(v.FootprintJson))
                : null)
            .OfType<CoverageVolume>()
            .ToList();
    }

    private static async Task<IReadOnlyDictionary<string, PlaneRectMm>> HoldBoundsAsync(BlocwerkDbContext db, Guid wallId, CancellationToken ct)
    {
        var live = await LiveWallHolds.QueryAsync(db, wallId, ct);
        var points = await live.AsNoTracking()
            .Where(h => h.FacetId != null && h.PlaneAMm != null && h.PlaneBMm != null)
            .Select(h => new { h.FacetId, h.PlaneAMm, h.PlaneBMm })
            .ToListAsync(ct);
        return points.GroupBy(p => p.FacetId!)
            .Select(g => (g.Key, Bounds: PlaneRectMm.Bounds(g.Select(p => (p.PlaneAMm!.Value, p.PlaneBMm!.Value)))))
            .Where(x => x.Bounds is not null)
            .ToDictionary(x => x.Key, x => x.Bounds!.Value, StringComparer.Ordinal);
    }

    private static IReadOnlyList<double[]> Footprint(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<double[][]>(json)?.Where(p => p.Length >= 2).ToList() ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
