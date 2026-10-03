// <copyright file="HoldPlacementConcurrentEditTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Geometry.TextureRegistration;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Blocwerk.Core.Tests;

/// <summary>A newer 2D edit always wins: a wall-wide placement run never writes over a move that lands while it writes.</summary>
public class HoldPlacementConcurrentEditTests
{
    [Fact]
    public async Task AMoveLandingMidWrite_IsNotOverwritten_AndTheHoldIsPlacedAgainAtItsNewPosition()
    {
        using var h = new WallTestHarness();
        var s = await HoldPlacementScenario.CreateAsync(h);
        var hold = await s.AddHoldAsync(0.25, 0.5);
        var factory = new MidWriteEditDbContextFactory(h.DbContextFactory.ConnectionString, () => MoveAsync(h, hold, 0.3));
        var service = new HoldTexturePlacementService(
            factory, h.CurrentUser, NullLogger<HoldTexturePlacementService>.Instance, s.Matcher, s.Files, s.Queue);

        await service.PlaceAsync(h.WallId);

        Assert.True(factory.Edited);
        var stored = (await s.LoadHoldsAsync())[hold];
        Assert.Equal((0.3, null, null), (stored.X, stored.FacetId, stored.PlaneAMm));
        await using (var db = h.CreateContext())
        {
            var run = await db.HoldPlacementRuns.SingleAsync();
            Assert.DoesNotContain(HoldPlacementEntry.FromJson(run.HoldsJson), e => e.HoldId == hold);
        }

        Assert.Equal([hold], await s.Service().PlaceEditedAsync(h.WallId, [hold]));
        var placed = (await s.LoadHoldsAsync())[hold];
        Assert.Equal("0", placed.FacetId);
        Assert.Equal(0.3 * 4000, placed.PlaneAMm!.Value, 1);
    }

    [Fact]
    public async Task AWallWidePlacement_RecordsThePanelGeometryItWasPlacedFor()
    {
        using var h = new WallTestHarness();
        var s = await HoldPlacementScenario.CreateAsync(h);
        var hold = await s.AddHoldAsync(0.25, 0.5);

        await s.Service().PlaceAsync(h.WallId);

        await using var db = h.CreateContext();
        var entry = HoldPlacementEntry.FromJson((await db.HoldPlacementRuns.SingleAsync()).HoldsJson).Single();
        Assert.Equal(HoldPlacementEntry.HashGeometry((await s.LoadHoldsAsync())[hold]), entry.GeometryHash);
    }

    /// <summary>A user's 2D move: the new centre, and the 3D position un-placed until the edit path places it again.</summary>
    private static async Task MoveAsync(WallTestHarness h, Guid holdId, double x)
    {
        await using var db = h.CreateContext();
        var hold = await db.Holds.SingleAsync(e => e.Id == holdId);
        hold.InvalidateGlyphPosition();
        hold.X = x;
        await db.SaveChangesAsync();
    }
}
