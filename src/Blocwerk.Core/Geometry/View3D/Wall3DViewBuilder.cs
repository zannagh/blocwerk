// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Holds;

namespace Blocwerk.Core.Geometry.View3D;

/// <summary>
/// Pure projection of a wall (live holds + boulders, already access-checked and loaded) and its
/// active <see cref="WallGeometryDocument"/> into the <see cref="Wall3DView"/> the renderer draws.
/// No I/O, so the plane→world maths and the boulder roles are unit-testable on their own.
/// </summary>
public static class Wall3DViewBuilder
{
    /// <summary>How far a hold disc floats off its facet, so it never z-fights the plywood.</summary>
    public const double HoldLiftMm = 12;

    /// <summary>Stand-in size for a hand hold whose metric size was never measured.</summary>
    public const double DefaultHandSizeMm = 90;

    /// <summary>Stand-in size for a foot hold whose metric size was never measured.</summary>
    public const double DefaultFootSizeMm = 55;

    /// <summary>Margin around the marker bounds when a facet carries no <c>extentMm</c>.</summary>
    public const double FallbackMarginMm = 150;

    /// <summary>Builds the view. <paramref name="wall"/> must carry its holds and boulders (with BoulderHolds).</summary>
    public static Wall3DView Build(Wall wall, WallGeometryDocument doc, Guid? boulderId)
    {
        var frames = new Dictionary<string, FacetFrame>(StringComparer.Ordinal);
        var facets = BuildFacets(doc, wall, frames);
        var markers = BuildMarkers(doc);

        var liveBoulders = wall.Boulders.Where(b => !b.IsArchived && !b.IsDraft && !b.IsHistoric).ToList();
        var usage = liveBoulders
            .SelectMany(b => b.BoulderHolds.Select(bh => bh.HoldId).Distinct())
            .GroupBy(id => id)
            .ToDictionary(g => g.Key, g => g.Count());

        var boulder = boulderId is { } bid ? wall.Boulders.FirstOrDefault(b => b.Id == bid) : null;
        var boulderHolds = boulder?.BoulderHolds.ToDictionary(bh => bh.HoldId) ?? [];

        var holds = new List<Wall3DHold>();
        var unplaced = 0;
        foreach (var hold in LiveHolds(wall))
        {
            if (hold.FacetId is null || hold.PlaneAMm is not { } a || hold.PlaneBMm is not { } b
                || !frames.TryGetValue(hold.FacetId, out var frame))
            {
                unplaced++;
                continue;
            }

            holds.Add(ToHold(hold, frame, a, b, usage.GetValueOrDefault(hold.Id), RoleOf(hold, boulder, boulderHolds)));
        }

        return new Wall3DView
        {
            WallId = wall.Id,
            WallName = wall.Name,
            BoulderId = boulder?.Id,
            BoulderName = boulder?.Name,
            MarkerSizeMm = doc.MarkerSizeMm,
            Facets = facets,
            Markers = markers,
            Holds = holds,
            UnplacedHoldCount = unplaced,
            Textures = [],
            SplatUrl = null,
        };
    }

    /// <summary>
    /// The hold's part in <paramref name="boulder"/>, mirroring the 2D picker: its own BoulderHold
    /// (start/top by type, foot-only by usage), else a foothold by the boulder's colour rule.
    /// </summary>
    public static Wall3DHoldRole? RoleOf(Hold hold, Boulder? boulder, IReadOnlyDictionary<Guid, BoulderHold> boulderHolds)
    {
        if (boulder is null)
        {
            return null;
        }

        if (boulderHolds.TryGetValue(hold.Id, out var bh))
        {
            return bh.Type switch
            {
                HoldType.Start => Wall3DHoldRole.Start,
                HoldType.Top => Wall3DHoldRole.Top,
                _ => bh.Usage == HoldUsage.FootOnly ? Wall3DHoldRole.Foot : Wall3DHoldRole.Hand,
            };
        }

        return !string.IsNullOrEmpty(boulder.FootColorOnly) && hold.Color == boulder.FootColorOnly
            ? Wall3DHoldRole.ColorFoot
            : null;
    }

    /// <summary>The wall's live holds: everything at or below the wall generation (staged gen+1 rows excluded).</summary>
    private static IEnumerable<Hold> LiveHolds(Wall wall) =>
        wall.Holds.Where(h => h.Generation <= wall.CurrentGeneration);

    private static Wall3DHold ToHold(Hold hold, FacetFrame frame, double a, double b, int usage, Wall3DHoldRole? role)
    {
        var isFoot = hold.Category == HoldCategory.Foot;
        var fallback = isFoot ? DefaultFootSizeMm : DefaultHandSizeMm;
        var measured = hold.WidthMm is > 0 && hold.HeightMm is > 0;
        var color = HoldPalette.Get(hold.Color);
        return new Wall3DHold(
            hold.Id,
            hold.FacetId!,
            frame.ToWorld(a, b, HoldLiftMm),
            a,
            b,
            measured ? hold.WidthMm!.Value : fallback,
            measured ? hold.HeightMm!.Value : fallback,
            measured,
            hold.Color,
            HoldPalette.DisplayName(hold.Color),
            color.Hex,
            isFoot,
            usage,
            role);
    }

    private static List<Wall3DFacet> BuildFacets(WallGeometryDocument doc, Wall wall, Dictionary<string, FacetFrame> frames)
    {
        var result = new List<Wall3DFacet>();
        foreach (var segment in doc.Segments)
        {
            foreach (var facet in segment.Facets)
            {
                var frame = FacetFrame.From(facet);
                if (frame is null || string.IsNullOrEmpty(facet.Id))
                {
                    continue;
                }

                frames[facet.Id] = frame;
                var extent = facet.ExtentMm is { Width: > 0, Height: > 0 } e ? e : FallbackExtent(doc, wall, facet.Id);
                if (extent is null)
                {
                    continue;
                }

                var name = segment.Name ?? $"Segment {segment.Index}";
                if (segment.Facets.Count > 1)
                {
                    name = $"{name} ({facet.Id})";
                }

                result.Add(new Wall3DFacet(
                    facet.Id,
                    segment.Index,
                    name,
                    frame.Origin,
                    frame.U,
                    frame.V,
                    frame.Normal,
                    frame.Corners(extent.Value),
                    extent.Value,
                    facet.MeasuredAngleDeg ?? segment.MeasuredAngleDeg ?? segment.DeclaredAngleDeg));
            }
        }

        return result;
    }

    /// <summary>Bounds of the facet's marker corners and placed holds, plus a margin; null when it has neither.</summary>
    private static PlaneRectMm? FallbackExtent(WallGeometryDocument doc, Wall wall, string facetId)
    {
        var points = doc.Markers
            .Where(m => m.Facet == facetId)
            .SelectMany(m => m.CornersPlaneMm)
            .Where(c => c.Length >= 2)
            .Select(c => (c[0], c[1]))
            .Concat(LiveHolds(wall)
                .Where(h => h.FacetId == facetId && h.PlaneAMm.HasValue && h.PlaneBMm.HasValue)
                .Select(h => (h.PlaneAMm!.Value, h.PlaneBMm!.Value)));
        var bounds = PlaneRectMm.Bounds(points);
        return bounds is { } r
            ? new PlaneRectMm(r.AMin - FallbackMarginMm, r.AMax + FallbackMarginMm, r.BMin - FallbackMarginMm, r.BMax + FallbackMarginMm)
            : null;
    }

    private static List<Wall3DMarker> BuildMarkers(WallGeometryDocument doc) =>
        doc.Markers
            .Where(m => m.CornersWorldMm is { Count: 4 } c && c.All(p => p.Length == 3))
            .Select(m => new Wall3DMarker(m.Id, m.Facet, m.CornersWorldMm!, m.Synthetic))
            .ToList();
}
