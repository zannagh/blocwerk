// <copyright file="LookOverSpotTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Enums;
using Blocwerk.Core.LookOver;
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

    // A dense wall: the old hold's spot is 20 px beside another hold's edge on a 4000 px photo (0.5 % of its width). That
    // is bare wall, "it is here"; it used to be refused as anything within 1.2 % of the width.
    [Fact]
    public void ASpotRightBesideAHold_IsBareWall_AndOnlyInsideOrAHairOutsideItsOutlineMeansIt()
    {
        var size = (4000, 3000);
        var circle = PanelHoldAt(0.5, 0.5, 0.01); // 40 px
        Assert.Null(LookOverRules.HoldAtSpot([circle], 0.5 + (60 / 4000d), 0.5, size));
        Assert.Equal(circle, LookOverRules.HoldAtSpot([circle], 0.5 + (42 / 4000d), 0.5, size));
        Assert.Equal(circle, LookOverRules.HoldAtSpot([circle], 0.5 + (30 / 4000d), 0.5, size));

        // An outline counts, not the circle: 80 px wide and 20 px high, a spot 30 px above its centre is beside it.
        var outlined = PanelHoldAt(0.3, 0.3, 0.01) with
        {
            ShapePoints = [new() { Dx = -0.01, Dy = -20 / 6000d }, new() { Dx = 0.01, Dy = -20 / 6000d }, new() { Dx = 0.01, Dy = 20 / 6000d }, new() { Dx = -0.01, Dy = 20 / 6000d }],
        };
        Assert.Null(LookOverRules.HoldAtSpot([outlined], 0.3, 0.3 - (30 / 3000d), size));
        Assert.Equal(outlined, LookOverRules.HoldAtSpot([outlined], 0.3 + (35 / 4000d), 0.3, size));
    }

    // Positions are per axis, radii per long side: on a 2000 x 1000 photo a hold of radius 0.02 is 40 px round, so a spot
    // 38 px below its centre is on it, which the old distance (0.038 down against 0.02) called bare wall.
    [Fact]
    public void TheSpotIsMeasuredInPixels_NotInMixedWidthAndHeightShares()
    {
        var hold = PanelHoldAt(0.5, 0.5, 0.02);

        Assert.Equal(hold, LookOverRules.HoldAtSpot([hold], 0.5, 0.5 + (38 / 1000d), (2000, 1000)));
        Assert.Null(LookOverRules.HoldAtSpot([hold], 0.5, 0.5 + (50 / 1000d), (2000, 1000)));
    }

    private static PanelHold PanelHoldAt(double x, double y, double radius) => new(Guid.NewGuid(), x, y, radius, null, HoldCategory.Hand);
}
