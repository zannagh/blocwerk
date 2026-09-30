// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Entities;
using Blocwerk.Core.Geometry;
using Blocwerk.Core.Geometry.View3D;
using Blocwerk.Core.MarkerPlanning;

namespace Blocwerk.Core.Tests.View3D;

/// <summary>
/// The Attic's active model (c6d007b8): the "sidewall" triangle reaches the floor next to the kickboard, as its plan
/// says, and neighbouring facets meet at their seams instead of poking through each other ("corner" through
/// "closing up space", the kickboard past the sidewall) (<see cref="FacetFloorReach"/>, <see cref="FacetSeamTrim"/>).
/// </summary>
public class Wall3DFacetSeamAtticTests
{
    private static readonly Dictionary<int, PlanTriangle> Triangles = new()
    {
        [1] = new(0, TriangleCorner.BottomLeft),
        [5] = new(2, TriangleCorner.BottomRight),
        [7] = new(6, TriangleCorner.BottomRight),
    };

    private static readonly WallGeometryDocument Model =
        WallGeometryDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "View3D", "attic-active-model.json")));

    [Fact]
    public void Sidewall_ReachesTheKickboardsBottom_WithItsHypotenuseOnTheMainWall()
    {
        var view = Build(Triangles);
        var side = Facet(view, "1");
        var kickboard = Facet(view, "3");

        // Before: its bottom edge ended at z −152, 170 mm above the kickboard's (−322).
        Assert.Equal(kickboard.Corners.Min(c => c[2]), side.Corners.Min(c => c[2]), 5.0);
        Assert.Equal(2, side.Corners.Count(c => c[2] < -300));

        // The hypotenuse stays on the main wall's plane; below it the side wall meets the kickboard, not behind it.
        Assert.Equal(2, side.Corners.Count(c => Math.Abs(Offset(c, Facet(view, "0"))) < 3));
        AssertInFront(side, kickboard);
        AssertInFront(side, Facet(view, "0"));
    }

    [Fact]
    public void Sidewall_KeepsItsSolvedBottom_WithoutPlanTriangles()
    {
        var side = Facet(Build(new Dictionary<int, PlanTriangle>()), "1");

        Assert.Equal(-152, side.Corners.Min(c => c[2]), 5.0);
    }

    [Fact]
    public void Corner_StopsAtBothClosingPieces()
    {
        var view = Build(Triangles);
        var corner = Facet(view, "6");

        // Before: it ran ~50 mm through "closing up space" (5a) and ~40 mm through "closing up end" (7a).
        AssertInFront(corner, Facet(view, "5a"));
        AssertInFront(corner, Facet(view, "7a"));
        Assert.Contains(corner.Corners, c => Math.Abs(Offset(c, Facet(view, "5a"))) < 3);
    }

    [Fact]
    public void KickboardAndMainWall_StopAtTheSidewall()
    {
        var view = Build(Triangles);
        var side = Facet(view, "1");

        AssertInFront(Facet(view, "3"), side);
        AssertInFront(Facet(view, "0"), side);
    }

    [Fact]
    public void KickboardMeetsTheMainWall_WithoutAGap()
    {
        var view = Build(Triangles);
        var kickboard = Facet(view, "3");
        var main = Facet(view, "0");

        // The kickboard's top follows the main wall's seam, not the nearly coplanar "leftover bit"'s, which would cut
        // it ~45 mm lower at the far end of the wall.
        var top = kickboard.Corners.Where(c => c[0] < 100).MaxBy(c => c[2])!;
        var bottom = main.Corners.Where(c => c[0] < 100).MinBy(c => c[2])!;
        Assert.Equal(bottom[2], top[2], 3.0);
    }

    [Fact]
    public void FarFacets_AreNotTrimmed()
    {
        // "why is it there" sits on top of the overhang, far from any facet's seam margin but the leftover bit's.
        var facet = Facet(Build(Triangles), "4");

        Assert.Null(facet.Outline);
        Assert.Equal(4, facet.Corners.Count);
    }

    private static Wall3DView Build(Dictionary<int, PlanTriangle> triangles) =>
        Wall3DViewBuilder.Build(new Wall { Name = "The Attic" }, Model, null, planTriangles: triangles);

    private static Wall3DFacet Facet(Wall3DView view, string id) => view.Facets.Single(f => f.Id == id);

    private static double Offset(double[] p, Wall3DFacet plane) =>
        Enumerable.Range(0, 3).Sum(i => plane.Normal[i] * (p[i] - plane.Origin[i]));

    /// <summary>Every corner lies on or in front of <paramref name="plane"/> (along its room-facing normal), within 3 mm.</summary>
    private static void AssertInFront(Wall3DFacet facet, Wall3DFacet plane)
    {
        foreach (var p in facet.Corners)
        {
            var d = Offset(p, plane);
            Assert.True(d > -3, $"{facet.Name} corner ({p[0]:F0}, {p[1]:F0}, {p[2]:F0}) is {-d:F1} mm behind {plane.Name}");
        }
    }
}
