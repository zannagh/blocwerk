// <copyright file="Wall3DHoldTwinTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Geometry;
using Blocwerk.Core.Geometry.View3D;
using Blocwerk.Core.Services;

namespace Blocwerk.Core.Tests;

/// <summary>
/// Two overlapping panels each store their own copy of the holds in the overlap. The 3D view draws each
/// physical hold once: stored links first, then same-colour, same-size copies of different panels that
/// land on top of each other on one facet.
/// </summary>
public class Wall3DHoldTwinTests
{
    private static readonly Guid Left = Guid.NewGuid();
    private static readonly Guid Right = Guid.NewGuid();

    [Fact]
    public void ExplicitLink_MergesCopiesTooFarApartForGeometry_ButNotAnImplausibleLink()
    {
        var wall = NewWall();
        var a = AddHold(wall, Left, 1000, 1000, "blue");
        var b = AddHold(wall, Right, 1090, 1000, "blue");   // 90 mm off: the panels' mappings disagree
        var far = AddHold(wall, Left, 3000, 3000, "red");
        var farTwin = AddHold(wall, Right, 200, 200, "red"); // a wrong link, metres away

        var view = Build(wall, null, new HoldLinkPair(a.Id, b.Id), new HoldLinkPair(far.Id, farTwin.Id));

        Assert.Equal(3, view.Holds.Count);
        Assert.Equal(1, view.MultiPanelHoldCount);
        var merged = Assert.Single(view.Holds, h => h.DuplicateIds is not null);
        Assert.Equal(new[] { a.Id, b.Id }.Order(), new[] { merged.Id, merged.DuplicateIds![0] }.Order());
        Assert.Contains(view.Holds, h => h.Id == far.Id);
        Assert.Contains(view.Holds, h => h.Id == farTwin.Id);
        Assert.Equal("3 holds (1 seen on two panels)", Wall3DHoldCountText.Format(view));
    }

    [Fact]
    public void Geometry_MergesSameColourSameSizeCopiesOfDifferentPanels()
    {
        var wall = NewWall();
        AddHold(wall, Left, 1000, 1000, "blue", size: 100);
        AddHold(wall, Right, 1020, 1015, "blue", size: 110);  // 25 mm ≤ 0.35 × 100
        AddHold(wall, Right, 1500, 1000, "blue", size: 100);  // elsewhere
        AddHold(wall, Left, 2000, 2000, "green", size: 100);
        AddHold(wall, Right, 2000, 2000, "green", size: 200); // too different in size

        var twins = HoldTwinMerger.Group(Candidates(wall), null);

        Assert.Equal(1, twins.GeometricMerges);
        Assert.Equal(0, twins.ExplicitMerges);
        Assert.Equal(4, twins.Groups.Count);
    }

    [Fact]
    public void Geometry_NeverMergesDifferentColours()
    {
        var wall = NewWall();
        AddHold(wall, Left, 1000, 1000, "blue");
        AddHold(wall, Right, 1000, 1000, "red");

        var view = Build(wall, null);

        Assert.Equal(2, view.Holds.Count);
        Assert.Equal(0, view.MultiPanelHoldCount);
        Assert.Equal("2 holds", Wall3DHoldCountText.Format(view));
    }

    [Fact]
    public void Geometry_NeverMergesTwoHoldsOfOnePanel()
    {
        var wall = NewWall();
        AddHold(wall, Left, 1000, 1000, "blue");
        AddHold(wall, Left, 1010, 1000, "blue");
        AddHold(wall, Right, 1005, 1000, "blue");  // pairs with ONE of them, not both

        var view = Build(wall, null);

        Assert.Equal(2, view.Holds.Count);
        Assert.Equal(1, view.MultiPanelHoldCount);
        Assert.Single(view.Holds.Single(h => h.DuplicateIds is not null).DuplicateIds!);
    }

    [Fact]
    public void Boulder_OnTheHiddenCopy_HighlightsTheMergedHold()
    {
        var wall = NewWall();
        var edge = AddHold(wall, Left, 1000, 1000, "blue", x: 0.95, y: 0.9); // near its photo's edge
        var centred = AddHold(wall, Right, 1005, 1000, "blue", x: 0.52, y: 0.5);
        var boulder = new Boulder { Name = "B", WallId = wall.Id };
        boulder.BoulderHolds.Add(new BoulderHold { BoulderId = boulder.Id, HoldId = edge.Id, Type = HoldType.Start });
        wall.Boulders.Add(boulder);

        var view = Build(wall, boulder.Id);

        var drawn = Assert.Single(view.Holds);
        Assert.Equal(centred.Id, drawn.Id);            // the better-centred copy represents it
        Assert.Equal(new[] { edge.Id }, drawn.DuplicateIds);
        Assert.Equal(Wall3DHoldRole.Start, drawn.Role);
        Assert.Equal(1, drawn.UsageCount);
    }

    [Fact]
    public void Representative_PrefersAnOutline_ThenTheSquarerView_ThenThePhotoCentre()
    {
        var wall = NewWall();
        var circle = AddHold(wall, Left, 1000, 1000, "blue", x: 0.5, y: 0.5);
        var outlined = AddHold(wall, Right, 1000, 1000, "blue", x: 0.9, y: 0.9);
        outlined.ShapePoints = ShapePoint.DefaultOctagon(0.01);
        Assert.Equal(outlined.Id, HoldTwinMerger.Group(Candidates(wall), null).Groups.Single()[0].Hold.Id);

        var placed = Candidates(wall);
        var oblique = placed.Single(c => c.Hold.Id == circle.Id) with { Tilt = 0.8 };
        var square = placed.Single(c => c.Hold.Id == outlined.Id) with { Tilt = 0.1 };
        var noOutline = square with { Placed = square.Placed with { Shape = oblique.Placed.Shape } };
        Assert.Equal(outlined.Id, HoldTwinMerger.Order([oblique, noOutline])[0].Hold.Id);
    }

    [Fact]
    public void Tilt_IsZeroSquareOn_AndGrowsWithPerspective()
    {
        Assert.Equal(0, PhotoViewTilt.At((x, y) => (2000 * x + 300 * y, -1500 * y), 0.5, 0.5)!.Value, 1e-6);

        PhotoToPlane oblique = (x, y) => (2000 * x / (1 + 0.6 * x), -1500 * y / (1 + 0.6 * x));
        Assert.True(PhotoViewTilt.At(oblique, 0.5, 0.5) > 0.3);
    }

    private static Wall3DView Build(Wall wall, Guid? boulderId, params HoldLinkPair[] links) =>
        Wall3DViewBuilder.Build(wall, WallGeometryDocument.Parse(Wall3DViewBuilderTests.SolvedJson), boulderId, null, links);

    /// <summary>The builder's placed rows, grouped by the merger directly (no boulder, no links).</summary>
    private static List<HoldTwinCandidate> Candidates(Wall wall)
    {
        var frame = FacetFrame.From(WallGeometryDocument.Parse(Wall3DViewBuilderTests.SolvedJson).FindFacet("0")!.Value.Facet)!;
        return wall.Holds.Select(h =>
        {
            var placed = new Wall3DHold(
                h.Id, "0", frame.ToWorld(h.PlaneAMm!.Value, h.PlaneBMm!.Value), h.PlaneAMm.Value, h.PlaneBMm.Value,
                h.WidthMm!.Value, h.HeightMm!.Value, true, h.Color, h.Color ?? string.Empty, "#000", false, 0, null);
            return new HoldTwinCandidate(h, placed with { Shape = HoldShapeProjector.Project(h, h.WidthMm.Value, h.HeightMm.Value, null) }, null);
        }).ToList();
    }

    private static Wall NewWall() => new() { Name = "Two panels", CurrentGeneration = 1 };

    private static Hold AddHold(
        Wall wall, Guid panel, double a, double b, string color, double size = 100, double x = 0.5, double y = 0.5)
    {
        var hold = new Hold
        {
            WallId = wall.Id, WallPanelId = panel, FacetId = "0", PlaneAMm = a, PlaneBMm = b,
            WidthMm = size, HeightMm = size, Color = color, Generation = 1, X = x, Y = y,
        };
        wall.Holds.Add(hold);
        return hold;
    }
}
