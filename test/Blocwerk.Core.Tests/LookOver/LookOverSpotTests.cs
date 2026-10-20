// <copyright file="LookOverSpotTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using static Blocwerk.Core.Tests.HoldLookFixture;
using static Blocwerk.Core.Tests.LookOver.LookOverFixture;

namespace Blocwerk.Core.Tests.LookOver;

/// <summary>
/// "It is here" with the page's checks, which the API now reaches too: only on the hold's own photo, only on bare wall (a
/// spot on a hold the update found is that hold), and with an optional new size.
/// </summary>
public class LookOverSpotTests
{
    [Fact]
    public async Task ItIsHere_WithARadius_MovesAndResizesTheHold()
    {
        using var h = new WallTestHarness();
        var k = await KeptHoldFixture.ApplyAsync(h);
        var service = Service(h);
        var view = (await service.ListAsync(k.Wall.WallId)).Single(v => v.HoldId == k.UsedId);

        await service.MoveAsync(k.Wall.WallId, view.Id, 0.7, 0.35, radius: 0.02, panelId: view.PanelId);

        var hold = (await HoldAsync(h, k.UsedId))!;
        Assert.Equal((0.7, 0.35, 0.02, true), (hold.X, hold.Y, hold.Radius, hold.NeedsReview));
        Assert.Equal((LookOverAnswer?)LookOverAnswer.Moved, (await FindingsAsync(h, k.Wall.WallId)).Single(f => f.Id == view.Id).Answer);
    }

    [Fact]
    public async Task ItIsHere_OnAHold_OrOnAnotherPhoto_IsRefused_AndTheHoldStays()
    {
        using var h = new WallTestHarness();
        var k = await KeptHoldFixture.ApplyAsync(h);
        var service = Service(h);
        var view = (await service.ListAsync(k.Wall.WallId)).Single(v => v.HoldId == k.UsedId);
        var (x, y) = Anchors[0];

        var onHold = await Assert.ThrowsAsync<UserFacingException>(() => service.MoveAsync(k.Wall.WallId, view.Id, x + 0.01, y));
        var elsewhere = await Assert.ThrowsAsync<UserFacingException>(() => service.MoveAsync(k.Wall.WallId, view.Id, 0.7, 0.35, panelId: Guid.NewGuid()));

        Assert.Contains(k.FoundId.ToString(), onHold.Message);
        Assert.Contains("another photo", elsewhere.Message);
        var hold = (await HoldAsync(h, k.UsedId))!;
        Assert.Equal((TargetX, TargetY), (hold.X, hold.Y));
        Assert.True((await service.ListAsync(k.Wall.WallId)).Single(v => v.Id == view.Id).Missing);
    }
}
