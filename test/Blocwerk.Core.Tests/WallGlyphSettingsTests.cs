using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The marker declaration on a wall is a wall-admin action: owner/admin only, and never from a kiosk
/// tablet — even one registered to that very wall.
/// </summary>
public class WallGlyphSettingsTests
{
    [Fact]
    public async Task Owner_CanDeclareMarkers_AndReadThemBack()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        var glyphs = Service(h);

        var saved = await glyphs.SetGlyphSettingsAsync(h.WallId, enabled: true, markerSizeMm: 150);

        Assert.Equal(new WallGlyphSettings(true, 150), saved);
        Assert.Equal(new WallGlyphSettings(true, 150), await glyphs.GetGlyphSettingsAsync(h.WallId));
    }

    [Fact]
    public async Task NullSize_KeepsTheStoredSize_WhenSwitchingOff()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        var glyphs = Service(h);
        await glyphs.SetGlyphSettingsAsync(h.WallId, enabled: true, markerSizeMm: 125);

        var saved = await glyphs.SetGlyphSettingsAsync(h.WallId, enabled: false, markerSizeMm: null);

        Assert.Equal(new WallGlyphSettings(false, 125), saved);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(5000)]
    [InlineData(double.NaN)]
    public async Task ImplausibleSize_IsRefused(double size)
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => Service(h).SetGlyphSettingsAsync(h.WallId, enabled: true, markerSizeMm: size));
    }

    [Theory]
    [InlineData(WallRole.Member)]
    [InlineData(WallRole.Moderator)]
    public async Task NonAdmin_IsRefused_AndNothingChanges(WallRole role)
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        h.ActingUser = await h.AddMemberAsync("climber@test", role);
        var glyphs = Service(h);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => glyphs.SetGlyphSettingsAsync(h.WallId, enabled: true, markerSizeMm: 125));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => glyphs.ImportGeometryAsync(h.WallId, GlyphGeometryJson.Build(), notes: null));

        await using var db = h.CreateContext();
        var wall = await db.Walls.SingleAsync(w => w.Id == h.WallId);
        Assert.False(wall.GlyphsEnabled);
        Assert.False(await db.WallGeometryModels.AnyAsync());
    }

    [Fact]
    public async Task Kiosk_IsRefused_EvenActingAsTheOwnerOfItsOwnWall()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        var kiosk = Substitute.For<IKioskContext>();
        kiosk.IsKiosk.Returns(true);
        kiosk.KioskWallId.Returns(h.WallId);
        var glyphs = new WallGlyphService(h.DbContextFactory, h.CurrentUser, NullLogger<WallGlyphService>.Instance, kiosk);

        await Assert.ThrowsAsync<KioskRestrictedException>(
            () => glyphs.SetGlyphSettingsAsync(h.WallId, enabled: true, markerSizeMm: 125));
        await Assert.ThrowsAsync<KioskRestrictedException>(
            () => glyphs.ImportGeometryAsync(h.WallId, GlyphGeometryJson.Build(), notes: null));

        await using var db = h.CreateContext();
        Assert.False((await db.Walls.SingleAsync(w => w.Id == h.WallId)).GlyphsEnabled);
    }

    [Fact]
    public async Task Admin_WhoIsNotTheOwner_CanDeclareMarkers()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        h.ActingUser = await h.AddMemberAsync("coowner@test", WallRole.Admin);

        var saved = await Service(h).SetGlyphSettingsAsync(h.WallId, enabled: true, markerSizeMm: 100);

        Assert.True(saved.Enabled);
    }

    internal static WallGlyphService Service(WallTestHarness h) =>
        new(h.DbContextFactory, h.CurrentUser, NullLogger<WallGlyphService>.Instance);
}
