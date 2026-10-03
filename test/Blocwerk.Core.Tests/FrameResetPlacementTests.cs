// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Text.Json.Nodes;
using Blocwerk.Core.Capture;
using Blocwerk.Core.Detection.Enrichment;
using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>
/// A model that starts a new frame (a wall without markers upgraded to markers: <see cref="FrameLineage"/>): hold placements
/// from the earlier frame are never carried through world space onto it. A hold its panel photo places again is placed
/// fresh; one it does not loses its old-frame placement (not measured) instead of keeping coordinates of another frame.
/// </summary>
public class FrameResetPlacementTests
{
    [Fact]
    public async Task AfterAFrameReset_OldPlacementsAreNotCarried_AndUnplacedHoldsAreNotMeasured()
    {
        using var h = new WallTestHarness();
        var s = await HoldPlacementScenario.CreateAsync(h);
        var left = await s.AddHoldAsync(0.25, 0.5);
        var right = await s.AddHoldAsync(0.75, 0.2);
        var service = s.Service();
        await service.PlaceAsync(h.WallId);
        var before = await s.LoadHoldsAsync();

        // The new model's textures (first bytes 2 and 3) are not matched by photo c0 at all.
        var model = await ResetAsync(h, s);
        var result = await service.PlaceFromPipelineAsync(h.WallId, model, h.Owner.Id);

        var c0 = result!.Panels.Single(p => p.PanelId == s.PanelC0);
        Assert.Equal((0, 0), (c0.Placed, c0.Carried));
        var holds = await s.LoadHoldsAsync();
        Assert.All(new[] { left, right }, id =>
        {
            Assert.Null(holds[id].FacetId);
            Assert.Null(holds[id].PlaneAMm);
            Assert.Equal(HoldMetric.TextureRegistrationRejected, holds[id].MetricSource);
        });

        await service.RevertAsync(h.WallId, result.RunId);
        var after = await s.LoadHoldsAsync();
        Assert.Equivalent(before[left], after[left]);
    }

    [Fact]
    public async Task AfterAFrameReset_AHoldThePhotoRegistersAgain_IsPlacedFresh()
    {
        using var h = new WallTestHarness();
        var s = await HoldPlacementScenario.CreateAsync(h);
        var left = await s.AddHoldAsync(0.25, 0.5);
        var right = await s.AddHoldAsync(0.75, 0.2);
        var service = s.Service();
        await service.PlaceAsync(h.WallId);

        // Photo c0 registers the new facet 0 (its frame 10 mm along u), not facet 1.
        var model = await ResetAsync(h, s);
        s.Matcher.Views.Add(new FakeTextureView(10, 2, 0, 2000, 100, HoldPlacementScenario.Shift(99.5 - 10)));
        var result = await service.PlaceFromPipelineAsync(h.WallId, model, h.Owner.Id);

        var c0 = result!.Panels.Single(p => p.PanelId == s.PanelC0);
        Assert.Equal((1, 0), (c0.Placed, c0.Carried));
        var holds = await s.LoadHoldsAsync();
        Assert.Equal(("0", HoldMetric.TextureRegistration), (holds[left].FacetId, holds[left].MetricSource));
        Assert.Equal(990, holds[left].PlaneAMm!.Value, 3);
        Assert.Null(holds[right].FacetId);
    }

    [Fact]
    public async Task SameFrame_IsTheResetModelAndWhatIsTiedToIt_AndNullWithoutAReset()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        var json = HoldPlacementScenario.TwoFacetJson;
        await using var db = h.CreateContext();
        var old = Model(h, json);
        var registeredToOld = Model(h, Registered(json, old.Id));
        var reset = Model(h, FrameLineage.StampReset(json, registeredToOld.Id, "no markers"));
        var correction = Model(h, FrameLineage.StampReset(json, registeredToOld.Id, "no markers"));
        correction.DerivedFromModelId = reset.Id;
        var recapture = Model(h, Registered(json, correction.Id));
        db.WallGeometryModels.AddRange(old, registeredToOld, reset, correction, recapture);
        await db.SaveChangesAsync();

        Assert.Null(await FrameLineage.SameFrameAsync(db, h.WallId, registeredToOld.Id, default));
        var frame = await FrameLineage.SameFrameAsync(db, h.WallId, recapture.Id, default);
        Assert.Equal(new[] { correction.Id, recapture.Id }.Order(), frame!.Order());
        var fromReset = await FrameLineage.SameFrameAsync(db, h.WallId, reset.Id, default);
        Assert.Equal(new[] { reset.Id, correction.Id, recapture.Id }.Order(), fromReset!.Order());
        Assert.True(FrameLineage.IsReset(reset.Json));
        Assert.False(FrameLineage.IsReset(old.Json));
    }

    private static WallGeometryModel Model(WallTestHarness h, string json) =>
        new() { WallId = h.WallId, Json = json, SchemaVersion = 1, Source = "test", IsActive = false };

    private static string Registered(string json, Guid referenceId)
    {
        var root = JsonNode.Parse(json)!.AsObject();
        root["quality"] = new JsonObject { ["registration"] = new JsonObject { ["referenceModelId"] = referenceId.ToString() } };
        return root.ToJsonString();
    }

    /// <summary>A new active model in a NEW frame (facet 0's origin 10 mm along u) with textures photo c0 does not match by itself.</summary>
    private static async Task<Guid> ResetAsync(WallTestHarness h, HoldPlacementScenario s)
    {
        s.Files.ReadAsync("n0.jpg", Arg.Any<CancellationToken>()).Returns(new byte[] { 2 });
        s.Files.ReadAsync("n1.jpg", Arg.Any<CancellationToken>()).Returns(new byte[] { 3 });
        await using var db = h.CreateContext();
        var previous = await db.WallGeometryModels.SingleAsync(m => m.IsActive);
        previous.IsActive = false;
        var json = HoldPlacementScenario.TwoFacetJson.Replace(
            "\"id\": \"0\", \"origin\": [0, 0, 0]", "\"id\": \"0\", \"origin\": [10, 0, 0]", StringComparison.Ordinal);
        var model = new WallGeometryModel
        {
            WallId = h.WallId, Json = FrameLineage.StampReset(json, previous.Id, "the active model has no markers to tie to"),
            SchemaVersion = 1, Source = "test", IsActive = true,
        };
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
