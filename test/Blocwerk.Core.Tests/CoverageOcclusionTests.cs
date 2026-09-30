// <copyright file="CoverageOcclusionTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Capture.Coverage;
using Blocwerk.Core.Geometry;
using static Blocwerk.Core.Tests.CoverageSceneFixtures;

namespace Blocwerk.Core.Tests;

/// <summary>
/// A camera counts for a point only when its straight line of sight does not pass through another facet's real board:
/// lying behind another facet's plane hides nothing by itself.
/// </summary>
public class CoverageOcclusionTests
{
    [Fact]
    public void AKickboardUnderAnOverhang_IsFullyRated()
    {
        // Shaped like The Attic: a 46° overhang above a vertical kickboard, a closing panel at the right end whose
        // plane passes behind the kickboard.
        var overhang = Facet("main", [0, 0, 0], [1, 0, 0], [0, -0.72, 0.69], [0, -0.69, -0.724], new PlaneRectMm(-50, 5050, -50, 3300));
        var kickboard = Facet("kick", [0, -12, -271], [1, 0, 0], [0, 0, 1], [0, -1, 0], new PlaneRectMm(-50, 5750, -50, 326));
        var closing = Facet("close", [5711, -1637, -276], [0, 1, 0], [0, 0, 1], [1, 0, 0], new PlaneRectMm(-50, 2125, -50, 2022));
        var scene = new CoverageScene([overhang, kickboard, closing], []);
        var cameras = Cameras(
            [[800, -1800, 600], [2800, -1800, 600], [4800, -1800, 600], [1800, -2500, 1200], [3800, -2500, 1200]],
            [[300, -12, -150], [1500, -12, -150], [2900, -12, -150], [4300, -12, -150], [5500, -12, -150]]);

        var rated = Rate(scene, "kick", cameras);

        Assert.DoesNotContain(CoverageCellStatus.Hidden, rated.Status);
        Assert.True(rated.Status.Count(s => s != CoverageCellStatus.Never) > rated.Status.Length * 0.9, "kickboard mostly unseen");
    }

    [Fact]
    public void AFinThatStandsInTheWay_StillBlocksThatCamera()
    {
        var wall = Facet("wall", [0, 0, 0], [1, 0, 0], [0, 0, 1], [0, -1, 0], new PlaneRectMm(0, 3000, 0, 3000));
        var fin = Facet("fin", [1500, 0, 0], [0, -1, 0], [0, 0, 1], [1, 0, 0], new PlaneRectMm(0, 1000, 0, 3000));
        var scene = new CoverageScene([wall, fin], []);
        double[] target = [1000, 0, 1500];
        var blocked = CoverageFixtures.Photo([2500, -1500, 1500], target, "behind-fin");
        var clear = CoverageFixtures.Photo([500, -1500, 1500], target, "clear");

        var views = PointViews.Evaluate(target, [0, -1, 0], "wall", [blocked, clear], scene);
        var onlyBlocked = PointViews.Evaluate(target, [0, -1, 0], "wall", [blocked], scene);

        Assert.Equal(1, views.Views);
        Assert.Equal(1, views.Blocked);
        Assert.Equal(CoverageCellStatus.Hidden, onlyBlocked.Status);
    }

    [Fact]
    public void ATrianglePanelsEmptyHalf_BlocksNothing()
    {
        // A side panel in front of the wall, a triangle below its seam with a leaning slab (only its lower end modelled).
        var wall = Facet("wall", [0, 0, 0], [1, 0, 0], [0, 0, 1], [0, -1, 0], new PlaneRectMm(0, 3000, 0, 3000));
        var slab = Facet("slab", [0, 0, 3000], [1, 0, 0], [0, -0.316, -0.949], [0, -0.949, 0.316], new PlaneRectMm(1400, 1600, 1600, 3200));
        var rect = new PlaneRectMm(0, 1000, 0, 3000);
        var triangle = Facet("side", [1500, 0, 0], [0, -1, 0], [0, 0, 1], [1, 0, 0], rect, [.. Marker(200, 500), .. Marker(500, 300)]);
        var unmarked = Facet("side", [1500, 0, 0], [0, -1, 0], [0, 0, 1], [1, 0, 0], rect);
        double[] target = [1000, 0, 2600];
        List<CoverageCamera> camera = [CoverageFixtures.Photo([2500, -1500, 2500], target)];

        var cut = new CoverageScene([wall, slab, triangle], []);
        var whole = new CoverageScene([wall, slab, unmarked], []);

        Assert.Equal(1, PointViews.Evaluate(target, [0, -1, 0], "wall", camera, cut).Views);
        Assert.Equal(1, PointViews.Evaluate(target, [0, -1, 0], "wall", camera, whole).Blocked);
    }
}
