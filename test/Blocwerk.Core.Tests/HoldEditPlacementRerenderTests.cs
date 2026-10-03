// <copyright file="HoldEditPlacementRerenderTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Detection.Enrichment;
using Blocwerk.Core.Geometry.TextureRegistration;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The edited holds' placement and "Render wall textures again": a cached registration of the earlier textures is never
/// reused, and the rolling edit run recorded before the texture set was is adopted (or still found) instead of lost.
/// </summary>
public class HoldEditPlacementRerenderTests
{
    [Fact]
    public async Task AnEditAfterTheTexturesAreRenderedAgain_RegistersThePhotoOnTheNewTextures()
    {
        using var h = new WallTestHarness();
        var s = await HoldPlacementScenario.CreateAsync(h);
        var hold = await s.AddHoldAsync(0.25, 0.5);
        var service = s.Service();
        await service.PlaceAsync(h.WallId);
        var before = (await s.LoadHoldsAsync())[hold];

        await HoldTexturePlacementRerenderTests.RerenderAsync(h, s);
        s.Matcher.Views.Add(new FakeTextureView(10, 4, 0, 2000, 100, HoldPlacementScenario.Shift(99.5 + 20)));
        var opened = s.Matcher.Opened;
        var placed = await service.PlaceEditedAsync(h.WallId, [hold]);

        Assert.Single(placed);
        Assert.Equal(opened + 1, s.Matcher.Opened);
        var stored = (await s.LoadHoldsAsync())[hold];
        Assert.Equal(HoldMetric.TextureRegistration, stored.MetricSource);
        Assert.Equal(before.PlaneAMm!.Value + 20, stored.PlaneAMm!.Value, 3);
        await using var db = h.CreateContext();
        var run = await db.HoldPlacementRuns.SingleAsync(r => r.Trigger == HoldPlacementTrigger.Edit);
        Assert.Equal((await TextureSetStamp.OfModelAsync(db, s.ModelId, CancellationToken.None))!.Key, run.TextureSetKey);
    }

    [Fact]
    public async Task ALegacyRollingEditRun_IsAdopted_AndItsEntriesStillGetTheirVolumePlacement()
    {
        using var h = new WallTestHarness();
        var s = await HoldPlacementScenario.CreateAsync(h);
        var first = await s.AddHoldAsync(0.3, 0.45);
        var second = await s.AddHoldAsync(0.6, 0.45);
        var service = s.Service();
        await service.PlaceEditedAsync(h.WallId, [first]);
        await ForgetEditRunKeysAsync(h);

        await service.PlaceEditedAsync(h.WallId, [second]);
        await RecordAsync(h, s, new Dictionary<Guid, string?> { [first] = "{\"prev\":1}" });

        await using var db = h.CreateContext();
        var run = await db.HoldPlacementRuns.SingleAsync(r => r.Trigger == HoldPlacementTrigger.Edit);
        Assert.Equal((await TextureSetStamp.OfModelAsync(db, s.ModelId, CancellationToken.None))!.Key, run.TextureSetKey);
        var entries = HoldPlacementEntry.FromJson(run.HoldsJson).ToDictionary(e => e.HoldId);
        Assert.Equal(new HashSet<Guid> { first, second }, entries.Keys.ToHashSet());
        Assert.Equal((true, "{\"prev\":1}"), (entries[first].RestoresVolumePlacement, entries[first].PrevVolumePlacementJson));
        Assert.False(entries[second].RestoresVolumePlacement);
    }

    [Fact]
    public async Task AfterARerender_AVolumePlacementIsRecordedInTheEditRunThatHasTheHold()
    {
        using var h = new WallTestHarness();
        var s = await HoldPlacementScenario.CreateAsync(h);
        var first = await s.AddHoldAsync(0.3, 0.45);
        var second = await s.AddHoldAsync(0.2, 0.45);
        var service = s.Service();
        await service.PlaceEditedAsync(h.WallId, [first]);
        await ForgetEditRunKeysAsync(h);

        await HoldTexturePlacementRerenderTests.RerenderAsync(h, s);
        s.Matcher.Views.Add(new FakeTextureView(10, 4, 0, 2000, 100, HoldPlacementScenario.Shift(99.5 + 20)));
        await service.PlaceEditedAsync(h.WallId, [second]);
        await RecordAsync(h, s, new Dictionary<Guid, string?> { [first] = "{\"a\":1}", [second] = "{\"b\":2}" });

        await using var db = h.CreateContext();
        var runs = await db.HoldPlacementRuns.Where(r => r.Trigger == HoldPlacementTrigger.Edit).ToListAsync();
        Assert.Equal(2, runs.Count);
        var legacy = HoldPlacementEntry.FromJson(runs.Single(r => r.TextureSetKey is null).HoldsJson).Single();
        var current = HoldPlacementEntry.FromJson(runs.Single(r => r.TextureSetKey is not null).HoldsJson).Single();
        Assert.Equal((first, "{\"a\":1}"), (legacy.HoldId, legacy.PrevVolumePlacementJson));
        Assert.Equal((second, "{\"b\":2}"), (current.HoldId, current.PrevVolumePlacementJson));
    }

    private static async Task RecordAsync(WallTestHarness h, HoldPlacementScenario s, Dictionary<Guid, string?> before)
    {
        await using var db = h.CreateContext();
        await HoldTexturePlacementService.RecordVolumePlacementsAsync(db, h.WallId, s.ModelId, before, CancellationToken.None);
    }

    /// <summary>Makes the edit runs look like they were recorded before the texture set was.</summary>
    private static async Task ForgetEditRunKeysAsync(WallTestHarness h)
    {
        await using var db = h.CreateContext();
        foreach (var run in await db.HoldPlacementRuns.Where(r => r.Trigger == HoldPlacementTrigger.Edit).ToListAsync())
        {
            run.TextureSetKey = null;
        }

        await db.SaveChangesAsync();
    }
}
