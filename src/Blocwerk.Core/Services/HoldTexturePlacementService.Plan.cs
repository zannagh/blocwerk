using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Capture;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Geometry;
using Blocwerk.Core.Geometry.TextureRegistration;
using Blocwerk.Core.Geometry.View3D;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Services;

/// <summary>Planning: the active model and its textures, the live holds per panel photo, and the registrations.</summary>
public sealed partial class HoldTexturePlacementService
{
    /// <summary>The wall's live holds (<see cref="LiveWallHolds"/>): the live panels' holds, never a superseded panel's.</summary>
    private static Task<IQueryable<Hold>> LiveHoldsQueryAsync(BlocwerkDbContext db, Guid wallId, CancellationToken ct) =>
        LiveWallHolds.QueryAsync(db, wallId, ct);

    /// <summary>The active model: its facets and its facet textures (with their extents and 3D frames), read from the capture store.</summary>
    private async Task<ActiveModel> LoadActiveModelAsync(BlocwerkDbContext db, Guid wallId, CancellationToken ct)
    {
        var model = await ActiveModelTextures.FindAsync(db, wallId, ct)
            ?? throw new UserFacingException("This wall has no active 3D model to place the holds on.");
        var loaded = await ActiveModelTextures.LoadAsync(db, files!, model.Id, model.Json, logger, ct);
        if (loaded.Textures.Count == 0)
        {
            throw new UserFacingException("The active 3D model has no facet textures yet, so there is nothing to match the photos to.");
        }

        return loaded;
    }

    /// <summary>The model's facet extents and 3D frames by facet id (empty when it does not parse).</summary>
    private (Dictionary<string, PlaneRectMm> Extents, Dictionary<string, FacetFrame> Frames) Facets(string json) =>
        ActiveModelTextures.Facets(json, logger);

    /// <summary>
    /// Registers one panel photo onto every texture (seeding missed and weak facets from <paramref name="anchors"/>) and plans
    /// its eligible holds. <paramref name="direct"/>: the photo's registrations from an earlier pass, reused as the starting point.
    /// </summary>
    private async Task<PanelPlan> PlanPanelAsync(
        BlocwerkDbContext db,
        Guid panelId,
        List<Hold> holds,
        List<RegistrationTexture> textures,
        List<PlaneAnchor> anchors,
        CancellationToken ct,
        IReadOnlyList<FacetRegistration>? direct = null)
    {
        var panel = await db.WallPanels.AsNoTracking().Where(p => p.Id == panelId)
            .Select(p => new { p.Col, p.Row, p.Generation, p.Photo, p.PhotoRevision }).FirstOrDefaultAsync(ct);
        var eligible = holds.Where(HoldTexturePlacer.IsEligible).ToList();
        var skipped = holds.Count - eligible.Count;
        var (col, row) = (panel?.Col ?? 0, panel?.Row ?? 0);
        if (eligible.Count == 0 || panel?.Photo is null)
        {
            var problem = eligible.Count == 0 ? null : "the panel has no photo";
            return new PanelPlan(new HoldPlacementPanelSummary(panelId, col, row, 0, skipped, eligible.Count, [], problem), []);
        }

        List<FacetRegistration> registrations;
        try
        {
            registrations = await Task.Run(() => Register(panel.Photo, textures, $"c{col} r{row}", anchors, direct, ct), ct);
        }
        catch (ArgumentException ex)
        {
            logger.LogWarning(ex, "Panel {PanelId}: the photo could not be decoded", panelId);
            return new PanelPlan(new HoldPlacementPanelSummary(panelId, col, row, 0, skipped, eligible.Count, [], "the photo could not be decoded"), []);
        }

        var placements = new List<PlannedPlacement>();
        foreach (var hold in eligible)
        {
            if (HoldTexturePlacer.Place(hold, registrations) is { } fit)
            {
                placements.Add(new PlannedPlacement(hold, fit, HoldTexturePlacer.Measure(hold, fit)));
            }
        }

        var facets = registrations.Select(PanelSummaries.Of).ToList();
        var none = registrations.Any(r => r.Accepted) ? null : "the photo matched none of the model's textures";
        var summary = new HoldPlacementPanelSummary(
            panelId, col, row, placements.Count, skipped, eligible.Count - placements.Count, facets, none);
        return new PanelPlan(summary, placements, registrations, Photo: new PanelPhotoStamp(panel.Generation, panel.Photo.Length, panel.PhotoRevision));
    }

    /// <summary>One matcher session for the photo, registered onto every texture (CPU-bound; run off the request thread).</summary>
    private List<FacetRegistration> Register(
        byte[] photo,
        List<RegistrationTexture> textures,
        string label,
        List<PlaneAnchor> anchors,
        IReadOnlyList<FacetRegistration>? direct,
        CancellationToken ct)
    {
        using var session = matcher!.OpenPhoto(photo);
        var focal = ExifCameraReader.Read(photo).Focal35mm is { } f35 ? f35 / 36.0 * Math.Max(session.Width, session.Height) : (double?)null;
        return new PhotoRegistrar(session, logger, label, focal, anchors, direct).RegisterAll(textures, ct);
    }
}
