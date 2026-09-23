using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Data;
using Blocwerk.Core.Detection.Enrichment;
using Blocwerk.Core.Detection.Outlines;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Geometry;
using Blocwerk.Core.MarkerPlanning;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Services;

/// <summary>Which photos are live, which holds sit on them, and the per-photo outline + metric plan.</summary>
public sealed partial class HoldOutlineUpgradeService
{
    /// <summary>
    /// The wall's live photos: per (Col, Row) the newest committed panel row (the same rule the live viewers
    /// use), plus the legacy single-image photo when holds still sit on it without a panel.
    /// </summary>
    private static async Task<List<OutlineUpgradePhoto>> LoadLivePhotosAsync(BlocwerkDbContext db, Guid wallId, CancellationToken ct)
    {
        var panels = await db.WallPanels
            .AsNoTracking()
            .Where(p => p.WallId == wallId && p.Photo != null)
            .Select(p => new { p.Id, p.Col, p.Row, p.Generation })
            .ToListAsync(ct);
        var photos = panels
            .GroupBy(p => (p.Col, p.Row))
            .Select(g => g.OrderByDescending(p => p.Generation).ThenBy(p => p.Id).First())
            .OrderBy(p => p.Row).ThenBy(p => p.Col)
            .Select(p => new OutlineUpgradePhoto(p.Id, p.Generation))
            .ToList();

        var wall = await db.Walls.AsNoTracking()
            .Where(w => w.Id == wallId)
            .Select(w => new { HasPhoto = w.Photo != null, w.CurrentGeneration })
            .FirstOrDefaultAsync(ct) ?? throw new InvalidOperationException("Wall not found");
        if (wall.HasPhoto && await db.Holds.AnyAsync(
                h => h.WallId == wallId && h.WallPanelId == null && h.Generation == wall.CurrentGeneration, ct))
        {
            photos.Add(new OutlineUpgradePhoto(null, wall.CurrentGeneration));
        }

        return photos;
    }

    private static IQueryable<Hold> LiveHolds(BlocwerkDbContext db, Guid wallId, OutlineUpgradePhoto photo) =>
        photo.PanelId is { } panelId
            ? db.Holds.Where(h => h.WallPanelId == panelId && h.Generation == photo.Generation)
            : db.Holds.Where(h => h.WallId == wallId && h.WallPanelId == null && h.Generation == photo.Generation);

    /// <summary>
    /// Outlines one photo's circle holds (decoded once, off the caller's thread) and, with a metric context,
    /// measures the newly outlined ones. Null when nothing on the photo is eligible or it cannot be decoded.
    /// </summary>
    private async Task<OutlineUpgradePhotoPlan?> PlanPhotoAsync(
        BlocwerkDbContext db, Guid wallId, OutlineUpgradePhoto photo, bool includeManual, WallGeometryDocument? metricModel, CancellationToken ct)
    {
        var holds = await LiveHolds(db, wallId, photo).AsNoTracking().ToListAsync(ct);
        if (!holds.Any(h => HoldOutlineUpgradePlanner.IsEligible(h, includeManual)))
        {
            return null;
        }

        var bytes = photo.PanelId is { } panelId
            ? await db.WallPanels.Where(p => p.Id == panelId).Select(p => p.Photo).FirstOrDefaultAsync(ct)
            : await db.Walls.Where(w => w.Id == wallId).Select(w => w.Photo).FirstOrDefaultAsync(ct);
        if (bytes is null)
        {
            return null;
        }

        (List<HoldOutlineUpgradeProposal> Proposals, int Width, int Height) outlined;
        try
        {
            outlined = await Task.Run(() => Outline(bytes, holds, includeManual), ct);
        }
        catch (ArgumentException ex)
        {
            logger.LogWarning(ex, "Outline upgrade on wall {WallId}: photo of panel {PanelId} could not be decoded", wallId, photo.PanelId);
            return null;
        }

        var metrics = metricModel is null || photo.PanelId is null
            ? []
            : await MeasureAsync(db, wallId, photo, outlined.Proposals, metricModel, outlined.Width, outlined.Height, ct);
        return new OutlineUpgradePhotoPlan(outlined.Proposals, metrics);
    }

    private (List<HoldOutlineUpgradeProposal> Proposals, int Width, int Height) Outline(byte[] bytes, List<Hold> holds, bool includeManual)
    {
        using var session = outlineService!.OpenSession(bytes);
        return (HoldOutlineUpgradePlanner.Plan(session, holds, includeManual), session.ImageWidth, session.ImageHeight);
    }

    /// <summary>
    /// Millimetre sizes for the newly outlined holds, from the photo's STORED marker observations and the
    /// wall's active model — the ingest metric code, reused. Nothing without observations. Markers changed
    /// since the photo was taken (another plan revision than the model's) are left out.
    /// </summary>
    private static async Task<Dictionary<Hold, HoldMetric>> MeasureAsync(
        BlocwerkDbContext db,
        Guid wallId,
        OutlineUpgradePhoto photo,
        List<HoldOutlineUpgradeProposal> proposals,
        WallGeometryDocument model,
        int width,
        int height,
        CancellationToken ct)
    {
        var rows = await db.WallMarkerObservations
            .AsNoTracking()
            .Where(o => o.WallPanelId == photo.PanelId && o.PanelGeneration == photo.Generation && !o.FromStagedPhoto)
            .ToListAsync(ct);
        var revisions = await MarkerRevisionScope.LoadAsync(db, wallId, ct);
        var markers = HoldMetricPlanner.MarkersFromObservations(await revisions.FilterAsync(rows, ct), width, height);
        var outlined = proposals
            .Where(p => p.Outcome == HoldOutlineUpgradeOutcome.Outline)
            .ToDictionary(p => p.Hold, p => p.Result);
        return HoldMetricPlanner.Measure(outlined.Keys, outlined, markers, model, null, width, height);
    }

    /// <summary>
    /// The metric inputs, only for a marker wall with an active model while the marker kill switch is on.
    /// </summary>
    private async Task<WallGeometryDocument?> LoadMetricModelAsync(BlocwerkDbContext db, Guid wallId, CancellationToken ct)
    {
        var glyphs = await db.Walls.AsNoTracking().Where(w => w.Id == wallId).Select(w => w.GlyphsEnabled).FirstOrDefaultAsync(ct);
        if (!glyphs || !settings.MarkersEnabled)
        {
            return null;
        }

        return await HoldMetricPlanner.LoadActiveGeometryAsync(db, wallId, logger, ct);
    }
}
