// <copyright file="YawSignConventionTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;
using Blocwerk.Core.Geometry;
using Blocwerk.Core.MarkerPlanning;

namespace Blocwerk.Core.Tests;

/// <summary>
/// Pins the one yaw sign shared by the solver (<c>frame.yaw_rel</c>), <see cref="SurfaceYaw"/> and
/// <see cref="WallSegment.Yaw"/>/<see cref="WallSegment.MeasuredYaw"/>: positive = the surface faces the
/// viewer's right, i.e. it is on the viewer's left. The Attic's left side triangle measured +89°.
/// </summary>
public class YawSignConventionTests
{
    private const double AtticLeftTriangleYaw = 89;

    [Fact]
    public void ApplyMeasured_CopiesTheSolverYawUnchanged_LeftSideStaysPositive()
    {
        var document = new WallGeometryDocument
        {
            Segments =
            [
                Segment(0, 0),
                Segment(1, AtticLeftTriangleYaw),
                Segment(2, -89),
            ],
        };
        var left = new WallSegment { Name = "left triangle", MarkerSegmentIndex = 1 };
        var right = new WallSegment { Name = "right triangle", MarkerSegmentIndex = 2 };

        WallGeometrySummary.ApplyMeasured(document, [left, right]);

        Assert.Equal(AtticLeftTriangleYaw, left.MeasuredYaw);
        Assert.Equal(-89, right.MeasuredYaw);
    }

    [Fact]
    public void MeasuredFor_ReadsThePrimaryFacetYawWithoutFlipping()
    {
        var (_, yaw) = WallGeometrySummary.MeasuredFor(Segment(1, AtticLeftTriangleYaw));

        Assert.Equal(AtticLeftTriangleYaw, yaw);
    }

    [Fact]
    public void SurfaceYaw_ReadsTheLeftSideSurfaceAsTurnedLeft()
    {
        Assert.Equal(SurfaceTurn.Left, SurfaceYaw.TurnOf(AtticLeftTriangleYaw));
        Assert.Equal("89° left", SurfaceYaw.Describe(AtticLeftTriangleYaw));
        Assert.Equal(SurfaceTurn.Right, SurfaceYaw.TurnOf(-AtticLeftTriangleYaw));
    }

    [Fact]
    public void FacetRows_CarryTheSolverYawUnchanged()
    {
        var document = new WallGeometryDocument { Segments = [Segment(1, AtticLeftTriangleYaw)] };

        var row = Assert.Single(WallGeometrySummary.FacetRows(document));

        Assert.Equal(AtticLeftTriangleYaw, row.YawDeg);
    }

    private static WallGeometrySegment Segment(int index, double yaw) => new()
    {
        Index = index,
        Name = $"segment {index}",
        Facets = [new WallGeometryFacet { Id = index.ToString(System.Globalization.CultureInfo.InvariantCulture), YawDeg = yaw }],
    };
}
