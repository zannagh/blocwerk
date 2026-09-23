// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Geometry;
using Blocwerk.Core.Geometry.View3D;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The pure half of the 3D view: plane→world maths against the schema's definition
/// (<c>O + a·u + b·v</c>, normal toward the climber), facet quads, hold placement and boulder roles.
/// </summary>
public class Wall3DViewBuilderTests
{
    /// <summary>
    /// Facet 0 and marker 0 as the real solve of the owner's wall reported them (45° overhang). The
    /// marker's world corners were written by the solver independently of our maths, so mapping its
    /// plane corners must reproduce them.
    /// </summary>
    internal const string SolvedJson = """
        {
          "version": 1, "units": "mm", "markerSizeMm": 125.0,
          "segments": [
            { "index": 0, "name": "main wall", "declaredAngleDeg": 45.0, "measuredAngleDeg": 45.377,
              "facets": [ { "id": "0", "origin": [-52.66, 0.0, 0.0], "u": [1.0, 0.0, 0.0],
                "v": [0.0, -0.711746, 0.702437], "normal": [0.0, -0.702437, -0.711746],
                "measuredAngleDeg": 45.377,
                "extentMm": { "aMin": -50.0, "aMax": 5135.6, "bMin": -50.0, "bMax": 3388.9 } } ] },
            { "index": 5, "name": "right piece",
              "facets": [
                { "id": "5a", "origin": [3100, 0, 0], "u": [0.8, 0.6, 0], "v": [0, 0, 1], "normal": [0.6, -0.8, 0] },
                { "id": "5b", "origin": [3900, 600, 0], "u": [0, 1, 0], "v": [0, 0, 1] } ] }
          ],
          "markers": [
            { "id": 0, "segment": 0, "role": "TL", "facet": "0",
              "cornersPlaneMm": [[0.0, 3199.33], [124.89, 3204.59], [130.15, 3079.7], [5.26, 3074.44]],
              "cornersWorldMm": [[-52.66, -2277.11, 2247.33], [72.23, -2280.85, 2251.02], [77.49, -2191.96, 2163.29], [-47.4, -2188.22, 2159.6]] },
            { "id": 30, "segment": 5, "role": "TL", "facet": "5a",
              "cornersPlaneMm": [[0, 1000], [125, 1000], [125, 875], [0, 875]] }
          ]
        }
        """;

    [Fact]
    public void PlaneToWorld_ReproducesTheSolversMarkerCorners()
    {
        var doc = WallGeometryDocument.Parse(SolvedJson);
        var frame = FacetFrame.From(doc.FindFacet("0")!.Value.Facet)!;
        var marker = doc.FindMarker(0)!;

        for (var i = 0; i < 4; i++)
        {
            var world = frame.ToWorld(marker.CornersPlaneMm[i][0], marker.CornersPlaneMm[i][1]);
            for (var k = 0; k < 3; k++)
            {
                Assert.Equal(marker.CornersWorldMm![i][k], world[k], 0.05);
            }
        }
    }

    [Fact]
    public void FacetFrame_LiftsAlongTheNormal_AndDerivesAMissingNormalAsUCrossV()
    {
        var doc = WallGeometryDocument.Parse(SolvedJson);

        var main = FacetFrame.From(doc.FindFacet("0")!.Value.Facet)!;
        var lifted = main.ToWorld(0, 0, 100);
        Assert.Equal(-52.66, lifted[0], 1e-6);
        Assert.Equal(-70.2437, lifted[1], 1e-3);
        Assert.Equal(-71.1746, lifted[2], 1e-3);

        // u = +y, v = +z  →  u × v = +x.
        var folded = FacetFrame.From(doc.FindFacet("5b")!.Value.Facet)!;
        Assert.Equal(new[] { 1.0, 0.0, 0.0 }, folded.Normal);
    }

    [Fact]
    public void Facets_UseExtentForTheQuad_AndFallBackToMarkerBoundsWithoutOne()
    {
        var view = Wall3DViewBuilder.Build(NewWall(), WallGeometryDocument.Parse(SolvedJson), null);

        var main = view.Facets.Single(f => f.Id == "0");
        Assert.Equal("main wall", main.Name);
        Assert.Equal(45.377, main.AngleDeg);
        Assert.Equal(-102.66, main.Corners[0][0], 1e-6); // aMin = -50 from origin x -52.66
        Assert.Equal(5082.94, main.Corners[1][0], 1e-6);
        Assert.Equal(3388.9 * 0.702437, main.Corners[2][2], 1e-3);

        // 5a has no extent: its one marker's bounds (0..125 × 875..1000) plus the margin.
        var a = view.Facets.Single(f => f.Id == "5a");
        Assert.Equal("right piece (5a)", a.Name);
        Assert.Equal(new PlaneRectMm(-150, 275, 725, 1150), a.Extent);

        // 5b has neither extent nor markers nor holds: nothing to draw.
        Assert.DoesNotContain(view.Facets, f => f.Id == "5b");
        Assert.Single(view.Markers); // marker 30 has no world corners
        Assert.Empty(view.Textures);
        Assert.Null(view.SplatUrl);
    }

    [Fact]
    public void Holds_ArePlacedOnTheirFacet_OnlyWhenMeasuredAndLive()
    {
        var wall = NewWall();
        var placed = AddHold(wall, "0", 1000, 2000, width: 120, height: 80, color: "blue");
        var foot = AddHold(wall, "0", 500, 100, category: HoldCategory.Foot);
        AddHold(wall, null, null, null);                  // never measured
        AddHold(wall, "9", 10, 10);                       // facet the model does not know
        AddHold(wall, "0", 10, 10, generation: 3);        // staged (gen + 1) row

        var view = Wall3DViewBuilder.Build(wall, WallGeometryDocument.Parse(SolvedJson), null);

        Assert.Equal(2, view.Holds.Count);
        Assert.Equal(2, view.UnplacedHoldCount); // the staged row is not live at all

        var h = view.Holds.Single(x => x.Id == placed.Id);
        var expected = FacetFrame.From(WallGeometryDocument.Parse(SolvedJson).FindFacet("0")!.Value.Facet)!
            .ToWorld(1000, 2000, Wall3DViewBuilder.HoldLiftMm);
        Assert.Equal(expected, h.Position);
        Assert.Equal((120.0, 80.0, true), (h.WidthMm, h.HeightMm, h.SizeMeasured));
        Assert.Equal(("Blue", "#3366dd"), (h.ColorName, h.Hex));
        Assert.Null(h.Role);

        var f = view.Holds.Single(x => x.Id == foot.Id);
        Assert.True(f.IsFoot);
        Assert.False(f.SizeMeasured);
        Assert.Equal(Wall3DViewBuilder.DefaultFootSizeMm, f.WidthMm);
    }

    [Fact]
    public void Boulder_FillsRoles_AndUsageCountsOnlyLiveBoulders()
    {
        var wall = NewWall();
        var start = AddHold(wall, "0", 100, 100);
        var top = AddHold(wall, "0", 100, 3000);
        var hand = AddHold(wall, "0", 200, 1500);
        var footOnly = AddHold(wall, "0", 300, 50);
        var yellowFoot = AddHold(wall, "0", 400, 50, color: "yellow");
        var other = AddHold(wall, "0", 900, 900, color: "red");

        var boulder = AddBoulder(wall, footColor: "yellow",
            (start, HoldType.Start, HoldUsage.HandAndFoot),
            (top, HoldType.Top, HoldUsage.HandAndFoot),
            (hand, HoldType.Normal, HoldUsage.HandOnly),
            (footOnly, HoldType.Normal, HoldUsage.FootOnly));
        AddBoulder(wall, null, (start, HoldType.Start, HoldUsage.HandAndFoot));
        AddBoulder(wall, null, (start, HoldType.Start, HoldUsage.HandAndFoot)).IsArchived = true;

        var view = Wall3DViewBuilder.Build(wall, WallGeometryDocument.Parse(SolvedJson), boulder.Id);
        Wall3DHoldRole? RoleOf(Hold x) => view.Holds.Single(h => h.Id == x.Id).Role;

        Assert.Equal(boulder.Id, view.BoulderId);
        Assert.Equal(Wall3DHoldRole.Start, RoleOf(start));
        Assert.Equal(Wall3DHoldRole.Top, RoleOf(top));
        Assert.Equal(Wall3DHoldRole.Hand, RoleOf(hand));
        Assert.Equal(Wall3DHoldRole.Foot, RoleOf(footOnly));
        Assert.Equal(Wall3DHoldRole.ColorFoot, RoleOf(yellowFoot));
        Assert.Null(RoleOf(other));

        Assert.Equal(2, view.Holds.Single(h => h.Id == start.Id).UsageCount);
        Assert.Equal(0, view.Holds.Single(h => h.Id == other.Id).UsageCount);
    }

    [Fact]
    public void UnknownBoulder_LeavesEveryRoleEmpty()
    {
        var wall = NewWall();
        AddHold(wall, "0", 100, 100);
        var view = Wall3DViewBuilder.Build(wall, WallGeometryDocument.Parse(SolvedJson), Guid.NewGuid());

        Assert.Null(view.BoulderId);
        Assert.All(view.Holds, h => Assert.Null(h.Role));
    }

    private static Wall NewWall() => new() { Name = "Test", CurrentGeneration = 2 };

    private static Hold AddHold(
        Wall wall, string? facet, double? a, double? b, double? width = null, double? height = null,
        string? color = null, HoldCategory category = HoldCategory.Hand, int generation = 2)
    {
        var hold = new Hold
        {
            WallId = wall.Id, FacetId = facet, PlaneAMm = a, PlaneBMm = b, WidthMm = width, HeightMm = height,
            Color = color, Category = category, Generation = generation,
        };
        wall.Holds.Add(hold);
        return hold;
    }

    private static Boulder AddBoulder(Wall wall, string? footColor, params (Hold Hold, HoldType Type, HoldUsage Usage)[] holds)
    {
        var boulder = new Boulder { Name = "B", WallId = wall.Id, FootColorOnly = footColor };
        foreach (var (hold, type, usage) in holds)
        {
            boulder.BoulderHolds.Add(new BoulderHold { BoulderId = boulder.Id, HoldId = hold.Id, Type = type, Usage = usage });
        }

        wall.Boulders.Add(boulder);
        return boulder;
    }
}
