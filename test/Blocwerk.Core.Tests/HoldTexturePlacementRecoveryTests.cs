using Blocwerk.Core.Detection.Enrichment;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>
/// Placing holds when a panel photo does not register on its own: a re-captured model whose new textures the photo
/// no longer matches keeps the holds' previous placements (carried through the shared wall frame), and a photo whose
/// coarse match fails is found around the view its holds linked to another, registered photo predict.
/// </summary>
public class HoldTexturePlacementRecoveryTests
{
    /// <summary><see cref="HoldPlacementScenario.TwoFacetJson"/> re-solved: facet 0's frame origin moved 10 mm along its u axis.</summary>
    private static readonly string RecapturedJson = HoldPlacementScenario.TwoFacetJson.Replace(
        "\"id\": \"0\", \"origin\": [0, 0, 0]", "\"id\": \"0\", \"origin\": [10, 0, 0]", StringComparison.Ordinal);

    [Fact]
    public async Task FailedReRegistration_KeepsThePreviousPlacementsCarried_AndRevertRestoresThem()
    {
        using var h = new WallTestHarness();
        var s = await HoldPlacementScenario.CreateAsync(h);
        var left = await s.AddHoldAsync(0.25, 0.5);
        var right = await s.AddHoldAsync(0.75, 0.2);
        var service = s.Service();
        await service.PlaceAsync(h.WallId);
        var fresh = await s.AddHoldAsync(0.3, 0.5);
        var before = await s.LoadHoldsAsync();

        // The re-capture's textures (first bytes 2 and 3) are not matched by photo c0 at all.
        var model = await RecaptureAsync(h, s);
        var result = await service.PlaceFromPipelineAsync(h.WallId, model, h.Owner.Id);

        Assert.Equal((2, 1), (result!.Placed, result.Failed));
        var c0 = result.Panels.Single(p => p.PanelId == s.PanelC0);
        Assert.Equal((2, 2, 1), (c0.Placed, c0.Carried, c0.Failed));
        var holds = await s.LoadHoldsAsync();
        AssertCarried(holds[left], before[left], "0", 990, 1500);
        AssertCarried(holds[right], before[right], "1", 1000, 2400);
        Assert.Null(holds[fresh].FacetId);

        var revert = await service.RevertAsync(h.WallId, result.RunId);

        Assert.Equal(2, revert.Reverted);
        var after = await s.LoadHoldsAsync();
        Assert.Equivalent(before[left], after[left]);
        Assert.Equivalent(before[right], after[right]);
    }

    [Fact]
    public async Task LinkSeededRegistration_RecoversAPanelWhoseCoarseMatchFails_AndRevertRestoresIt()
    {
        using var h = new WallTestHarness();
        var s = await HoldPlacementScenario.CreateAsync(h);

        // Photo c1 (first byte 20) sees facet 0 shifted by 500 mm, but the coarse search never finds it.
        s.Matcher.Views.Add(new FakeTextureView(20, 0, 0, 1500, 100, HoldPlacementScenario.Shift(99.5 + 500), NeedsSeed: true));
        var c1Holds = new List<Guid>();
        await using (var db = h.CreateContext())
        {
            for (var i = 0; i < 10; i++)
            {
                var (x0, y) = ((700 + (130 * i)) / 4000.0, Y(i) / 3000.0);
                var onC0 = await s.AddHoldAsync(x0, y);
                var onC1 = await s.AddHoldAsync(x0 - (500 / 4000.0), y, s.PanelC1);
                db.HoldLinks.Add(new HoldLink { WallId = h.WallId, HoldAId = onC0, HoldBId = onC1 });
                c1Holds.Add(onC1);
            }

            await db.SaveChangesAsync();
        }

        var unlinked = await s.AddHoldAsync(0.25, 0.5, s.PanelC1);
        var service = s.Service();
        var result = await service.PlaceAsync(h.WallId);

        var c1 = result.Panels.Single(p => p.PanelId == s.PanelC1);
        Assert.Equal((11, 0, 0), (c1.Placed, c1.Carried, c1.Failed));
        Assert.True(c1.Facets.Single(f => f.FacetId == "0").Accepted);
        var holds = await s.LoadHoldsAsync();
        HoldTexturePlacementTests.AssertPlaced(holds[unlinked], "0", 1500, 1500);
        for (var i = 0; i < 10; i++)
        {
            HoldTexturePlacementTests.AssertPlaced(holds[c1Holds[i]], "0", 700 + (130 * i), 3000 - Y(i));
        }

        await service.RevertAsync(h.WallId, result.RunId);
        var after = await s.LoadHoldsAsync();
        Assert.All(c1Holds.Append(unlinked), id => Assert.Null(after[id].FacetId));
    }

    /// <summary>Photo y (px) of the i-th linked hold: spread over the photo, not on one line with x.</summary>
    private static double Y(int i) => 400 + ((i * 7 % 10) * 240);

    private static void AssertCarried(Hold hold, Hold before, string facet, double a, double b)
    {
        Assert.Equal(facet, hold.FacetId);
        Assert.Equal(a, hold.PlaneAMm!.Value, 3);
        Assert.Equal(b, hold.PlaneBMm!.Value, 3);
        Assert.Equal(HoldMetric.TextureRegistrationCarried, hold.MetricSource);
        Assert.Equal((before.WidthMm, before.HeightMm, before.AreaMm2), (hold.WidthMm, hold.HeightMm, hold.AreaMm2));
    }

    /// <summary>A new active model (<see cref="RecapturedJson"/>) with new textures photo c0 does not match; returns its id.</summary>
    private static async Task<Guid> RecaptureAsync(WallTestHarness h, HoldPlacementScenario s)
    {
        s.Files.ReadAsync("n0.jpg", Arg.Any<CancellationToken>()).Returns(new byte[] { 2 });
        s.Files.ReadAsync("n1.jpg", Arg.Any<CancellationToken>()).Returns(new byte[] { 3 });
        await using var db = h.CreateContext();
        (await db.WallGeometryModels.SingleAsync(m => m.IsActive)).IsActive = false;
        var model = new WallGeometryModel { WallId = h.WallId, Json = RecapturedJson, SchemaVersion = 1, Source = "test", IsActive = true };
        db.WallGeometryModels.Add(model);
        foreach (var (facet, path) in new[] { ("0", "n0.jpg"), ("1", "n1.jpg") })
        {
            db.WallGeometryTextures.Add(new WallGeometryTexture
            {
                GeometryModelId = model.Id, FacetId = facet, StoredPath = path,
                AMin = -100, AMax = 2100, BMin = -100, BMax = 3100, WidthPx = 2200, HeightPx = 3200,
            });
        }

        await db.SaveChangesAsync();
        return model.Id;
    }
}
