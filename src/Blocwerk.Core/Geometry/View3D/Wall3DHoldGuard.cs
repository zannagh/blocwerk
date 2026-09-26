// <copyright file="Wall3DHoldGuard.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Geometry.Registration;

namespace Blocwerk.Core.Geometry.View3D;

/// <summary>
/// Keeps every drawn hold on a facet. A hold is drawn where its centre plus its outline sits
/// (<see cref="DrawnCentre"/>); a stored footprint refined against an earlier position can put that far off the
/// facet (The Attic - COLMAP Improvements: five carried-over holds of one panel photo drew their outlines a metre
/// below the main wall, in the air under the kickboard). Such a hold moves to the facet whose plane and outline
/// contain the drawn point, else it is not drawn and counts as not measured.
/// </summary>
public static class Wall3DHoldGuard
{
    /// <summary>How far outside its facet's extent a drawn hold may still sit, mm (as <see cref="Wall3DFallbackPlacement.OffFacetMarginMm"/>).</summary>
    public const double MarginMm = Wall3DFallbackPlacement.OffFacetMarginMm;

    /// <summary>Farthest a drawn point may lie off another facet's plane to move onto it, mm.</summary>
    public const double PlaneToleranceMm = 60;

    /// <summary>Whether (a, b) lies on the extent, within <paramref name="margin"/>.</summary>
    /// <param name="extent">The facet extent.</param>
    /// <param name="a">Plane a, mm.</param>
    /// <param name="b">Plane b, mm.</param>
    /// <param name="margin">The tolerance, mm.</param>
    /// <returns>True when on it.</returns>
    public static bool Contains(PlaneRectMm extent, double a, double b, double margin = MarginMm) =>
        double.IsFinite(a) && double.IsFinite(b)
        && a >= extent.AMin - margin && a <= extent.AMax + margin
        && b >= extent.BMin - margin && b <= extent.BMax + margin;

    /// <summary>The centre of an outline's bounds relative to its hold's centre, (0, 0) without one.</summary>
    /// <param name="outline">The outline, <c>[da, db]</c> per vertex.</param>
    /// <returns>The offset, mm.</returns>
    public static (double A, double B) OutlineOffset(IReadOnlyList<double[]>? outline)
    {
        if (outline is not { Count: >= 3 })
        {
            return (0, 0);
        }

        return ((outline.Min(v => v[0]) + outline.Max(v => v[0])) / 2, (outline.Min(v => v[1]) + outline.Max(v => v[1])) / 2);
    }

    /// <summary>Where the hold is drawn on its facet: its centre plus the offset of its outline.</summary>
    /// <param name="hold">The hold.</param>
    /// <returns>Plane (a, b), mm.</returns>
    public static (double A, double B) DrawnCentre(Wall3DHold hold)
    {
        var (da, db) = OutlineOffset(hold.Shape?.Outline);
        return (hold.PlaneA + da, hold.PlaneB + db);
    }

    /// <summary>
    /// The hold as drawn: unchanged when it sits on its facet, moved onto the facet containing its drawn point
    /// (outline re-centred on it), or null when no facet does.
    /// </summary>
    /// <param name="hold">The placed hold.</param>
    /// <param name="facets">Every drawn facet's frame and extent, by id.</param>
    /// <returns>The hold to draw, or null.</returns>
    public static Wall3DHold? Keep(Wall3DHold hold, IReadOnlyDictionary<string, (FacetFrame Frame, PlaneRectMm Extent)> facets)
    {
        if (!facets.TryGetValue(hold.FacetId, out var own))
        {
            return hold;
        }

        var (ca, cb) = DrawnCentre(hold);
        if (Contains(own.Extent, ca, cb))
        {
            return hold;
        }

        var world = own.Frame.ToWorld(ca, cb);
        var best = facets
            .Where(kv => kv.Key != hold.FacetId)
            .Select(kv => (Id: kv.Key, kv.Value.Frame, Plane: InPlane(kv.Value.Frame, world), kv.Value.Extent))
            .Where(x => Math.Abs(x.Plane.Off) <= PlaneToleranceMm && Contains(x.Extent, x.Plane.A, x.Plane.B))
            .OrderBy(x => Math.Abs(x.Plane.Off))
            .FirstOrDefault();
        if (best.Id is null)
        {
            return null;
        }

        var (da, db) = (ca - hold.PlaneA, cb - hold.PlaneB);
        return hold with
        {
            FacetId = best.Id,
            PlaneA = best.Plane.A,
            PlaneB = best.Plane.B,
            Position = best.Frame.ToWorld(best.Plane.A, best.Plane.B, Wall3DViewBuilder.HoldLiftMm),
            Shape = hold.Shape is { } s ? s with { Outline = Shift(s.Outline, da, db), Holes = s.Holes.Select(h => Shift(h, da, db)).ToList() } : null,
            PhotoOutline = null,
        };
    }

    /// <summary>Whether a photo outline, relative to the hold's centre, is drawn on the hold's facet.</summary>
    /// <param name="hold">The hold.</param>
    /// <param name="ring">Its photo outline.</param>
    /// <param name="extent">Its facet's extent.</param>
    /// <returns>True when on it.</returns>
    public static bool OnFacet(Wall3DHold hold, IReadOnlyList<double[]> ring, PlaneRectMm extent)
    {
        var (da, db) = OutlineOffset(ring);
        return Contains(extent, hold.PlaneA + da, hold.PlaneB + db);
    }

    /// <summary>
    /// Whether a placement may be written: its facet has no known extent, or (a, b) and the outline drawn there
    /// (a footprint, relative to (a, b)) both sit on it. The placement and footprint writers check this, so they
    /// cannot store a hold that the view would have to drop.
    /// </summary>
    /// <param name="facetId">The target facet.</param>
    /// <param name="a">Plane a, mm.</param>
    /// <param name="b">Plane b, mm.</param>
    /// <param name="extents">The model's facet extents.</param>
    /// <param name="outline">The outline drawn at (a, b), if any.</param>
    /// <returns>True when on the facet.</returns>
    public static bool PlacementOnFacet(
        string facetId, double a, double b, IReadOnlyDictionary<string, PlaneRectMm> extents, IReadOnlyList<double[]>? outline = null)
    {
        if (!extents.TryGetValue(facetId, out var extent))
        {
            return true;
        }

        var (da, db) = OutlineOffset(outline);
        return Contains(extent, a, b) && Contains(extent, a + da, b + db);
    }

    private static (double A, double B, double Off) InPlane(FacetFrame frame, double[] world)
    {
        var d = Vec3.Sub(world, frame.Origin);
        return (Vec3.Dot(d, frame.U), Vec3.Dot(d, frame.V), Vec3.Dot(d, frame.Normal));
    }

    private static List<double[]> Shift(IReadOnlyList<double[]> ring, double da, double db) =>
        ring.Select(v => new[] { v[0] - da, v[1] - db }).ToList();
}
