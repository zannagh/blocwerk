// <copyright file="CoverageSceneInsideWallTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Capture.Coverage;
using Blocwerk.Core.Geometry;
using static Blocwerk.Core.Tests.CoverageSceneFixtures;

namespace Blocwerk.Core.Tests;

/// <summary>
/// Which parts of a facet's region are not rated: only where it lies behind another facet's real board and no camera sees
/// it, or beyond the facet's marker-confirmed seam. A distant, parallel or behind panel never hides it.
/// </summary>
public class CoverageSceneInsideWallTests
{
    private static readonly CoverageFacet MainWall =
        Facet("main", [0, 0, 0], [1, 0, 0], [0, 0, 1], [0, -1, 0], new PlaneRectMm(0, 5130, 0, 3500));

    private static readonly List<CoverageCamera> FrontCameras = Cameras(
        [[1000, -3000, 1500], [2500, -3000, 1500], [4000, -3000, 1500]],
        [[1200, 0, 1000], [2500, 0, 2500], [3800, 0, 1000]]);

    [Fact]
    public void AFarSidePanel_DoesNotHideTheMainWall()
    {
        var scene = new CoverageScene([MainWall, Facet("far", [5721, 0, 0], [0, -1, 0], [0, 0, 1], [1, 0, 0], new PlaneRectMm(-3000, 3000, 0, 3500))], []);

        Assert.DoesNotContain(CoverageCellStatus.Hidden, Rate(scene, "main", FrontCameras).Status);
    }

    [Fact]
    public void AParallelPanelBehindTheWall_DoesNotHideIt()
    {
        var scene = new CoverageScene([MainWall, Facet("back", [0, 2000, 0], [1, 0, 0], [0, 0, 1], [0, -1, 0], new PlaneRectMm(0, 5130, 0, 3500))], []);

        Assert.DoesNotContain(CoverageCellStatus.Hidden, Rate(scene, "main", FrontCameras).Status);
    }

    [Fact]
    public void AnAdjacentSidePanel_IsHiddenWhereItsRegionReachesBehindTheWall()
    {
        var side = Facet("side", [5000, 0, 0], [0, 1, 0], [0, 0, 1], [-1, 0, 0], new PlaneRectMm(-3000, 1000, 0, 3500));
        var scene = new CoverageScene([MainWall, side], []);
        List<CoverageCamera> cameras = [CoverageFixtures.Photo([2500, -2500, 1500], [5000, -500, 1500])];

        Assert.Equal(CoverageCellStatus.Hidden, PointViews.Evaluate([5000, 500, 1500], [-1, 0, 0], "side", cameras, scene).Status);
        Assert.Equal(1, PointViews.Evaluate([5000, -1500, 1500], [-1, 0, 0], "side", cameras, scene).Views);
    }

    [Fact]
    public void ATrianglePanelsCutAwayHalf_IsNotRated_ItsRealHalfIs()
    {
        // A side panel whose markers lie below its seam with a leaning slab: the seam runs from (0, 3000) to (1000, 0).
        var slab = Facet("slab", [0, 0, 3000], [1, 0, 0], [0, -0.316, -0.949], [0, -0.949, 0.316], new PlaneRectMm(1400, 1600, 1600, 3200));
        var side = Facet("side", [1500, 0, 0], [0, -1, 0], [0, 0, 1], [1, 0, 0], new PlaneRectMm(0, 1000, 0, 3000), [.. Marker(200, 500), .. Marker(500, 300)]);
        var scene = new CoverageScene([MainWall, slab, side], []);
        var cameras = Cameras([[3000, -500, 800], [3000, -1500, 2200], [2600, -200, 2800]], [[1500, -300, 800], [1500, -800, 2600]]);

        var rated = Rate(scene, "side", cameras);

        Assert.Equal(CoverageCellStatus.Hidden, rated.Status[rated.Grid.IndexOf(900, 2500)]);
        Assert.Equal(CoverageCellStatus.Hidden, rated.Status[rated.Grid.IndexOf(700, 1500)]);
        Assert.NotEqual(CoverageCellStatus.Hidden, rated.Status[rated.Grid.IndexOf(200, 500)]);
        Assert.NotEqual(CoverageCellStatus.Hidden, rated.Status[rated.Grid.IndexOf(300, 1800)]);
        Assert.InRange(rated.Status.Count(s => s == CoverageCellStatus.Hidden), rated.Status.Length * 0.4, rated.Status.Length * 0.6);
    }
}
