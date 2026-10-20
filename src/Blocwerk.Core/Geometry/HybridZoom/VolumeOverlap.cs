// <copyright file="VolumeOverlap.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Geometry.HybridZoom;

/// <summary>A volume's outline on its facet (plane mm), as the overlap test needs it.</summary>
/// <param name="Id">The volume.</param>
/// <param name="Outline">Its footprint polygon, (a, b) mm.</param>
/// <param name="HeightMm">How far it stands off the facet, mm.</param>
public sealed record VolumeOutline(Guid Id, IReadOnlyList<(double A, double B)> Outline, double HeightMm = 0)
{
    /// <summary>The mean of the outline's vertices (inside it: footprints are convex).</summary>
    public (double A, double B) Centroid =>
        Outline.Count == 0 ? (0, 0) : (Outline.Average(p => p.A), Outline.Average(p => p.B));
}

/// <summary>Where the 3D view should look for a volume: its facet, footprint centre and height.</summary>
/// <param name="Id">The volume.</param>
/// <param name="FacetId">Its facet.</param>
/// <param name="A">Footprint centre, plane a (mm).</param>
/// <param name="B">Footprint centre, plane b (mm).</param>
/// <param name="H">Height above the facet (mm).</param>
public sealed record VolumeSpot(Guid Id, string FacetId, double A, double B, double H)
{
    /// <summary>The 3D view's <c>?at=</c> form, framed from below: <c>facet:a:b:h:under</c>.</summary>
    /// <returns>The query value.</returns>
    public string ToUnderQuery() => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{FacetId}:{A:0}:{B:0}:{H:0}:under");
}

/// <summary>
/// How much of a view lies on a volume. A close-up warped through the facet plane is only right where the wall is
/// that plane; on a volume the surface stands off it, the warp shows the volume displaced against the panel photo,
/// and the view belongs to the 3D inset instead. Pure: no I/O.
/// </summary>
public static class VolumeOverlap
{
    /// <summary>The volume most of the view's samples fall on and the share of samples on any volume.</summary>
    /// <param name="samples">The view's sample points on the facet plane, mm.</param>
    /// <param name="volumes">The facet's volumes.</param>
    /// <returns>The volume (null when none) and the share 0..1.</returns>
    public static (Guid? VolumeId, double Share) Of(IReadOnlyList<(double A, double B)> samples, IReadOnlyList<VolumeOutline> volumes)
    {
        if (samples.Count == 0 || volumes.Count == 0)
        {
            return (null, 0);
        }

        var hits = new Dictionary<Guid, int>();
        var onAny = 0;
        foreach (var (a, b) in samples)
        {
            var hit = volumes.FirstOrDefault(v => Contains(v.Outline, a, b));
            if (hit is null)
            {
                continue;
            }

            onAny++;
            hits[hit.Id] = hits.GetValueOrDefault(hit.Id) + 1;
        }

        var best = hits.Count == 0 ? (Guid?)null : hits.OrderByDescending(h => h.Value).ThenBy(h => h.Key).First().Key;
        return (best, (double)onAny / samples.Count);
    }

    /// <summary>
    /// The volumes that reach into a view: a sample of the view on them, or a corner of them inside the view's outline
    /// on the plane (a small volume can fall between the samples).
    /// </summary>
    /// <param name="samples">The view's sample points on the facet plane, mm.</param>
    /// <param name="viewOutline">The view's outline on the plane (its four corners, in order), mm.</param>
    /// <param name="volumes">The facet's volumes.</param>
    /// <returns>The volumes in the view.</returns>
    public static List<VolumeOutline> Touching(
        IReadOnlyList<(double A, double B)> samples, IReadOnlyList<(double A, double B)> viewOutline, IReadOnlyList<VolumeOutline> volumes) =>
        volumes.Where(v => samples.Any(s => Contains(v.Outline, s.A, s.B)) || v.Outline.Any(p => Contains(viewOutline, p.A, p.B))).ToList();

    /// <summary>Even-odd point-in-polygon.</summary>
    /// <param name="polygon">The polygon.</param>
    /// <param name="a">Point a.</param>
    /// <param name="b">Point b.</param>
    /// <returns>True inside.</returns>
    public static bool Contains(IReadOnlyList<(double A, double B)> polygon, double a, double b)
    {
        var inside = false;
        for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
        {
            var (ai, bi) = polygon[i];
            var (aj, bj) = polygon[j];
            if ((bi > b) != (bj > b) && a < ((aj - ai) * (b - bi) / (bj - bi)) + ai)
            {
                inside = !inside;
            }
        }

        return inside;
    }
}
