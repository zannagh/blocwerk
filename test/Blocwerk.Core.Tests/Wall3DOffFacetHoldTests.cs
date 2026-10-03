using Blocwerk.Core.Entities;
using Blocwerk.Core.Geometry;
using Blocwerk.Core.Geometry.View3D;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The 3D view's approximate placement (<see cref="Wall3DFallbackPlacement"/>) of a hold without a facet position:
/// drawn when its photo mapping lands on its facet, but counted as not measured — never drawn in the air — when
/// the mapping lands off every facet (The Attic's kickboard holds, extrapolated below the main wall).
/// </summary>
public class Wall3DOffFacetHoldTests
{
    [Fact]
    public void HoldMappedOffEveryFacet_IsNotDrawn_AndCountsAsNotMeasured()
    {
        var (wall, panelId) = WallWithPlacedPanel();
        var onFacet = Lost(wall, panelId, 0.4, 0.5);    // b = 1000: inside facet 0
        var offFacet = Lost(wall, panelId, 0.4, 0.95);  // b = −800: below facet 0 (bMin −50)

        var view = Wall3DViewBuilder.Build(wall, WallGeometryDocument.Parse(Wall3DViewBuilderTests.SolvedJson), null);

        Assert.Equal(1, view.UnplacedHoldCount);
        Assert.DoesNotContain(view.Holds, x => x.Id == offFacet.Id);
        var drawn = view.Holds.Single(x => x.Id == onFacet.Id);
        Assert.True(drawn.PlacementApproximate);
        Assert.Equal(1000, drawn.PlaneB, 1);
    }

    [Fact]
    public void Fallback_RefusesAPlacementBeyondTheMarginOfItsFacet()
    {
        var (wall, panelId) = WallWithPlacedPanel();
        var doc = WallGeometryDocument.Parse(Wall3DViewBuilderTests.SolvedJson);
        var frames = new Dictionary<string, FacetFrame> { ["0"] = FacetFrame.From(doc.FindFacet("0")!.Value.Facet)! };
        var projector = HoldPlaneProjector.Create(wall.Holds.Where(x => x.FacetId is not null), doc, null);
        var extents = Wall3DFallbackPlacement.FacetExtents(doc);

        // b = 3000 − 4000·y: bMin −50 plus the 50 mm margin is b = −100, y = 0.775
        Assert.NotNull(Wall3DFallbackPlacement.Place(Lost(wall, panelId, 0.4, 0.77), projector, extents, frames));
        Assert.Null(Wall3DFallbackPlacement.Place(Lost(wall, panelId, 0.4, 0.8), projector, extents, frames));
    }

    /// <summary>A panel with 12 placed holds: a = 1000·x + 100, b = 3000 − 4000·y on facet 0.</summary>
    private static (Wall Wall, Guid PanelId) WallWithPlacedPanel()
    {
        var panelId = Guid.NewGuid();
        var wall = new Wall { Name = "Test", CurrentGeneration = 2 };
        for (var i = 0; i < 12; i++)
        {
            var (x, y) = (0.1 + (0.07 * i), 0.1 + (0.1 * (i % 6)));
            wall.Holds.Add(new Hold
            {
                WallId = wall.Id, WallPanelId = panelId, Generation = 2, X = x, Y = y, Radius = 0.02,
                FacetId = "0", PlaneAMm = (1000 * x) + 100, PlaneBMm = 3000 - (4000 * y), WidthMm = 50, HeightMm = 50,
            });
        }

        return (wall, panelId);
    }

    private static Hold Lost(Wall wall, Guid panelId, double x, double y)
    {
        var hold = new Hold
        {
            WallId = wall.Id, WallPanelId = panelId, Generation = 2, X = x, Y = y, Radius = 0.02,
            ShapePoints = ShapePoint.DefaultOctagon(0.02),
        };
        wall.Holds.Add(hold);
        return hold;
    }
}
