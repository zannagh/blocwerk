// <copyright file="CloseUpFacetMask.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Geometry.Footprints;
using Blocwerk.Core.Geometry.Proposals;
using Blocwerk.Core.Geometry.Volumes;

namespace Blocwerk.Core.Geometry.HybridZoom;

/// <summary>
/// Where a planar close-up shows another facet than the one it is warped through. The warp is exact only on its
/// facet's plane; across a fold (a side wall, the kickboard) the close-up shows the other facet at the wrong place
/// against the panel photo. The view is cut into <see cref="Steps"/>² cells; each cell's centre is cast from the
/// close-up camera onto the model (nearest facet whose real shape it hits), and the cells that land on another facet
/// are returned, merged into horizontal runs, as holes (view coordinates) for the renderer to leave transparent.
/// Cells whose ray hits no facet are kept. Pure: no I/O.
/// </summary>
public static class CloseUpFacetMask
{
    /// <summary>Cells per side of the view.</summary>
    public const int Steps = 16;

    /// <summary>A ray that meets a facet more obliquely than this (cosine) does not count as hitting it.</summary>
    private const double MinCos = 0.15;

    /// <summary>The off-facet cells as rectangles (u0, v0, u1, v1 → four corners), row runs merged.</summary>
    /// <param name="camera">The close-up's camera.</param>
    /// <param name="viewToPhoto">Its warp, view → photo px.</param>
    /// <param name="facetId">The facet the close-up is warped through.</param>
    /// <param name="facets">The model's facets.</param>
    /// <returns>The holes.</returns>
    public static IReadOnlyList<IReadOnlyList<(double U, double V)>> OffFacet(
        SolvedCamera camera, PlaneHomography viewToPhoto, string facetId, IReadOnlyList<CastFacet> facets)
    {
        var result = new List<IReadOnlyList<(double U, double V)>>();
        if (facets.Count < 2)
        {
            return result;
        }

        const double Cell = 1.0 / Steps;
        for (var j = 0; j < Steps; j++)
        {
            int? runStart = null;
            for (var i = 0; i <= Steps; i++)
            {
                var off = i < Steps && HitFacet(camera, viewToPhoto, (i + 0.5) * Cell, (j + 0.5) * Cell, facets) is { } hit && hit != facetId;
                if (off && runStart is null)
                {
                    runStart = i;
                }
                else if (!off && runStart is { } start)
                {
                    result.Add(Rect(start * Cell, j * Cell, i * Cell, (j + 1) * Cell));
                    runStart = null;
                }
            }
        }

        return result;
    }

    /// <summary>The facet the ray through view point (u, v) meets first, or null.</summary>
    private static string? HitFacet(SolvedCamera camera, PlaneHomography viewToPhoto, double u, double v, IReadOnlyList<CastFacet> facets)
    {
        var (px, py) = viewToPhoto.Apply(u, v);
        if (!double.IsFinite(px) || !double.IsFinite(py))
        {
            return null;
        }

        var o = camera.Centre;
        var dir = camera.RayDirection(px, py);
        string? best = null;
        var bestT = double.PositiveInfinity;
        foreach (var f in facets)
        {
            var n = f.Frame.Normal;
            var den = (dir[0] * n[0]) + (dir[1] * n[1]) + (dir[2] * n[2]);
            if (-den < MinCos)
            {
                continue;
            }

            var t = (((f.Frame.Origin[0] - o[0]) * n[0]) + ((f.Frame.Origin[1] - o[1]) * n[1]) + ((f.Frame.Origin[2] - o[2]) * n[2])) / den;
            if (t <= 0 || t >= bestT)
            {
                continue;
            }

            var (a, b, _) = FacetCloud.Local(f.Frame, o[0] + (t * dir[0]), o[1] + (t * dir[1]), o[2] + (t * dir[2]));
            if (f.Covers(a, b))
            {
                (best, bestT) = (f.Id, t);
            }
        }

        return best;
    }

    private static IReadOnlyList<(double U, double V)> Rect(double u0, double v0, double u1, double v1) =>
        [(u0, v0), (u1, v0), (u1, v1), (u0, v1)];
}
