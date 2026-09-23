// <copyright file="HoldFootprintRefiner.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Geometry.View3D;

namespace Blocwerk.Core.Geometry.Footprints;

/// <summary>How many holds a refinement gave which kind of footprint.</summary>
/// <param name="Footprints">The footprint per hold id.</param>
/// <param name="MultiView">Holds intersected from ≥ 2 views.</param>
/// <param name="SingleView">Holds that fell back to the single-view correction.</param>
/// <param name="Skipped">Traced holds with no mapping or no panel camera.</param>
public sealed record HoldFootprintRefinement(IReadOnlyDictionary<Guid, HoldFootprint> Footprints, int MultiView, int SingleView, int Skipped);

/// <summary>
/// Batch step: for every placed, traced hold, its silhouette in the panel photo (mapped onto the facet
/// the way the 3D view maps it) plus its silhouettes in the capture photos that see it (the capture's
/// solved cameras locate the hold there; the outline segmenter traces it, seeded at that spot), turned
/// into a footprint by <see cref="HoldFootprintEstimator"/>. The panel photo's camera is resected from
/// its placed holds. No I/O: photos are opened through a callback, one at a time.
/// </summary>
public static class HoldFootprintRefiner
{
    /// <summary>Capture views steeper than this (to the facet normal) are not traced.</summary>
    public const double MaxCaptureThetaDeg = 75;

    /// <summary>A hold must project at least this far (fraction) inside a capture photo.</summary>
    public const double FrameMargin = 0.03;

    /// <summary>Refines every eligible hold of a wall.</summary>
    /// <param name="live">The wall's live holds.</param>
    /// <param name="doc">Its active geometry model.</param>
    /// <param name="cameras">The model's solved capture cameras (may be empty: single-view only).</param>
    /// <param name="open">Opens one capture photo's outline session (at the photo's real size), or null.</param>
    /// <param name="projector">The panel-photo → facet mappings the 3D view uses.</param>
    /// <returns>The refinement.</returns>
    public static HoldFootprintRefinement Refine(
        IReadOnlyList<Hold> live,
        WallGeometryDocument doc,
        IReadOnlyList<SolvedCamera> cameras,
        Func<SolvedCamera, IHoldOutlineSession?> open,
        HoldPlaneProjector projector)
    {
        var frames = Frames(doc);
        var panelCams = PanelCameras(live, frames);
        var primaries = new Dictionary<Guid, (Hold Hold, FacetFrame Frame, FootprintView View)>();
        var skipped = 0;
        foreach (var hold in live)
        {
            if (Primary(hold, frames, panelCams, projector) is { } p)
            {
                primaries[hold.Id] = (hold, p.Frame, p.View);
            }
            else if (hold.ShapePoints is { Count: >= 3 })
            {
                skipped++;
            }
        }

        var others = primaries.Keys.ToDictionary(id => id, _ => new List<FootprintView>());
        foreach (var camera in cameras)
        {
            TraceIn(camera, open, primaries.Values, others);
        }

        var result = new Dictionary<Guid, HoldFootprint>();
        foreach (var (id, (hold, frame, view)) in primaries)
        {
            var fp = HoldFootprintEstimator.Estimate(frame, (hold.PlaneAMm!.Value, hold.PlaneBMm!.Value), view, others[id], HoldFootprint.KeyOf(hold));
            if (fp is not null)
            {
                result[id] = fp;
            }
        }

        var multi = result.Values.Count(f => f.Source == HoldFootprintSource.MultiView);
        return new HoldFootprintRefinement(result, multi, result.Count - multi, skipped + (primaries.Count - result.Count));
    }

    /// <summary>The camera centre of each hold photo, resected from its placed holds (all facets).</summary>
    public static Dictionary<Wall3DPhotoKey, double[]> PanelCameras(IReadOnlyList<Hold> live, IReadOnlyDictionary<string, FacetFrame> frames)
    {
        var result = new Dictionary<Wall3DPhotoKey, double[]>();
        var placed = live.Where(h => h.FacetId is { } f && frames.ContainsKey(f) && h.PlaneAMm.HasValue && h.PlaneBMm.HasValue);
        foreach (var group in placed.GroupBy(HoldPlaneProjector.PhotoOf))
        {
            var holds = group.OrderBy(h => h.Id).ToList();
            var world = holds.Select(h => frames[h.FacetId!].ToWorld(h.PlaneAMm!.Value, h.PlaneBMm!.Value)).ToList();
            if (CameraResection.Centre(world, holds.Select(h => (h.X, h.Y)).ToList()) is { } c)
            {
                result[group.Key] = c;
            }
        }

        return result;
    }

    private static Dictionary<string, FacetFrame> Frames(WallGeometryDocument doc)
    {
        var frames = new Dictionary<string, FacetFrame>(StringComparer.Ordinal);
        foreach (var facet in doc.Segments.SelectMany(s => s.Facets))
        {
            if (!string.IsNullOrEmpty(facet.Id) && FacetFrame.From(facet) is { } frame)
            {
                frames[facet.Id] = frame;
            }
        }

        return frames;
    }

    /// <summary>The panel-photo silhouette on the facet, exactly as the 3D view maps it today.</summary>
    private static (FacetFrame Frame, FootprintView View)? Primary(
        Hold hold, Dictionary<string, FacetFrame> frames, Dictionary<Wall3DPhotoKey, double[]> panelCams, HoldPlaneProjector projector)
    {
        if (hold.ShapePoints is not { Count: >= 3 } outline || hold.FacetId is not { } facetId || !hold.PlaneAMm.HasValue
            || !hold.PlaneBMm.HasValue || !frames.TryGetValue(facetId, out var frame)
            || !panelCams.TryGetValue(HoldPlaneProjector.PhotoOf(hold), out var camera) || projector.For(hold) is not { } mapping)
        {
            return null;
        }

        var ring = outline.Select(p => mapping.Map(hold.X + p.Dx, hold.Y + p.Dy)).ToList();
        return ring.All(p => double.IsFinite(p.A) && double.IsFinite(p.B))
            ? (frame, new FootprintView(ring, camera, "panel"))
            : null;
    }

    /// <summary>Traces every hold this capture photo sees and adds its facet-plane silhouette.</summary>
    private static void TraceIn(
        SolvedCamera declared,
        Func<SolvedCamera, IHoldOutlineSession?> open,
        IEnumerable<(Hold Hold, FacetFrame Frame, FootprintView View)> primaries,
        Dictionary<Guid, List<FootprintView>> others)
    {
        var seen = primaries.Where(p => Sees(declared, p.Frame, p.View)).ToList();
        if (seen.Count == 0)
        {
            return;
        }

        using var session = open(declared);
        if (session is null)
        {
            return;
        }

        var camera = declared.ScaledTo(session.ImageWidth, session.ImageHeight);
        foreach (var (hold, frame, view) in seen)
        {
            if (Trace(camera, session, frame, view) is { } silhouette)
            {
                others[hold.Id].Add(new FootprintView(silhouette, camera.Centre, camera.Image));
            }
        }
    }

    /// <summary>Whether the capture camera sees the hold head-on enough and inside the frame.</summary>
    private static bool Sees(SolvedCamera camera, FacetFrame frame, FootprintView view)
    {
        var centre = Centroid(view.Silhouette);
        if (HoldFootprintEstimator.ViewOf(frame, centre, camera.Centre) is not { ThetaDeg: <= MaxCaptureThetaDeg })
        {
            return false;
        }

        return camera.Project(frame.ToWorld(centre.A, centre.B)) is { } px
            && px.X >= FrameMargin * camera.Width && px.X <= (1 - FrameMargin) * camera.Width
            && px.Y >= FrameMargin * camera.Height && px.Y <= (1 - FrameMargin) * camera.Height;
    }

    /// <summary>The segmenter's outline in the capture photo, seeded with the panel silhouette's box there, on the facet.</summary>
    private static List<(double A, double B)>? Trace(SolvedCamera camera, IHoldOutlineSession session, FacetFrame frame, FootprintView view)
    {
        var pixels = view.Silhouette.Select(p => camera.Project(frame.ToWorld(p.A, p.B))).ToList();
        if (pixels.Any(p => p is null))
        {
            return null;
        }

        var xs = pixels.Select(p => p!.Value.X).ToList();
        var ys = pixels.Select(p => p!.Value.Y).ToList();
        double w = session.ImageWidth, h = session.ImageHeight;
        var box = (W: xs.Max() - xs.Min(), H: ys.Max() - ys.Min());
        var seed = new HoldSeed(
            (xs.Min() + (box.W / 2)) / w,
            (ys.Min() + (box.H / 2)) / h,
            Math.Max(box.W, box.H) / 2 / Math.Max(w, h),
            box.W / w,
            box.H / h);
        var result = session.Outline(seed);
        if (result.Method == HoldOutlineMethod.CircleFallback || result.Polygon.Count < 3)
        {
            return null;
        }

        var ring = result.Polygon.Select(p => camera.PixelToPlane(frame, p.X * w, p.Y * h)).ToList();
        return ring.All(p => p is not null) ? ring.Select(p => p!.Value).ToList() : null;
    }

    private static (double A, double B) Centroid(IReadOnlyList<(double A, double B)> ring) =>
        (ring.Average(p => p.A), ring.Average(p => p.B));
}
