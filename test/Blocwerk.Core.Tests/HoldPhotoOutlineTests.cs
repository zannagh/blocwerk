// <copyright file="HoldPhotoOutlineTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Geometry.Footprints;
using Blocwerk.Core.Geometry.View3D;

namespace Blocwerk.Core.Tests;

/// <summary>
/// Where a hold shows in the facet photo: seen from the camera that painted its spot, a hold proud of the
/// facet shows displaced away from that camera. On the vertical test facet (a = x, b = z, normal −y).
/// </summary>
public class HoldPhotoOutlineTests
{
    private static readonly FacetFrame Wall = HoldFootprintEstimatorTests.Wall;

    [Fact]
    public void VolumeHold_IsProjectedFromTheSourceCamera_AwayFromIt()
    {
        // a hold on a 100 mm volume, body top 160 mm; the camera 1000 mm out, 500 mm to its left
        var hold = Hold(0, 0, new Wall3DHoldProtrusion(100, 160, 0, 0, 170, true, true, 10, 0));
        var ring = HoldPhotoOutline.For(Wall, hold, Camera("c", [-500, -1000, 0]), [])!;

        // projected at 130 mm: (q − c) · 130 / 870 → centre shifted by 10 (volume) + 510 · 0.149 ≈ 86 mm
        Assert.Equal(10 + (510 * 130.0 / 870), ring.Average(v => v[0]), 1.0);
        Assert.Equal(0, ring.Average(v => v[1]), 1.0);
    }

    [Fact]
    public void VolumeHold_WithItsPanelCamera_IsWalkedBackUpThePanelRay_ThenSeenFromTheSource()
    {
        // flat-mapped at (300, 0) by a panel camera straight in front of a = 0; the hold is 100 mm proud
        var hold = Hold(300, 0, new Wall3DHoldProtrusion(100, 100, 0, 0, 110, true, true, 40, 0));
        double[] panel = [0, -3000, 0];
        var ring = HoldPhotoOutline.For(Wall, hold, Camera("c", [-500, -1000, 0]), [], panel)!;

        // on the panel ray at 100 mm: a = 300 · 2900 / 3000 = 290; seen from the source: + 100/900 · (290 + 500)
        var expected = (300.0 * 2900 / 3000) + (100.0 / 900 * ((300.0 * 2900 / 3000) + 500)) - 300;
        Assert.Equal(expected, ring.Average(v => v[0]), 0.5);
        Assert.Equal(0, ring.Average(v => v[1]), 0.5);
    }

    [Fact]
    public void WallHold_SeenFromTheAverageView_StaysWhereItsFootprintIs()
    {
        var hold = Hold(0, 0, new Wall3DHoldProtrusion(0, 40, 0, 0, 50, true, false, 0, 0));
        var left = Camera("l", [-400, -1500, 0]);
        var right = Camera("r", [400, -1500, 0]);
        var middle = Camera("m", [0, -1500, 0]);

        Assert.Null(HoldPhotoOutline.For(Wall, hold, middle, [left, right, middle]));
    }

    [Fact]
    public void WallHold_SeenFromOneSide_MovesAwayFromThatSide_ByItsShareOfTheParallax()
    {
        var hold = Hold(0, 0, new Wall3DHoldProtrusion(0, 40, 0, 0, 50, true, false, 0, 0));
        var left = Camera("l", [-600, -1500, 0]);
        var right = Camera("r", [600, -1500, 0]);
        var ring = HoldPhotoOutline.For(Wall, hold, left, [left, right])!;

        // t(left) − mean(t) = 600 / 1500 = 0.4 per mm along +a; 0.3 · 40 mm body → 4.8 mm
        Assert.Equal(HoldPhotoOutline.WallParallaxShare * 40 * 0.4, ring.Average(v => v[0]), 0.2);
        Assert.Equal(0, ring.Average(v => v[1]), 0.2);

        // a circle stand-in is not the capture photos' consensus: left alone
        var circle = hold with { Shape = hold.Shape! with { Source = Wall3DShapeSource.Circle } };
        Assert.Null(HoldPhotoOutline.For(Wall, circle, left, [left, right]));
    }

    [Fact]
    public void Apply_UsesTheMapsCamera_AndLeavesFacetsWithoutAMapAlone()
    {
        var doc = new Geometry.WallGeometryDocument
        {
            Version = 1,
            Segments = [new Geometry.WallGeometrySegment { Index = 0, Facets = [new Geometry.WallGeometryFacet { Id = "0", Origin = [0, 0, 0], U = [1, 0, 0], V = [0, 0, 1], Normal = [0, -1, 0] }] }],
        };
        var onWall = Hold(15, 15, new Wall3DHoldProtrusion(0, 40, 0, 0, 50, true, false, 0, 0));
        var elsewhere = onWall with { Id = Guid.NewGuid(), FacetId = "9" };
        var view = new Wall3DView { Holds = [onWall, elsewhere] };
        var map = TextureSourceMap.Parse(TextureSourceMapTests.Doc())!;
        var cams = new[] { Camera("A", [-600, -1500, 0]), Camera("B", [600, -1500, 0]) };

        var result = Wall3DPhotoOutlines.Apply(view, doc, new Dictionary<string, TextureSourceMap> { ["0"] = map }, cams);

        Assert.True(result.Holds[0].PhotoOutline!.Average(v => v[0]) > 1, "camera A (left) pushes the hold right");
        Assert.Null(result.Holds[1].PhotoOutline);
    }

    /// <summary>A camera at <paramref name="c"/> looking straight at the wall (+y), 2000 px square, f = 1000 px.</summary>
    internal static SolvedCamera Camera(string name, double[] c)
    {
        double[] r = [1, 0, 0, 0, 0, -1, 0, 1, 0];
        double[] t = [-c[0], c[2], -c[1]];
        return new SolvedCamera(name, 2000, 2000, [1000, 0, 1000, 0, 1000, 1000, 0, 0, 1], [0, 0, 0, 0, 0], r, t);
    }

    internal static Wall3DHold Hold(double a, double b, Wall3DHoldProtrusion protrusion)
    {
        var ring = new List<double[]> { new[] { -20.0, -20.0 }, new[] { 20.0, -20.0 }, new[] { 20.0, 20.0 }, new[] { -20.0, 20.0 } };
        return new Wall3DHold(
            Guid.NewGuid(), "0", [a, 0, b], a, b, 40, 40, true, null, "x", "#fff", false, 0, null,
            new Wall3DHoldShape(Wall3DShapeSource.Footprint, ring, []), null, protrusion);
    }
}
