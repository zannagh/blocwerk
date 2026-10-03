// <copyright file="ProposalInputs.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Data;
using Blocwerk.Core.Geometry;
using Blocwerk.Core.Geometry.Footprints;
using Blocwerk.Core.Geometry.Proposals;
using Blocwerk.Core.Geometry.View3D;
using Blocwerk.Core.Geometry.Volumes;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Services;

/// <summary>
/// What a hold proposal run needs from the database: the model's facets with their visible volumes, every live
/// hold as a <see cref="KnownHoldReference"/> (<see cref="KnownHoldReferences"/>), and per panel photo its camera
/// centre, its placed holds as anchors and its size (for mapping a proposal into the photo, <see cref="PanelPointMapper"/>).
/// </summary>
/// <param name="Facets">The facets to cast onto.</param>
/// <param name="Known">The existing holds.</param>
/// <param name="Panels">The panel photos.</param>
public sealed record ProposalInputs(
    IReadOnlyList<CastFacet> Facets,
    IReadOnlyList<KnownHoldReference> Known,
    IReadOnlyList<(Guid PanelId, double[]? Camera, IReadOnlyList<PanelAnchor> Anchors, int Width, int Height)> Panels)
{
    /// <summary>Loads the inputs.</summary>
    /// <param name="db">The database.</param>
    /// <param name="wallId">The wall.</param>
    /// <param name="modelId">Its active model.</param>
    /// <param name="doc">The model document.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The inputs.</returns>
    public static async Task<ProposalInputs> LoadAsync(BlocwerkDbContext db, Guid wallId, Guid modelId, WallGeometryDocument doc, CancellationToken ct)
    {
        var volumes = (await db.WallVolumes.AsNoTracking().Where(v => v.GeometryModelId == modelId && !v.IsHidden && !v.IsRemoved).ToListAsync(ct))
            .Select(v => (v.FacetId, Surface: VolumeSurface.FromJson(v.SurfaceJson)))
            .Where(v => v.Surface is not null)
            .ToList();
        var facets = new List<CastFacet>();
        var frames = new Dictionary<string, FacetFrame>(StringComparer.Ordinal);
        foreach (var f in doc.Segments.SelectMany(s => s.Facets))
        {
            if (!string.IsNullOrEmpty(f.Id) && FacetFrame.From(f) is { } frame && f.ExtentMm is { } extent)
            {
                frames[f.Id] = frame;
                facets.Add(new CastFacet(f.Id, frame, extent, volumes.Where(v => v.FacetId == f.Id).Select(v => v.Surface!).ToList()));
            }
        }

        var live = await (await LiveWallHolds.QueryAsync(db, wallId, ct)).AsNoTracking().ToListAsync(ct);
        var placed = live.Where(h => h.FacetId is { } f && frames.ContainsKey(f) && h.PlaneAMm != null && h.PlaneBMm != null).ToList();
        var flat = placed.Where(h => h.VolumePlacementJson is null).ToList();
        var photos = await PanelPhotoInfoLoader.LoadAsync(db, wallId, placed.Select(HoldPlaneProjector.PhotoOf), ct);
        var cams = HoldFootprintRefiner.PanelCameras(flat, frames, photos);
        var markers = await Wall3DPhotoMarkerLoader.LoadAsync(db, wallId, ct);
        var known = KnownHoldReferences.Build(live, doc, frames, cams, markers);
        var panels = flat.Where(h => h.WallPanelId is not null)
            .GroupBy(HoldPlaneProjector.PhotoOf)
            .Where(g => photos.ContainsKey(g.Key))
            .Select(g => (g.Key.PanelId!.Value, cams.GetValueOrDefault(g.Key),
                (IReadOnlyList<PanelAnchor>)g.Select(h => new PanelAnchor(h.FacetId!, h.PlaneAMm!.Value, h.PlaneBMm!.Value, h.X, h.Y)).ToList(),
                photos[g.Key].Width, photos[g.Key].Height))
            .ToList();
        return new ProposalInputs(facets, known, panels);
    }
}
