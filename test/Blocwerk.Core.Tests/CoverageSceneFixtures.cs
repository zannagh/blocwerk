// <copyright file="CoverageSceneFixtures.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Capture.Coverage;
using Blocwerk.Core.Geometry;
using Blocwerk.Core.Geometry.View3D;

namespace Blocwerk.Core.Tests;

/// <summary>Hand-built facets for the coverage scene's occlusion tests (world z up, the climbers at negative y).</summary>
internal static class CoverageSceneFixtures
{
    /// <summary>A facet with its plane frame and region.</summary>
    public static CoverageFacet Facet(
        string id, double[] origin, double[] u, double[] v, double[] normal, PlaneRectMm region, IReadOnlyList<double[]>? markerCorners = null)
    {
        var frame = FacetFrame.From(new WallGeometryFacet { Id = id, Origin = origin, U = u, V = v, Normal = normal })!;
        return new CoverageFacet(id, id, frame, region, 0, 0, markerCorners);
    }

    /// <summary>The corners of a 100 mm marker centred on (a, b).</summary>
    public static IEnumerable<double[]> Marker(double a, double b) =>
        [[a - 50, b - 50], [a + 50, b - 50], [a + 50, b + 50], [a - 50, b + 50]];

    /// <summary>Photos from each centre aimed at each target.</summary>
    public static List<CoverageCamera> Cameras(IEnumerable<double[]> centres, IEnumerable<double[]> targets)
    {
        var list = targets.ToList();
        return centres.SelectMany(c => list.Select(t => (c, t))).Select((ct, i) => CoverageFixtures.Photo(ct.c, ct.t, $"p{i:000}")).ToList();
    }

    /// <summary>The rated grid of one facet of the scene.</summary>
    public static RatedFacet Rate(CoverageScene scene, string facetId, IReadOnlyList<CoverageCamera> cameras) =>
        FacetCoverageRater.Rate(scene, cameras, 100).Single(r => r.Facet.Id == facetId);
}
