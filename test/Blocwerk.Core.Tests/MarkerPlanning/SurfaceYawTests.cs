// <copyright file="SurfaceYawTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Geometry;
using Blocwerk.Core.MarkerPlanning;

namespace Blocwerk.Core.Tests.MarkerPlanning;

/// <summary>
/// Yaw is stored signed (the solver's counter-clockwise-from-above sense) but always said as a
/// direction: positive = turned left, negative = turned right.
/// </summary>
public class SurfaceYawTests
{
    private static readonly string GeometryPath = Path.Combine(AppContext.BaseDirectory, "MarkerPlanning", "attic-wall-geometry.json");

    [Theory]
    [InlineData(30, "30° left")]
    [InlineData(-90, "90° right")]
    [InlineData(0, "straight")]
    [InlineData(0.4, "straight")]
    [InlineData(-0.4, "straight")]
    [InlineData(12.34, "12.3° left")]
    [InlineData(double.NaN, "unknown turn")]
    public void Describe_NamesTheDirection_NeverASignedYaw(double yaw, string expected)
    {
        Assert.Equal(expected, SurfaceYaw.Describe(yaw));
    }

    [Fact]
    public void Signed_PutsTheSignOnTheTurn()
    {
        Assert.Equal(30, SurfaceYaw.Signed(SurfaceTurn.Left, 30));
        Assert.Equal(30, SurfaceYaw.Signed(SurfaceTurn.Left, -30));
        Assert.Equal(-45, SurfaceYaw.Signed(SurfaceTurn.Right, 45));
        Assert.Equal(0, SurfaceYaw.Signed(SurfaceTurn.Straight, 45));
        Assert.Equal(SurfaceTurn.Right, SurfaceYaw.TurnOf(-45));
        Assert.Equal(SurfaceTurn.Left, SurfaceYaw.TurnOf(45));
    }

    /// <summary>The Attic's side triangle hangs off the main wall's LEFT edge and the solver measures it at +89°.</summary>
    [Fact]
    public void SolvedLeftSideWall_ReadsAsTurnedLeft()
    {
        var plan = MarkerPlanFromGeometry.Build(WallGeometryDocument.Parse(File.ReadAllText(GeometryPath)), AtticMarkerPlan.Photo);
        var side = plan.Segments.Single(s => s.Index == 2);

        Assert.Equal(SurfaceTurn.Left, SurfaceYaw.TurnOf(side.YawDeg));
        Assert.Equal(SegmentEdge.Left, AtticMarkerPlan.Plan.Segments.Single(s => s.Index == 2).AttachedTo!.ParentEdge);
    }
}
