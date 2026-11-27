// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Capture.Scan;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Blocwerk.Core.Tests.Scan;

/// <summary>The "iPhone scan" wall setting: panels by default, owners and admins switch it, freely until a scan is applied.</summary>
public class WallUpdateModeTests
{
    [Fact]
    public async Task AWall_StartsOnPanels_AndAnAdminSwitchesItBothWays()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync();
        var modes = Modes(h);
        h.ActingUser = await h.AddMemberAsync("admin", WallRole.Admin);

        Assert.Equal(WallUpdateMode.Panels, (await modes.GetAsync(h.WallId)).Mode);
        Assert.Equal(WallUpdateMode.Scan, (await modes.SetAsync(h.WallId, WallUpdateMode.Scan)).Mode);
        Assert.Equal(WallUpdateMode.Panels, (await modes.SetAsync(h.WallId, WallUpdateMode.Panels)).Mode);
        await using var db = h.CreateContext();
        Assert.Equal(WallUpdateMode.Panels, (await db.Walls.IgnoreQueryFilters().SingleAsync(w => w.Id == h.WallId)).UpdateMode);
    }

    [Theory]
    [InlineData(WallRole.Moderator)]
    [InlineData(WallRole.Member)]
    public async Task ModeratorsAndMembers_CannotSwitchIt(WallRole role)
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync();
        var modes = Modes(h);
        h.ActingUser = await h.AddMemberAsync("someone", role);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => modes.SetAsync(h.WallId, WallUpdateMode.Scan));
    }

    [Fact]
    public async Task AKioskTablet_CannotSwitchIt()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync();
        var kiosk = Substitute.For<IKioskContext>();
        kiosk.IsKiosk.Returns(true);
        kiosk.KioskWallId.Returns(h.WallId);
        var modes = Modes(h, kiosk);

        await Assert.ThrowsAsync<KioskRestrictedException>(() => modes.SetAsync(h.WallId, WallUpdateMode.Scan));
    }

    [Fact]
    public async Task OnceAScanIsApplied_GoingBackToPanelsIsRefused()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync();
        var modes = Modes(h);
        await using (var db = h.CreateContext())
        {
            var wall = await db.Walls.IgnoreQueryFilters().SingleAsync(w => w.Id == h.WallId);
            wall.UpdateMode = WallUpdateMode.Scan;
            wall.ActiveScanCaptureId = Guid.NewGuid();
            await db.SaveChangesAsync();
        }

        var view = await modes.GetAsync(h.WallId);
        var refused = await Assert.ThrowsAsync<UserFacingException>(() => modes.SetAsync(h.WallId, WallUpdateMode.Panels));

        Assert.False(view.CanSwitchToPanels);
        Assert.Equal(WallUpdateModeService.LockedMessage, refused.Message);
    }

    private static WallUpdateModeService Modes(WallTestHarness h, IKioskContext? kiosk = null) =>
        new(h.DbContextFactory, h.CurrentUser, NullLogger<WallUpdateModeService>.Instance, kiosk);
}
