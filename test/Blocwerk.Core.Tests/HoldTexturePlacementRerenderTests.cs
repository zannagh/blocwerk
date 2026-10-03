// <copyright file="HoldTexturePlacementRerenderTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Detection.Enrichment;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>
/// "Render wall textures again" keeps the model id but makes a new texture set: what a run placed on the earlier textures
/// is not settled any more and is placed again on the new ones, while a model whose textures did not change keeps its
/// placements settled (also for runs recorded before the texture set was).
/// </summary>
public class HoldTexturePlacementRerenderTests
{
    [Fact]
    public async Task UnchangedTextures_ThePlacedHoldsStaySettled()
    {
        using var h = new WallTestHarness();
        var s = await HoldPlacementScenario.CreateAsync(h);
        await s.AddHoldAsync(0.25, 0.5);
        var service = s.Service();

        var first = await service.PlaceFromPipelineAsync(h.WallId, s.ModelId, h.Owner.Id);
        var again = await service.PlaceFromPipelineAsync(h.WallId, s.ModelId, h.Owner.Id);

        Assert.Equal(1, first!.Placed);
        Assert.Null(again);
        await using var db = h.CreateContext();
        var run = await db.HoldPlacementRuns.SingleAsync();
        Assert.Equal((await TextureSetStamp.OfModelAsync(db, s.ModelId, CancellationToken.None))!.Key, run.TextureSetKey);
    }

    [Fact]
    public async Task ARunRecordedBeforeTheTextureSetWas_StaysSettledOnUnchangedTextures()
    {
        using var h = new WallTestHarness();
        var s = await HoldPlacementScenario.CreateAsync(h);
        await s.AddHoldAsync(0.25, 0.5);
        var service = s.Service();
        await service.PlaceFromPipelineAsync(h.WallId, s.ModelId, h.Owner.Id);
        await ForgetTextureSetKeysAsync(h);

        Assert.Null(await service.PlaceFromPipelineAsync(h.WallId, s.ModelId, h.Owner.Id));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AfterTheTexturesAreRenderedAgain_TheHoldsArePlacedAgainOnThem(bool legacyRun)
    {
        using var h = new WallTestHarness();
        var s = await HoldPlacementScenario.CreateAsync(h);
        var left = await s.AddHoldAsync(0.25, 0.5);
        var right = await s.AddHoldAsync(0.75, 0.2);
        var service = s.Service();
        await service.PlaceFromPipelineAsync(h.WallId, s.ModelId, h.Owner.Id);
        if (legacyRun)
        {
            await ForgetTextureSetKeysAsync(h);
        }

        var before = await s.LoadHoldsAsync();

        // The new render: photo c0 registers the new facet 0 texture 20 mm further along; the facet 1 texture not at all.
        await RerenderAsync(h, s);
        s.Matcher.Views.Add(new FakeTextureView(10, 4, 0, 2000, 100, HoldPlacementScenario.Shift(99.5 + 20)));
        var result = await service.PlaceFromPipelineAsync(h.WallId, s.ModelId, h.Owner.Id);

        Assert.NotNull(result);
        var c0 = result.Panels.Single(p => p.PanelId == s.PanelC0);
        Assert.Equal((2, 1), (c0.Placed, c0.Carried));
        var holds = await s.LoadHoldsAsync();
        Assert.Equal(("0", HoldMetric.TextureRegistration), (holds[left].FacetId, holds[left].MetricSource));
        Assert.Equal(before[left].PlaneAMm!.Value + 20, holds[left].PlaneAMm!.Value, 3);

        // Not registered on the new textures: kept in place, but now as a carried placement this run checked.
        Assert.Equal(("1", HoldMetric.TextureRegistrationCarried), (holds[right].FacetId, holds[right].MetricSource));
        Assert.Equal(before[right].PlaneAMm!.Value, holds[right].PlaneAMm!.Value, 3);

        // The new run is on the new textures, so it is settled in turn.
        Assert.Null(await service.PlaceFromPipelineAsync(h.WallId, s.ModelId, h.Owner.Id));
        await using var db = h.CreateContext();
        var run = await db.HoldPlacementRuns.SingleAsync(r => r.Id == result.RunId);
        Assert.Equal((await TextureSetStamp.OfModelAsync(db, s.ModelId, CancellationToken.None))!.Key, run.TextureSetKey);
    }

    [Fact]
    public void TheKey_DependsOnTheStoredFilesOnly_AndALegacyRunCountsByItsTime()
    {
        var key = TextureSetStamp.KeyOf([("0", "a.jpg"), ("1", "b.jpg")]);
        var at = DateTimeOffset.UtcNow;
        var stamp = new TextureSetStamp(key, at);

        Assert.Equal(key, TextureSetStamp.KeyOf([("1", "b.jpg"), ("0", "a.jpg")]));
        Assert.NotEqual(key, TextureSetStamp.KeyOf([("0", "a.jpg"), ("1", "c.jpg")]));
        Assert.True(stamp.Covers(key, at.AddDays(-1)));
        Assert.False(stamp.Covers("other", at.AddDays(1)));
        Assert.True(stamp.Covers(null, at.AddSeconds(1)));
        Assert.False(stamp.Covers(null, at.AddSeconds(-1)));
    }

    /// <summary>Makes the runs look like they were recorded before <see cref="HoldPlacementRun.TextureSetKey"/> existed.</summary>
    private static async Task ForgetTextureSetKeysAsync(WallTestHarness h)
    {
        await using var db = h.CreateContext();
        foreach (var run in await db.HoldPlacementRuns.ToListAsync())
        {
            run.TextureSetKey = null;
        }

        await db.SaveChangesAsync();
    }

    /// <summary>"Render wall textures again": the same model's texture rows replaced by a new set (first bytes 4 and 5).</summary>
    internal static async Task RerenderAsync(WallTestHarness h, HoldPlacementScenario s)
    {
        s.Files.ReadAsync("r0.jpg", Arg.Any<CancellationToken>()).Returns(new byte[] { 4 });
        s.Files.ReadAsync("r1.jpg", Arg.Any<CancellationToken>()).Returns(new byte[] { 5 });
        await using var db = h.CreateContext();
        db.WallGeometryTextures.RemoveRange(await db.WallGeometryTextures.Where(t => t.GeometryModelId == s.ModelId).ToListAsync());
        foreach (var (facet, path) in new[] { ("0", "r0.jpg"), ("1", "r1.jpg") })
        {
            db.WallGeometryTextures.Add(new WallGeometryTexture
            {
                GeometryModelId = s.ModelId, FacetId = facet, StoredPath = path,
                AMin = -100, AMax = 2100, BMin = -100, BMax = 3100, WidthPx = 2200, HeightPx = 3200,
            });
        }

        await db.SaveChangesAsync();
    }
}
