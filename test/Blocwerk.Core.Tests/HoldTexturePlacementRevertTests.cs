using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Detection.Enrichment;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>Reverting a texture-registration placement run, and who may run it.</summary>
public class HoldTexturePlacementRevertTests
{
    [Fact]
    public async Task Revert_RestoresTheSnapshot_AndLeavesHoldsMovedSinceAlone()
    {
        using var h = new WallTestHarness();
        var s = await HoldPlacementScenario.CreateAsync(h);
        var sized = await s.AddHoldAsync(0.25, 0.5, configure: x =>
        {
            x.WidthMm = 33;
            x.FootprintMm = "{\"stale\":true}";
            x.FingerprintJson = new HoldFingerprint { L = 1, A = 2, B = 3 }.ToJson();
        });
        var moved = await s.AddHoldAsync(0.75, 0.5);
        var before = await s.LoadHoldsAsync();
        var service = s.Service();
        var run = await service.PlaceAsync(h.WallId);

        // The refinement writes a footprint; the user then drags the other hold.
        await using (var db = h.CreateContext())
        {
            (await db.Holds.SingleAsync(x => x.Id == sized)).FootprintMm = "{\"fresh\":true}";
            var drag = await db.Holds.SingleAsync(x => x.Id == moved);
            drag.InvalidateGlyphForEdit(moved: true, reshaped: false);
            drag.X = 0.7;
            await db.SaveChangesAsync();
        }

        var placedFingerprint = (await s.LoadHoldsAsync())[sized].FingerprintJson;
        var revert = await service.RevertAsync(h.WallId, run.RunId);

        Assert.Equal((1, 0), (revert.Reverted, revert.Missing));
        Assert.Equal([moved], revert.SkippedEdited);
        var after = await s.LoadHoldsAsync();
        Assert.Equivalent(before[sized], after[sized]);
        Assert.NotEqual(before[sized].FingerprintJson, placedFingerprint);
        Assert.Equal(0.7, after[moved].X);
        Assert.Null(after[moved].FacetId);

        var status = await service.GetStatusAsync(h.WallId);
        Assert.NotNull(status.LatestRun!.RevertedAt);
        Assert.Single(status.LatestRun.Panels); // c1 has no holds, so it is not looked at
        await Assert.ThrowsAsync<UserFacingException>(() => service.RevertAsync(h.WallId, run.RunId));
    }

    [Fact]
    public async Task ReRun_RevertsToThePreviousRunsPlacement()
    {
        using var h = new WallTestHarness();
        var s = await HoldPlacementScenario.CreateAsync(h);
        var id = await s.AddHoldAsync(0.25, 0.5);
        var service = s.Service();
        await service.PlaceAsync(h.WallId);
        var first = (await s.LoadHoldsAsync())[id];

        // The second run sees a slightly different registration.
        s.Matcher.Views[0] = s.Matcher.Views[0] with { PhotoToTexture = Geometry.PlaneHomography.FromCoefficients([1, 0, 109.5, 0, 1, 99.5, 0, 0, 1]) };
        var second = await service.PlaceAsync(h.WallId);
        Assert.Equal(1010, (await s.LoadHoldsAsync())[id].PlaneAMm!.Value, 3);

        await service.RevertAsync(h.WallId, second.RunId);
        Assert.Equivalent(first, (await s.LoadHoldsAsync())[id]);
    }

    [Theory]
    [InlineData(WallRole.Member)]
    [InlineData(WallRole.Moderator)]
    public async Task NonAdmin_IsRefused_AndNothingChanges(WallRole role)
    {
        using var h = new WallTestHarness();
        var s = await HoldPlacementScenario.CreateAsync(h);
        await s.AddHoldAsync(0.25, 0.5);
        var run = await s.Service().PlaceAsync(h.WallId);
        h.ActingUser = await h.AddMemberAsync("climber@test", role);
        var service = s.Service();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.GetStatusAsync(h.WallId));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.PlaceAsync(h.WallId));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.RevertAsync(h.WallId, run.RunId));

        await using var db = h.CreateContext();
        Assert.Equal(1, await db.HoldPlacementRuns.CountAsync());
    }

    [Fact]
    public async Task Kiosk_IsRefused_EvenForTheOwnersOwnWall()
    {
        using var h = new WallTestHarness();
        var s = await HoldPlacementScenario.CreateAsync(h);
        await s.AddHoldAsync(0.25, 0.5);
        var kiosk = Substitute.For<IKioskContext>();
        kiosk.IsKiosk.Returns(true);
        kiosk.KioskWallId.Returns(h.WallId);

        await Assert.ThrowsAsync<KioskRestrictedException>(() => s.Service(kiosk).PlaceAsync(h.WallId));

        await using var db = h.CreateContext();
        Assert.False(await db.Holds.AnyAsync(x => x.MetricSource == HoldMetric.TextureRegistration));
    }

    [Fact]
    public async Task WallWithoutTextures_IsRefusedWithAReason()
    {
        using var h = new WallTestHarness();
        var s = await HoldPlacementScenario.CreateAsync(h);
        await using (var db = h.CreateContext())
        {
            db.WallGeometryTextures.RemoveRange(db.WallGeometryTextures);
            await db.SaveChangesAsync();
        }

        var status = await s.Service().GetStatusAsync(h.WallId);
        var ex = await Assert.ThrowsAsync<UserFacingException>(() => s.Service().PlaceAsync(h.WallId));

        Assert.False(status.HasTextures);
        Assert.Contains("textures", ex.Message);
    }
}
