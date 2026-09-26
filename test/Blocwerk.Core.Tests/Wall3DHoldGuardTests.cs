using Blocwerk.Core.Entities;
using Blocwerk.Core.Geometry;
using Blocwerk.Core.Geometry.View3D;

namespace Blocwerk.Core.Tests;

/// <summary>
/// <see cref="Wall3DHoldGuard"/>: a hold is never drawn off its facet. A stored footprint that puts the outline
/// off the facet (The Attic - COLMAP Improvements: carried-over holds whose outlines floated under the kickboard)
/// moves the hold onto the facet that contains the drawn point, else it counts as not measured.
/// </summary>
public class Wall3DHoldGuardTests
{
    /// <summary>Two coplanar facets side by side (x = a, z = b), and a low third one 500 mm in front of them.</summary>
    private const string Json = """
        {
          "version": 1, "units": "mm", "markerSizeMm": 125.0,
          "segments": [
            { "index": 0, "facets": [ { "id": "0", "origin": [0, 0, 0], "u": [1, 0, 0], "v": [0, 0, 1], "normal": [0, -1, 0],
              "extentMm": { "aMin": 0, "aMax": 2000, "bMin": 0, "bMax": 3000 } } ] },
            { "index": 1, "facets": [ { "id": "1", "origin": [2000, 0, 0], "u": [1, 0, 0], "v": [0, 0, 1], "normal": [0, -1, 0],
              "extentMm": { "aMin": 0, "aMax": 2000, "bMin": 0, "bMax": 3000 } } ] },
            { "index": 2, "facets": [ { "id": "2", "origin": [0, -500, -1500], "u": [1, 0, 0], "v": [0, 0, 1], "normal": [0, -1, 0],
              "extentMm": { "aMin": 0, "aMax": 4000, "bMin": 0, "bMax": 400 } } ] }
          ],
          "markers": []
        }
        """;

    [Fact]
    public void FootprintDrawnOffEveryFacet_IsNotDrawn_AndCountsAsNotMeasured()
    {
        var wall = new Wall { Name = "Test", CurrentGeneration = 1 };
        var ok = AddHold(wall, "0", 1000, 1000, 0, 0);
        var floating = AddHold(wall, "0", 1000, 700, -300, -1000); // drawn at b = −300, a metre under facet 0

        var view = Wall3DViewBuilder.Build(wall, WallGeometryDocument.Parse(Json), null);

        Assert.Equal(1, view.UnplacedHoldCount);
        Assert.DoesNotContain(view.Holds, h => h.Id == floating.Id);
        Assert.Contains(view.Holds, h => h.Id == ok.Id);
        AssertAllOnTheirFacets(view);
    }

    [Fact]
    public void FootprintDrawnOnANeighbouringFacet_MovesTheHoldThere_WithItsOutlineCentred()
    {
        var wall = new Wall { Name = "Test", CurrentGeneration = 1 };
        var moved = AddHold(wall, "0", 1900, 1500, 600, 20); // drawn at a = 2500 on facet 0's plane: facet 1, a = 500

        var view = Wall3DViewBuilder.Build(wall, WallGeometryDocument.Parse(Json), null);

        var drawn = Assert.Single(view.Holds);
        Assert.Equal(moved.Id, drawn.Id);
        Assert.Equal(0, view.UnplacedHoldCount);
        Assert.Equal("1", drawn.FacetId);
        Assert.Equal(500, drawn.PlaneA, 3);
        Assert.Equal(1520, drawn.PlaneB, 3);
        Assert.Equal((0.0, 0.0), Wall3DHoldGuard.OutlineOffset(drawn.Shape!.Outline));
        AssertAllOnTheirFacets(view);
    }

    [Fact]
    public void AFacetFarFromTheDrawnPoint_DoesNotTakeTheHold()
    {
        // Drawn at b = −1300 on facet 0's plane: facet 2's outline covers it, but its plane lies 500 mm in front.
        var wall = new Wall { Name = "Test", CurrentGeneration = 1 };
        AddHold(wall, "0", 1000, 200, 0, -1500);

        var view = Wall3DViewBuilder.Build(wall, WallGeometryDocument.Parse(Json), null);

        Assert.Empty(view.Holds);
        Assert.Equal(1, view.UnplacedHoldCount);
    }

    [Fact]
    public void PlacementOnFacet_ChecksTheCentreAndTheOutlineDrawnThere_WithTheMargin()
    {
        var extents = Wall3DFallbackPlacement.FacetExtents(WallGeometryDocument.Parse(Json));
        var square = Square(0, 0);

        Assert.True(Wall3DHoldGuard.PlacementOnFacet("0", 1000, 1000, extents, square));
        Assert.True(Wall3DHoldGuard.PlacementOnFacet("0", -40, 3040, extents));
        Assert.False(Wall3DHoldGuard.PlacementOnFacet("0", -60, 1000, extents));
        Assert.False(Wall3DHoldGuard.PlacementOnFacet("0", 1000, 700, extents, Square(-300, -1000)));
        Assert.False(Wall3DHoldGuard.PlacementOnFacet("0", 1000, double.NaN, extents));
        Assert.True(Wall3DHoldGuard.PlacementOnFacet("unknown", -9999, -9999, extents));
    }

    [Fact]
    public void PhotoOutlineOffTheFacet_IsRecognised()
    {
        var extent = new PlaneRectMm(0, 2000, 0, 3000);
        var hold = new Wall3DHold(Guid.NewGuid(), "0", [0, 0, 0], 1000, 700, 40, 40, true, null, "x", "#000", false, 0, null);

        Assert.True(Wall3DHoldGuard.OnFacet(hold, Square(10, -20), extent));
        Assert.False(Wall3DHoldGuard.OnFacet(hold, Square(-800, -985), extent));
    }

    /// <summary>A hold at (a, b) on <paramref name="facet"/> whose stored footprint is a 40 mm square centred at (da, db).</summary>
    internal static Hold AddHold(Wall wall, string facet, double a, double b, double da, double db)
    {
        var hold = new Hold
        {
            WallId = wall.Id, Generation = 1, X = 0.5, Y = 0.5, Radius = 0.02,
            FacetId = facet, PlaneAMm = a, PlaneBMm = b, WidthMm = 40, HeightMm = 40,
        };
        hold.FootprintMm = Footprint(hold, da, db);
        wall.Holds.Add(hold);
        return hold;
    }

    internal static string Footprint(Hold hold, double da, double db) =>
        new HoldFootprint(HoldFootprintSource.MultiView, 3, 60, null, HoldFootprint.KeyOf(hold), Square(da, db)).ToJson();

    private static List<double[]> Square(double da, double db) =>
        [[da - 20, db - 20], [da + 20, db - 20], [da + 20, db + 20], [da - 20, db + 20]];

    private static void AssertAllOnTheirFacets(Wall3DView view)
    {
        var extents = view.Facets.ToDictionary(f => f.Id, f => f.Extent);
        Assert.All(view.Holds, h =>
        {
            var (a, b) = Wall3DHoldGuard.DrawnCentre(h);
            Assert.True(Wall3DHoldGuard.Contains(extents[h.FacetId], a, b), $"{h.Id} drawn at ({a:F0}, {b:F0}) off facet {h.FacetId}");
        });
    }
}
