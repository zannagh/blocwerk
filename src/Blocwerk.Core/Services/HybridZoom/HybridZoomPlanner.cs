// <copyright file="HybridZoomPlanner.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Geometry.HybridZoom;
using Blocwerk.Core.Geometry.View3D;

namespace Blocwerk.Core.Services.HybridZoom;

/// <summary>The close-up to render: the chosen photo, the panel rectangle it covers and the volumes cut out of it.</summary>
/// <param name="Choice">The chosen photo and its warp.</param>
/// <param name="Rect">The panel rectangle it covers.</param>
/// <param name="StoredPath">The photo's stored name.</param>
/// <param name="SourceWidth">The pixel width the choice's photo coordinates refer to.</param>
/// <param name="Holes">Volumes in the view, as polygons in view coordinates, left transparent (<see cref="CloseUpHoles"/>).</param>
internal sealed record ZoomPlan(
    CloseUpChoice Choice, ViewRect Rect, string StoredPath, int SourceWidth, IReadOnlyList<IReadOnlyList<(double U, double V)>>? Holes = null);

/// <summary>
/// Plans one close-up from loaded inputs. The view's centre decides the facet (<see cref="HoldPlaneProjector.Place"/>,
/// the same mapping the 3D view places a hold through); the view is then sampled through that facet's plane, and the
/// capture photos are ranked by <see cref="CloseUpCameraSelector"/>. A view no photo covers whole is retried smaller
/// about its centre (<see cref="ShrinkSteps"/>), so a request padded for panning still gets the part a photo shows.
/// A view mostly on a volume gets no close-up (the 3D view's job); volumes reaching into it, and the parts of it on
/// other facets (<see cref="CloseUpFacetMask"/>), are cut out of it.
/// </summary>
internal static class HybridZoomPlanner
{
    /// <summary>The scales a view is tried at, largest first.</summary>
    public static readonly double[] ShrinkSteps = [1.0, 0.8, 0.62];

    /// <summary>Samples per side of the volume test (denser than the camera grid: volumes are small).</summary>
    private const int VolumeSteps = 9;

    /// <summary>
    /// The plan, or why there is none, and the volume in the view (the 3D view's "look underneath" target): the one
    /// the view is on for <see cref="CloseUpStatus.OnVolume"/>, else the first one cut out of the close-up.
    /// </summary>
    public static (CloseUpStatus Status, ZoomPlan? Plan, VolumeSpot? Volume) Plan(
        ZoomInputs inputs, Guid panelId, ViewRect rect, CloseUpRules rules, double volumeShare)
    {
        var model = inputs.Model;
        var fit = inputs.Projector.Place(inputs.Probe(panelId, rect.CentreX, rect.CentreY), model.Extents);
        if (fit is null || !model.Frames.TryGetValue(fit.FacetId, out var frame))
        {
            return (CloseUpStatus.NoMapping, null, null);
        }

        var (onVolume, touching, spot) = VolumesInView(inputs, fit, rect, volumeShare);
        if (onVolume)
        {
            return (CloseUpStatus.OnVolume, null, spot);
        }

        var photos = Candidates(inputs).ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var step in ShrinkSteps)
        {
            var view = rect.Scaled(step);
            Func<double, double, double[]?> worldAt = (u, v) => WorldAt(view, u, v, fit.Map, frame);
            var panelPxPerMm = PanelPxPerMm(view, worldAt, inputs.PanelInfo.Width, inputs.PanelInfo.Height);
            if (panelPxPerMm is null)
            {
                return (CloseUpStatus.NoMapping, null, null);
            }

            var best = CloseUpCameraSelector.Rank(worldAt, frame.Normal, panelPxPerMm.Value, [.. photos.Values], rules).FirstOrDefault();
            if (best is not null)
            {
                var camera = photos[best.Name].Camera;
                var holes = CloseUpHoles.For(touching, frame, camera, best.ViewToPhoto)
                    .Concat(CloseUpFacetMask.OffFacet(camera, best.ViewToPhoto, fit.FacetId, model.Facets ?? []))
                    .ToList();
                var plan = new ZoomPlan(best, view, inputs.Photos[best.Name].StoredPath, camera.Width, holes);
                return (CloseUpStatus.Rendered, plan, spot);
            }
        }

        return (CloseUpStatus.NoBetterPhoto, null, null);
    }

    /// <summary>The panel photo's resolution over the view, px per mm; null when a corner does not map.</summary>
    public static double? PanelPxPerMm(ViewRect view, Func<double, double, double[]?> worldAt, int width, int height)
    {
        (double U, double V)[] corners = [(0, 0), (1, 0), (1, 1), (0, 1)];
        var world = corners.Select(c => worldAt(c.U, c.V)).ToList();
        if (world.Any(w => w is null))
        {
            return null;
        }

        var image = corners.Select(c => view.At(c.U, c.V)).Select(p => (p.X * width, p.Y * height)).ToList();
        var px = CloseUpCameraSelector.PxPerMm(image, world!);
        return px > 0 ? px : null;
    }

    /// <summary>Whether the view is mostly on a volume, the volumes reaching into it, and the one to look under.</summary>
    private static (bool OnVolume, List<VolumeOutline> Touching, VolumeSpot? Spot) VolumesInView(
        ZoomInputs inputs, HoldPlaneFit fit, ViewRect rect, double volumeShare)
    {
        if (!inputs.Volumes.TryGetValue(fit.FacetId, out var volumes) || volumes.Count == 0)
        {
            return (false, [], null);
        }

        var samples = PlaneSamples(rect, fit.Map, VolumeSteps);
        var outline = PlaneSamples(rect, fit.Map, 2);
        var (volumeId, share) = VolumeOverlap.Of(samples, volumes);
        var touching = VolumeOverlap.Touching(samples, outline.Count == 4 ? [outline[0], outline[1], outline[3], outline[2]] : [], volumes);
        var target = volumes.FirstOrDefault(v => v.Id == volumeId) ?? touching.FirstOrDefault();
        var spot = target is null ? null : SpotOf(target, fit.FacetId);
        return (target is not null && target.Id == volumeId && share >= volumeShare, touching, spot);
    }

    private static VolumeSpot SpotOf(VolumeOutline volume, string facetId)
    {
        var (a, b) = volume.Centroid;
        return new VolumeSpot(volume.Id, facetId, a, b, volume.HeightMm);
    }

    private static double[]? WorldAt(ViewRect view, double u, double v, PhotoToPlane map, FacetFrame frame)
    {
        var (x, y) = view.At(u, v);
        var (a, b) = map(x, y);
        return double.IsFinite(a) && double.IsFinite(b) ? frame.ToWorld(a, b) : null;
    }

    /// <summary>The view sampled on the plane, row by row; unmappable samples are left out.</summary>
    private static List<(double A, double B)> PlaneSamples(ViewRect view, PhotoToPlane map, int steps)
    {
        var result = new List<(double A, double B)>(steps * steps);
        for (var j = 0; j < steps; j++)
        {
            for (var i = 0; i < steps; i++)
            {
                var (x, y) = view.At((double)i / (steps - 1), (double)j / (steps - 1));
                var (a, b) = map(x, y);
                if (double.IsFinite(a) && double.IsFinite(b))
                {
                    result.Add((a, b));
                }
            }
        }

        return result;
    }

    private static List<CloseUpPhoto> Candidates(ZoomInputs inputs) =>
        inputs.Model.Cameras
            .Where(c => inputs.Photos.ContainsKey(c.Key))
            .Select(c =>
            {
                var photo = inputs.Photos[c.Key];
                var camera = photo.Width > 0 && photo.Height > 0 ? c.Value.ScaledTo(photo.Width, photo.Height) : c.Value;
                return new CloseUpPhoto(c.Key, camera, photo.Sharpness);
            })
            .Where(p => p.Camera.Width > 0 && p.Camera.Height > 0)
            .ToList();
}
