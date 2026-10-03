// <copyright file="Wall3DPhotoOutlines.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Geometry.Footprints;

namespace Blocwerk.Core.Geometry.View3D;

/// <summary>
/// Adds each hold's <see cref="Wall3DHold.PhotoOutline"/>: for a hold on a facet whose texture carries a
/// <see cref="TextureSourceMap"/>, the camera that painted most of the hold's outline decides where the
/// texture shows it (<see cref="HoldPhotoOutline"/>). Facets without a map, and cameras the model does not
/// know, leave the hold as it is (the Photos mode then draws its shape, as before).
/// </summary>
public static class Wall3DPhotoOutlines
{
    /// <summary>The view with photo outlines on every hold that has one.</summary>
    /// <param name="view">The built view.</param>
    /// <param name="doc">Its geometry document (facet frames).</param>
    /// <param name="maps">Source-view map per facet id.</param>
    /// <param name="cameras">The model's capture cameras.</param>
    /// <param name="panelCameras">Per hold id, the centre of the panel photo it was placed from (world mm), when known.</param>
    /// <returns>The view, holds updated.</returns>
    public static Wall3DView Apply(
        Wall3DView view,
        WallGeometryDocument doc,
        IReadOnlyDictionary<string, TextureSourceMap> maps,
        IReadOnlyList<SolvedCamera> cameras,
        IReadOnlyDictionary<Guid, double[]>? panelCameras = null)
    {
        if (maps.Count == 0 || cameras.Count == 0)
        {
            return view;
        }

        var byName = cameras.GroupBy(c => c.Image, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var frames = doc.Segments.SelectMany(s => s.Facets)
            .Where(f => !string.IsNullOrEmpty(f.Id) && maps.ContainsKey(f.Id))
            .Select(f => (f.Id, Frame: FacetFrame.From(f)))
            .Where(x => x.Frame is not null)
            .ToDictionary(x => x.Id, x => x.Frame!, StringComparer.Ordinal);
        var extents = view.Facets.GroupBy(f => f.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().Extent, StringComparer.Ordinal);
        var holds = view.Holds.Select(h => OnFacet(WithPhotoOutline(h, frames, maps, byName, cameras, panelCameras?.GetValueOrDefault(h.Id)), extents)).ToList();
        return view with { Holds = holds };
    }

    /// <summary>A photo outline drawn off the hold's facet (<see cref="Wall3DHoldGuard"/>) is dropped: the shape is drawn instead.</summary>
    private static Wall3DHold OnFacet(Wall3DHold hold, Dictionary<string, PlaneRectMm> extents) =>
        hold.PhotoOutline is { Count: >= 3 } ring && extents.TryGetValue(hold.FacetId, out var extent) && !Wall3DHoldGuard.OnFacet(hold, ring, extent)
            ? hold with { PhotoOutline = null }
            : hold;

    private static Wall3DHold WithPhotoOutline(
        Wall3DHold hold,
        Dictionary<string, FacetFrame> frames,
        IReadOnlyDictionary<string, TextureSourceMap> maps,
        Dictionary<string, SolvedCamera> byName,
        IReadOnlyList<SolvedCamera> cameras,
        double[]? panelCamera)
    {
        if (!frames.TryGetValue(hold.FacetId, out var frame) || hold.Shape is not { Outline.Count: >= 3 } shape)
        {
            return hold;
        }

        var (sa, sb) = hold.Protrusion is { OnVolume: true } p ? (p.ShiftA, p.ShiftB) : (0, 0);
        var points = shape.Outline.Select(v => (hold.PlaneA + sa + v[0], hold.PlaneB + sb + v[1]))
            .Append((hold.PlaneA + sa, hold.PlaneB + sb));
        if (maps[hold.FacetId].Dominant(points) is not { } name || !byName.TryGetValue(name, out var source))
        {
            return hold;
        }

        var ring = HoldPhotoOutline.For(frame, hold, source, cameras, panelCamera);

        // the hold may show where another photo painted the texture: then that photo decides
        var shown = ring?.Select(v => (hold.PlaneA + v[0], hold.PlaneB + v[1])) ?? [];
        if (ring is not null && maps[hold.FacetId].Dominant(shown) is { } other && other != name && byName.TryGetValue(other, out var second))
        {
            ring = HoldPhotoOutline.For(frame, hold, second, cameras, panelCamera);
        }

        return ring is null ? hold : hold with { PhotoOutline = ring };
    }
}
