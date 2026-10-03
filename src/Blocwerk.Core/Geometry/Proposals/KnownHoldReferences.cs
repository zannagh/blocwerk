// <copyright file="KnownHoldReferences.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;
using Blocwerk.Core.Geometry.View3D;
using Blocwerk.Core.Geometry.Volumes;

namespace Blocwerk.Core.Geometry.Proposals;

/// <summary>
/// Every live hold as a <see cref="KnownHoldReference"/> at its best known position: its volume point, else its
/// facet position (along its panel camera's ray when known), else its panel-photo centre mapped onto a facet the
/// way the 3D view places it (<see cref="Wall3DFallbackPlacement"/>: the photo's markers, or a fit to its placed
/// holds). Plus the printed markers, which the detector also sees as holds.
/// </summary>
public static class KnownHoldReferences
{
    /// <summary>Tolerance of a hold known only as a point (no panel camera), mm.</summary>
    public const double PointToleranceMm = 60;

    /// <summary>Largest hold size trusted for a tolerance, mm.</summary>
    public const double MaxSizeMm = 400;

    /// <summary>The references.</summary>
    /// <param name="live">The wall's live holds.</param>
    /// <param name="doc">The active model.</param>
    /// <param name="frames">Its facet frames by id.</param>
    /// <param name="cameras">Panel camera centres per photo (world mm).</param>
    /// <param name="markers">Marker observations per photo, or null.</param>
    /// <returns>One reference per locatable hold, then one per marker.</returns>
    public static List<KnownHoldReference> Build(
        IReadOnlyList<Hold> live,
        WallGeometryDocument doc,
        IReadOnlyDictionary<string, FacetFrame> frames,
        IReadOnlyDictionary<Wall3DPhotoKey, double[]> cameras,
        IReadOnlyDictionary<Wall3DPhotoKey, Wall3DPhotoMarkers>? markers)
    {
        var placed = live.Where(h => IsPlaced(h, frames)).ToList();
        var projector = HoldPlaneProjector.Create(placed, doc, markers);
        var extents = Wall3DFallbackPlacement.FacetExtents(doc);
        var result = placed
            .Select(h => ReferenceOf(h, frames[h.FacetId!], cameras.GetValueOrDefault(HoldPlaneProjector.PhotoOf(h))))
            .ToList();
        foreach (var h in live.Where(h => !IsPlaced(h, frames)))
        {
            if (Wall3DFallbackPlacement.Place(h, projector, extents, frames) is { } fit)
            {
                var camera = cameras.GetValueOrDefault(HoldPlaneProjector.PhotoOf(h));
                result.Add(Along(h.Id, fit.Frame, fit.Fit.PlaneAMm, fit.Fit.PlaneBMm, camera, Math.Max(PointToleranceMm, RadiusMm(h, fit.Fit))));
            }
        }

        result.AddRange(Markers(doc));
        return result;
    }

    private static bool IsPlaced(Hold h, IReadOnlyDictionary<string, FacetFrame> frames) =>
        h.FacetId is { } f && frames.ContainsKey(f) && h.PlaneAMm.HasValue && h.PlaneBMm.HasValue;

    /// <summary>The hold's larger side, capped against a wrong stored size.</summary>
    private static double SizeOf(Hold h) => Math.Min(MaxSizeMm, Math.Max(h.WidthMm ?? 50, h.HeightMm ?? 50));

    /// <summary>The hold's drawn radius through its photo mapping, mm (capped: an extrapolated mapping can blow up).</summary>
    private static double RadiusMm(Hold h, HoldPlaneFit fit)
    {
        var (a0, b0) = fit.Map(h.X, h.Y);
        var (a1, b1) = fit.Map(h.X + h.Radius, h.Y);
        var r = Math.Sqrt(((a1 - a0) * (a1 - a0)) + ((b1 - b0) * (b1 - b0)));
        return double.IsFinite(r) ? Math.Min(MaxSizeMm / 2, r) : 0;
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

    /// <summary>A placed hold: on its volume, else along its panel ray (or the facet normal) from its flat point.</summary>
    private static KnownHoldReference ReferenceOf(Hold h, FacetFrame frame, double[]? camera)
    {
        var size = SizeOf(h);
        var tolerance = Math.Max(HoldProposalFinder.MatchMm + (0.25 * size), size / 2);
        if (HoldVolumePlacement.FromJson(h.VolumePlacementJson) is { } p && p.Matches(h.PlaneAMm, h.PlaneBMm))
        {
            var on = HoldVolumePlacer.World(frame, p);
            return new KnownHoldReference(h.Id, on, on, tolerance);
        }

        var pointTolerance = Math.Max(PointToleranceMm + (0.25 * size), size / 2);
        return Along(h.Id, frame, h.PlaneAMm!.Value, h.PlaneBMm!.Value, camera, camera is null ? pointTolerance : tolerance);
    }

    /// <summary>
    /// The segment a hold drawn at (a, b) on the flat facet can stand on: towards the photo's camera, or straight
    /// out of the facet without one, up to <see cref="HoldProposalFinder.KnownRayMm"/> off the wall.
    /// </summary>
    private static KnownHoldReference Along(Guid id, FacetFrame frame, double a, double b, double[]? camera, double tolerance)
    {
        var f = frame.ToWorld(a, b);
        var n = frame.Normal;
        double[] d = camera is null ? [n[0], n[1], n[2]] : [camera[0] - f[0], camera[1] - f[1], camera[2] - f[2]];
        var len = Math.Sqrt((d[0] * d[0]) + (d[1] * d[1]) + (d[2] * d[2]));
        var cos = ((d[0] * n[0]) + (d[1] * n[1]) + (d[2] * n[2])) / len;
        if (!(cos > 0))
        {
            return new KnownHoldReference(id, f, f, tolerance);
        }

        var reach = HoldProposalFinder.KnownRayMm / Math.Max(0.2, cos) / len;
        return new KnownHoldReference(id, f, [f[0] + (reach * d[0]), f[1] + (reach * d[1]), f[2] + (reach * d[2])], tolerance);
    }
}
