// <copyright file="ProposalInputs.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Geometry;
using Blocwerk.Core.Geometry.Footprints;
using Blocwerk.Core.Geometry.Proposals;
using Blocwerk.Core.Geometry.View3D;
using Blocwerk.Core.Geometry.Volumes;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Services;

/// <summary>
/// What a hold proposal run needs from the database: the model's facets with their visible volumes, every live
/// placed hold as a <see cref="KnownHoldReference"/>, and per panel photo its camera centre, its placed holds as
/// anchors and its size (for mapping a proposal into the photo, <see cref="PanelPointMapper"/>).
/// </summary>
/// <param name="Facets">The facets to cast onto.</param>
/// <param name="Known">The existing holds.</param>
/// <param name="Panels">The panel photos.</param>
public sealed record ProposalInputs(
    IReadOnlyList<CastFacet> Facets,
    IReadOnlyList<KnownHoldReference> Known,
    IReadOnlyList<(Guid PanelId, double[]? Camera, IReadOnlyList<PanelAnchor> Anchors, int Width, int Height)> Panels)
{
    /// <summary>Tolerance of a hold known only as a point (no panel camera), mm.</summary>
    private const double PointToleranceMm = 60;

    /// <summary>Loads the inputs.</summary>
    /// <param name="db">The database.</param>
    /// <param name="wallId">The wall.</param>
    /// <param name="modelId">Its active model.</param>
    /// <param name="doc">The model document.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The inputs.</returns>
    public static async Task<ProposalInputs> LoadAsync(BlocwerkDbContext db, Guid wallId, Guid modelId, WallGeometryDocument doc, CancellationToken ct)
    {
        var volumes = (await db.WallVolumes.AsNoTracking().Where(v => v.GeometryModelId == modelId && !v.IsHidden).ToListAsync(ct))
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

        var live = await (await LiveWallHolds.QueryAsync(db, wallId, ct)).AsNoTracking()
            .Where(h => h.FacetId != null && h.PlaneAMm != null && h.PlaneBMm != null).ToListAsync(ct);
        var placed = live.Where(h => frames.ContainsKey(h.FacetId!)).ToList();
        var flat = placed.Where(h => h.VolumePlacementJson is null).ToList();
        var photos = await PanelPhotoInfoLoader.LoadAsync(db, wallId, placed.Select(HoldPlaneProjector.PhotoOf), ct);
        var cams = HoldFootprintRefiner.PanelCameras(flat, frames, photos);
        var known = placed.Select(h => ReferenceOf(h, frames[h.FacetId!], cams.GetValueOrDefault(HoldPlaneProjector.PhotoOf(h))))
            .Concat(Markers(doc))
            .ToList();
        var panels = flat.Where(h => h.WallPanelId is not null)
            .GroupBy(HoldPlaneProjector.PhotoOf)
            .Where(g => photos.ContainsKey(g.Key))
            .Select(g => (g.Key.PanelId!.Value, cams.GetValueOrDefault(g.Key),
                (IReadOnlyList<PanelAnchor>)g.Select(h => new PanelAnchor(h.FacetId!, h.PlaneAMm!.Value, h.PlaneBMm!.Value, h.X, h.Y)).ToList(),
                photos[g.Key].Width, photos[g.Key].Height))
            .ToList();
        return new ProposalInputs(facets, known, panels);
    }

    /// <summary>The printed markers as known "holds": the detector sees their black squares as holds.</summary>
    private static IEnumerable<KnownHoldReference> Markers(WallGeometryDocument doc) =>
        doc.Markers
            .Where(m => m.CornersWorldMm is { Count: 4 } c && c.All(p => p.Length == 3))
            .Select(m =>
            {
                var c = m.CornersWorldMm!;
                double[] centre = [c.Average(p => p[0]), c.Average(p => p[1]), c.Average(p => p[2])];
                return new KnownHoldReference(Guid.Empty, centre, centre, 0.8 * doc.MarkerSizeMm);
            });

    /// <summary>A hold as a known reference: on its volume, along its panel ray, or its flat point.</summary>
    private static KnownHoldReference ReferenceOf(Hold h, FacetFrame frame, double[]? camera)
    {
        var size = Math.Max(h.WidthMm ?? 50, h.HeightMm ?? 50);
        var tolerance = HoldProposalFinder.MatchMm + (0.25 * size);
        if (HoldVolumePlacement.FromJson(h.VolumePlacementJson) is { } p && p.Matches(h.PlaneAMm, h.PlaneBMm))
        {
            var on = HoldVolumePlacer.World(frame, p);
            return new KnownHoldReference(h.Id, on, on, tolerance);
        }

        var f = frame.ToWorld(h.PlaneAMm!.Value, h.PlaneBMm!.Value);
        if (camera is null)
        {
            return new KnownHoldReference(h.Id, f, f, PointToleranceMm + (0.25 * size));
        }

        double[] d = [camera[0] - f[0], camera[1] - f[1], camera[2] - f[2]];
        var len = Math.Sqrt((d[0] * d[0]) + (d[1] * d[1]) + (d[2] * d[2]));
        var cos = ((d[0] * frame.Normal[0]) + (d[1] * frame.Normal[1]) + (d[2] * frame.Normal[2])) / len;
        var reach = HoldProposalFinder.KnownRayMm / Math.Max(0.2, cos) / len;
        return new KnownHoldReference(h.Id, f, [f[0] + (reach * d[0]), f[1] + (reach * d[1]), f[2] + (reach * d[2])], tolerance);
    }
}
