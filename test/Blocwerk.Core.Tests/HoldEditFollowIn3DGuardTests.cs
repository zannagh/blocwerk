// <copyright file="HoldEditFollowIn3DGuardTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Detection.Enrichment;
using Microsoft.EntityFrameworkCore;
using static Blocwerk.Core.Tests.FollowIn3D;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The edited holds' placement never writes over something newer: a 2D edit or a model switch that lands while the photo is
/// being registered wins, and a registration that matched nothing is not cached (the next edit tries again).
/// </summary>
public class HoldEditFollowIn3DGuardTests
{
    [Fact]
    public async Task EditLandingDuringTheRegistration_IsNotOverwritten()
    {
        using var h = new WallTestHarness();
        var s = await HoldPlacementScenario.CreateAsync(h);
        var hold = await s.AddHoldAsync(0.3, 0.45);
        s.Matcher.OnOpen = () =>
        {
            using var db = h.CreateContext();
            var row = db.Holds.Single(x => x.Id == hold);
            (row.X, row.Y) = (0.2, 0.2);
            db.SaveChanges();
        };

        var placed = await s.Service().PlaceEditedAsync(h.WallId, [hold]);

        var stored = (await s.LoadHoldsAsync())[hold];
        Assert.Empty(placed);
        Assert.Equal((0.2, 0.2), (stored.X, stored.Y));
        Assert.Null(stored.FacetId);
        Assert.Null(stored.MetricSource);
        Assert.Empty(await RunsAsync(h));
    }

    [Fact]
    public async Task ModelSwitchDuringTheRegistration_WritesNothing()
    {
        using var h = new WallTestHarness();
        var s = await HoldPlacementScenario.CreateAsync(h);
        var hold = await s.AddHoldAsync(0.3, 0.45);
        s.Matcher.OnOpen = () =>
        {
            using var db = h.CreateContext();
            db.WallGeometryModels.Single().IsActive = false;
            db.SaveChanges();
        };

        Assert.Empty(await s.Service().PlaceEditedAsync(h.WallId, [hold]));
        Assert.Null((await s.LoadHoldsAsync())[hold].FacetId);
    }

    [Fact]
    public async Task RegistrationThatMatchedNothing_IsNotCached()
    {
        using var h = new WallTestHarness();
        var s = await HoldPlacementScenario.CreateAsync(h);
        var hold = await s.AddHoldAsync(0.5, 0.5, s.PanelC1);

        await s.Service().PlaceEditedAsync(h.WallId, [hold]);
        await s.Service().PlaceEditedAsync(h.WallId, [hold]);

        Assert.Equal(2, s.Matcher.Opened);
        Assert.Null((await s.LoadHoldsAsync())[hold].FacetId);
    }

    [Fact]
    public async Task SettledPlacement_IsNotSettledForAnotherPanelGeometry()
    {
        using var h = new WallTestHarness();
        var s = await HoldPlacementScenario.CreateAsync(h);
        var hold = await s.AddHoldAsync(0.3, 0.45);
        await s.Service().PlaceEditedAsync(h.WallId, [hold]);
        await using (var db = h.CreateContext())
        {
            // A 2D move that kept the stored placement fields (as a stale write would have): the placement is not settled.
            (await db.Holds.SingleAsync(x => x.Id == hold)).X = 0.25;
            await db.SaveChangesAsync();
        }

        Assert.Single(await s.Service().PlaceEditedAsync(h.WallId, [hold]));
        var stored = (await s.LoadHoldsAsync())[hold];
        Assert.Equal((HoldMetric.TextureRegistration, 1000.0), (stored.MetricSource, Math.Round(stored.PlaneAMm!.Value, 1)));
    }
}
