// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using NSubstitute;

namespace Blocwerk.Core.Tests.Scan;

/// <summary>The "iPhone scan" wall setting: panels by default, owners and admins switch it, freely until a scan is applied.</summary>
public class WallUpdateModeTests
{
    [Fact]
    public async Task AWall_StartsOnPanels_AndAnAdminSwitchesItBothWays()
    {
        using var h = new WallTestHarness();
        using var s = new ScanScenario(h);
        await h.SeedWallAsync();
        h.ActingUser = await h.AddMemberAsync("admin", WallRole.Admin);

        Assert.Equal(WallUpdateMode.Panels, (await s.Modes.GetAsync(h.WallId)).Mode);
        Assert.Equal(WallUpdateMode.Scan, (await s.Modes.SetAsync(h.WallId, WallUpdateMode.Scan)).Mode);
        Assert.Equal(WallUpdateMode.Panels, (await s.Modes.SetAsync(h.WallId, WallUpdateMode.Panels)).Mode);
        await using var db = h.CreateContext();
        Assert.Equal(WallUpdateMode.Panels, (await db.Walls.IgnoreQueryFilters().SingleAsync(w => w.Id == h.WallId)).UpdateMode);
    }

    [Theory]
    [InlineData(WallRole.Moderator)]
    [InlineData(WallRole.Member)]
    public async Task ModeratorsAndMembers_CannotSwitchIt(WallRole role)
    {
        using var h = new WallTestHarness();
        using var s = new ScanScenario(h);
        await h.SeedWallAsync();
        h.ActingUser = await h.AddMemberAsync("someone", role);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => s.Modes.SetAsync(h.WallId, WallUpdateMode.Scan));
    }

    [Fact]
    public async Task AKioskTablet_CannotSwitchIt()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync();
        var kiosk = Substitute.For<IKioskContext>();
        kiosk.IsKiosk.Returns(true);
        kiosk.KioskWallId.Returns(h.WallId);
        using var s = new ScanScenario(h, kiosk);

        await Assert.ThrowsAsync<KioskRestrictedException>(() => s.Modes.SetAsync(h.WallId, WallUpdateMode.Scan));
    }

    [Fact]
    public async Task OnceAScanIsApplied_GoingBackToPanelsIsRefused()
    {
        using var h = new WallTestHarness();
        using var s = new ScanScenario(h);
        await s.SeedScanWallAsync();
        await using (var db = h.CreateContext())
        {
            var wall = await db.Walls.IgnoreQueryFilters().SingleAsync(w => w.Id == h.WallId);
            wall.ActiveScanCaptureId = Guid.NewGuid();
            await db.SaveChangesAsync();
        }

        var view = await s.Modes.GetAsync(h.WallId);
        var refused = await Assert.ThrowsAsync<UserFacingException>(() => s.Modes.SetAsync(h.WallId, WallUpdateMode.Panels));

        Assert.False(view.CanSwitchToPanels);
        Assert.Equal(Capture.Scan.WallUpdateModeService.LockedMessage, refused.Message);
    }
}
