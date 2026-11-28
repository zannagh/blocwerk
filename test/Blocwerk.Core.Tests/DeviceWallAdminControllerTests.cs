// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Capture.Scan;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Tests.Scan;
using Blocwerk.Web.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Tests;

/// <summary>
/// <c>/api/v1/walls/{w}/scan-mode</c>, <c>/scans/{id}</c> and <c>DELETE /api/v1/walls/{w}</c> for the companion app:
/// admins read and set the mode (locked once a scan is applied), site admins remove a scan, only the owner deletes the
/// wall and only with its name typed again; an outsider gets 404, a member without the role 403.
/// </summary>
public sealed class DeviceWallAdminControllerTests
{
    [Fact]
    public async Task AnAdmin_ReadsAndSetsTheMode_UntilAScanIsApplied()
    {
        using var h = new WallTestHarness();
        using var s = new ScanScenario(h);
        await h.SeedWallAsync(holdCount: 0);
        h.ActingUser = await h.AddMemberAsync("admin@test", WallRole.Admin);

        var view = View(await Controller(s).GetScanMode(h.WallId, default));
        Assert.Equal(("Panels", true, 0, false), (view.Mode, view.CanSwitchToPanels, view.Scans.Count, view.MayDeleteScans));
        Assert.Equal("Scan", View(await Controller(s).SetScanMode(h.WallId, new SetScanModeRequest { Mode = "scan" }, default)).Mode);
        Refused(await Controller(s).SetScanMode(h.WallId, new SetScanModeRequest { Mode = "Lidar" }, default), 400);
        Refused(await Controller(s).SetScanMode(h.WallId, new SetScanModeRequest { Mode = "0" }, default), 400);

        await using (var db = h.CreateContext())
        {
            await db.Walls.IgnoreQueryFilters().Where(w => w.Id == h.WallId)
                .ExecuteUpdateAsync(u => u.SetProperty(w => w.ActiveScanCaptureId, Guid.NewGuid()));
        }

        Refused(await Controller(s).SetScanMode(h.WallId, new SetScanModeRequest { Mode = "Panels" }, default), 409, WallUpdateModeService.LockedMessage);
        Assert.False(View(await Controller(s).GetScanMode(h.WallId, default)).CanSwitchToPanels);
    }

    [Fact]
    public async Task AModerator_Gets403_OnTheMode_AnOutsider404()
    {
        using var h = new WallTestHarness();
        using var s = new ScanScenario(h);
        await h.SeedWallAsync(holdCount: 0);
        h.ActingUser = await h.AddMemberAsync("moderator@test", WallRole.Moderator);

        Refused(await Controller(s).GetScanMode(h.WallId, default), 403);
        Refused(await Controller(s).SetScanMode(h.WallId, new SetScanModeRequest { Mode = "Scan" }, default), 403);
        h.ActingUser = await OutsiderAsync(h);
        Assert.IsType<NotFoundResult>(await Controller(s).GetScanMode(h.WallId, default));
        Assert.IsType<NotFoundResult>(await Controller(s).DeleteScan(h.WallId, Guid.NewGuid(), default));
    }

    [Fact]
    public async Task OnlyASiteAdmin_RemovesAScan()
    {
        using var h = new WallTestHarness();
        using var s = new ScanScenario(h);
        await h.SeedWallAsync(holdCount: 0);

        Refused(await Controller(s).DeleteScan(h.WallId, Guid.NewGuid(), default), 403); // the owner, not a site admin
        await using (var db = h.CreateContext())
        {
            await db.Users.Where(u => u.Id == h.Owner.Id).ExecuteUpdateAsync(u => u.SetProperty(x => x.Role, IdentityRole.Admin));
        }

        Refused(await Controller(s).DeleteScan(h.WallId, Guid.NewGuid(), default), 404);
        Assert.True(View(await Controller(s).GetScanMode(h.WallId, default)).MayDeleteScans);
    }

    [Fact]
    public async Task OnlyTheOwner_DeletesTheWall_AndOnlyWithItsNameTyped()
    {
        using var h = new WallTestHarness();
        using var s = new ScanScenario(h);
        await h.SeedWallAsync(holdCount: 0);
        var owner = h.Owner;
        h.ActingUser = await h.AddMemberAsync("admin@test", WallRole.Admin);

        Refused(await Controller(s).DeleteWall(h.WallId, new DeleteWallRequest { ConfirmName = "Test Wall" }, h.WallService, default), 403);
        h.ActingUser = owner;
        Refused(await Controller(s).DeleteWall(h.WallId, new DeleteWallRequest { ConfirmName = "test wall" }, h.WallService, default), 400);
        Refused(await Controller(s).DeleteWall(h.WallId, new DeleteWallRequest(), h.WallService, default), 400);
        Assert.IsType<NotFoundResult>(await Controller(s).DeleteWall(Guid.NewGuid(), new DeleteWallRequest { ConfirmName = "Test Wall" }, h.WallService, default));
        await using (var db = h.CreateContext())
        {
            Assert.True(await db.Walls.IgnoreQueryFilters().AnyAsync(w => w.Id == h.WallId));
        }

        Assert.IsType<NoContentResult>(await Controller(s).DeleteWall(h.WallId, new DeleteWallRequest { ConfirmName = "Test Wall" }, h.WallService, default));
        await using var check = h.CreateContext();
        Assert.False(await check.Walls.IgnoreQueryFilters().AnyAsync(w => w.Id == h.WallId));
    }

    private static DeviceScanModeResponse View(IActionResult result) =>
        Assert.IsType<DeviceScanModeResponse>(Assert.IsType<OkObjectResult>(result).Value);

    private static void Refused(IActionResult result, int status, string? message = null)
    {
        var refused = Assert.IsType<ObjectResult>(result);
        Assert.Equal(status, refused.StatusCode);
        var error = Assert.IsType<ApiErrorResponse>(refused.Value);
        if (message is not null)
        {
            Assert.Equal(message, error.Message);
        }
    }

    private static async Task<User> OutsiderAsync(WallTestHarness h)
    {
        await using var db = h.CreateContext();
        var user = new User { Identifier = "outsider@test", DisplayName = "Outsider" };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    private static DeviceWallAdminController Controller(ScanScenario s) =>
        new(s.Harness.DbContextFactory, s.Harness.CurrentUser, s.Modes, s.Service)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = DeviceKeyTestPrincipals.Device() },
            },
        };
}
