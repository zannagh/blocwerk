// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Geometry;
using Blocwerk.Core.Geometry.View3D;
using Blocwerk.Core.MarkerPlanning;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The plan's right-angle corner picks the kept half of a triangle facet (<see cref="TriangleKeptSide"/>). The
/// overhang "2" leans 45° toward the room (−y) from the origin, its normal facing the climber. Two vertical side walls
/// meet it; each facet's a runs "right as you face its markers" (the plan's x): on the right side wall (yaw −90,
/// facing −x) toward the room, on the left one (yaw +90, facing +x) away from it.
/// </summary>
public class Wall3DFacetOutlinesRightAngleTests
{
    private const double S = 0.7071067811865476;

    private static readonly double[][] BehindRight = [[0, 0], [1000, 1000], [0, 1000]];

    private static readonly Wall3DFacet Overhang = Facet([0, 0, 0], [1, 0, 0], [0, -S, S], [0, -S, -S]);

    /// <summary>x = 1000, facing −x: (a, b) ↦ (1000, −a, b); the seam is a = b.</summary>
    private static readonly Wall3DFacet RightSide = Facet([1000, 0, 0], [0, -1, 0], [0, 0, 1], [-1, 0, 0]);

    /// <summary>x = 0, facing +x: (a, b) ↦ (0, a − 1000, b); the seam is a + b = 1000.</summary>
    private static readonly Wall3DFacet LeftSide = Facet([0, -1000, 0], [0, 1, 0], [0, 0, 1], [1, 0, 0]);

    [Theory]
    [InlineData(TriangleCorner.TopLeft)]
    [InlineData(TriangleCorner.BottomLeft)]
    [InlineData(TriangleCorner.TopRight)]
    public void RightSide_KeepsTheHalfBehindTheOverhang_UnlessThePlanSaysFloor(TriangleCorner corner)
    {
        // Top-left is the plan's own "behind"; bottom-left and top-right sit on the seam, so the plan says nothing.
        AssertPoints(BehindRight, Wall3DFacetOutlines.Clip(RightSide, Overhang, (700, 200), corner)!);
    }

    [Fact]
    public void RightSide_BottomRight_ReachesTheFloorInFrontOfTheOverhang()
    {
        // Markers behind the seam must not overrule the plan.
        AssertPoints([[0, 0], [1000, 0], [1000, 1000]], Wall3DFacetOutlines.Clip(RightSide, Overhang, (200, 700), TriangleCorner.BottomRight)!);
    }

    [Fact]
    public void LeftSide_MirrorsTheCorners_BottomLeftIsTheFloorInFront()
    {
        AssertPoints([[0, 0], [1000, 0], [0, 1000]], Wall3DFacetOutlines.Clip(LeftSide, Overhang, null, TriangleCorner.BottomLeft)!);
        AssertPoints([[1000, 0], [1000, 1000], [0, 1000]], Wall3DFacetOutlines.Clip(LeftSide, Overhang, null, TriangleCorner.TopRight)!);
    }

    [Fact]
    public void LeftSide_BottomRight_IsOnTheSeam_SoTheHalfBehindIsKept()
    {
        AssertPoints([[1000, 0], [1000, 1000], [0, 1000]], Wall3DFacetOutlines.Clip(LeftSide, Overhang, null, TriangleCorner.BottomRight)!);
    }

    [Fact]
    public void Corner_OnlyMapsOntoAFacetThatRisesUpItsSurface()
    {
        var roof = Facet([0, 0, 1000], [1, 0, 0], [0, -1, 0], [0, 0, -1]);

        Assert.Equal(TriangleCorner.BottomLeft, TriangleKeptSide.Mapped(LeftSide, null, TriangleCorner.BottomLeft));
        Assert.Null(TriangleKeptSide.Mapped(roof, [0, 0, 1], TriangleCorner.BottomLeft));
    }

    private static Wall3DFacet Facet(double[] origin, double[] u, double[] v, double[] normal) =>
        new("f", 0, "f", origin, u, v, normal, [], new PlaneRectMm(0, 1000, 0, 1000), null);

    private static void AssertPoints(double[][] expected, IReadOnlyList<double[]> actual)
    {
        Assert.Equal(expected.Length, actual.Count);
        for (var i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i][0], actual[i][0], 1e-6);
            Assert.Equal(expected[i][1], actual[i][1], 1e-6);
        }
    }
}
