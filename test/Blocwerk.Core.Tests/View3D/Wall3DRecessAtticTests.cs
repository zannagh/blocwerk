// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Entities;
using Blocwerk.Core.Geometry;
using Blocwerk.Core.Geometry.View3D;
using Blocwerk.Core.MarkerPlanning;

namespace Blocwerk.Core.Tests.View3D;

/// <summary>
/// The Attic's enclosed recess: "closing up space" (5a) and "closing up end" (7a) are triangles facing each other, standing
/// on the main wall with their hypotenuses on its plane (<see cref="Wall3DRecesses"/>).
/// </summary>
public class Wall3DRecessAtticTests
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
    public void Attic_HasOneRecess_BetweenTheClosingPieces()
    {
        var recess = Assert.Single(Build(Triangles).Recesses);

        Assert.Equal("0", recess.MainId);
        Assert.Equal("5a", recess.LeftId);
        Assert.Equal("7a", recess.ClosingId);
    }

    [Fact]
    public void Roof_LiesOnTheMainPlane_SpanningBothHypotenuses()
    {
        var view = Build(Triangles);
        var main = view.Facets.Single(f => f.Id == "0");
        var recess = view.Recesses.Single();

        Assert.Equal(4, recess.Roof.Count);
        Assert.All(recess.Roof, c => Assert.True(Math.Abs(Offset(c, main)) < 40));
        Assert.True(recess.Roof.Max(c => c[0]) > 6200);
        Assert.True(recess.Roof.Min(c => c[0]) < 5800);
    }

    [Fact]
    public void Volume_ContainsTheSlot_AndNotTheGymOutsideIt()
    {
        var recess = Build(Triangles).Recesses.Single();

        Assert.True(Inside(recess, [5995, 0, 500]));         // in the pocket, behind the roof plane
        Assert.False(Inside(recess, [6500, 0, 500]));        // behind the closing piece
        Assert.False(Inside(recess, [5995, 0, 1900]));       // above the pocket's top
        Assert.False(Inside(recess, [5995, -400, 100]));     // in front of the roof plane, in the room
        Assert.False(Inside(recess, [5995, 600, 500]));      // behind the pocket's back
        Assert.False(Inside(recess, [5400, 0, 500]));        // outside the left piece
    }

    [Fact]
    public void WithoutPlanTriangles_TheFacetsAreRectangles_AndNothingIsDetected()
    {
        Assert.Empty(Build(new Dictionary<int, PlanTriangle>()).Recesses);
    }

    [Fact]
    public void ARoomWithoutFacingTriangles_HasNoRecess()
    {
        var one = new Dictionary<int, PlanTriangle> { [5] = new(2, TriangleCorner.BottomRight) };

        Assert.Empty(Build(one).Recesses);
    }

    private static bool Inside(Wall3DRecess recess, double[] p) =>
        recess.Planes.All(n => (n[0] * p[0]) + (n[1] * p[1]) + (n[2] * p[2]) <= n[3]);

    private static Wall3DView Build(Dictionary<int, PlanTriangle> triangles) =>
        Wall3DViewBuilder.Build(new Wall { Name = "The Attic" }, Model, null, planTriangles: triangles);

    private static double Offset(double[] p, Wall3DFacet plane) =>
        Enumerable.Range(0, 3).Sum(i => plane.Normal[i] * (p[i] - plane.Origin[i]));
}
