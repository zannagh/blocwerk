// <copyright file="HoldProposalFacetTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Geometry;
using Blocwerk.Core.Geometry.Footprints;
using Blocwerk.Core.Geometry.Proposals;
using Blocwerk.Core.Geometry.View3D;
using static Blocwerk.Core.Tests.HoldProposalFinderTests;

namespace Blocwerk.Core.Tests;

/// <summary>
/// Proposals across facet boundaries: a hold at a seam whose point lies just onto the facet fewer rays landed on is
/// proposed on that facet, and the two faces of a thin panel are never one hold.
/// </summary>
public class HoldProposalFacetTests
{
    private static readonly FacetFrame Wall = HoldFootprintEstimatorTests.Wall;

    /// <summary>The back face of a 20 mm panel whose front face is <see cref="Wall"/>.</summary>
    private static readonly FacetFrame Back = FacetFrame.From(new WallGeometryFacet
    {
        Id = "back", Origin = [0, 20, 0], U = [-1, 0, 0], V = [0, 0, 1], Normal = [0, 1, 0],
    })!;

    [Fact]
    public void AHoldJustOverASeam_IsProposedOnTheFacetItIsOn_EvenWhenMostRaysLandedOnTheOther()
    {
        // Same plane, split at a = 1500 ("0" ends at 1490, its rays may land up to 20 mm past that); the hold is at a = 1503.
        CastFacet[] facets =
        [
            new("0", Wall, new PlaneRectMm(0, 1490, 0, 2000), []),
            new("1", Wall, new PlaneRectMm(1500, 3000, 0, 2000), []),
        ];
        var hold = Wall.ToWorld(1503, 1000, 60);
        var detections = Cameras.Values.Select(c => DetectIn(c, hold, 60)).OfType<CaptureDetection>().ToList();
        var hits = detections.Select(d => WallSurfaceCaster.Cast(Cameras[d.Photo], d, facets)).OfType<SurfaceHit>().ToList();

        var (candidates, _) = HoldProposalFinder.Find(Cameras, detections, facets, [], []);

        Assert.True(hits.Count(h => h.FacetId == "0") > hits.Count(h => h.FacetId == "1"), "most rays land on facet 0");
        var c = Assert.Single(candidates);
        Assert.Equal("1", c.FacetId);
        Assert.InRange(RayMath.Length(c.World, hold), 0, 5);
    }

    [Fact]
    public void TheTwoFacesOfAThinPanel_AreNeverOneHold()
    {
        var front = Wall.ToWorld(1500, 1000, 10);
        double[] back = [1500, 30, 1000];
        var cameras = Cameras.Where(c => c.Key is "p1" or "p2" or "p3").ToDictionary(c => c.Key, c => c.Value);
        cameras["b1"] = LookAt("b1", [1500, 3000, 1000], [1500, 0, 1000]);
        CastFacet[] facets = [new("front", Wall, new PlaneRectMm(0, 3000, 0, 2000), []), new("back", Back, new PlaneRectMm(-3000, 0, 0, 2000), [])];
        var hits = cameras.Values
            .Select(c => DetectIn(c, c.Image == "b1" ? back : front, 60))
            .OfType<CaptureDetection>()
            .Select(d => WallSurfaceCaster.Cast(cameras[d.Photo], d, facets))
            .OfType<SurfaceHit>()
            .ToList();

        var cluster = Assert.Single(MultiViewHoldClusterer.Cluster(hits, cameras, 3));

        Assert.Equal("back", Assert.Single(hits, h => h.Detection.Photo == "b1").FacetId);
        Assert.Equal(3, cluster.Views);
        Assert.DoesNotContain(cluster.Hits, h => h.Detection.Photo == "b1");
    }
}
