// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Text.Json;
using Blocwerk.Core.Assets;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Blocwerk.Web.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>
/// <c>/api/v1/walls/{w}/settings</c> and <c>/members/{userId}/role</c> for the companion app: members read, admins
/// change through the web's own service calls, a field left out of the patch stays, nobody changes the owner's role,
/// and an outsider gets 404. Who may call at all is <see cref="DeviceApiRoutePostureTests"/>.
/// </summary>
public sealed class DeviceWallSettingsControllerTests
{
    [Fact]
    public async Task AMember_ReadsTheSettings_WithTheirRole_AnOutsiderGets404()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        h.ActingUser = await h.AddMemberAsync("member@test", WallRole.Moderator);

        var settings = Settings(await Controller(h).GetSettings(h.WallId, default));

        Assert.Equal(("Test Wall", "Moderator", h.Owner.Id, 0, false), (settings.Name, settings.MyRole, settings.OwnerId, settings.Angle, settings.UnderMaintenance));
        h.ActingUser = await OutsiderAsync(h);
        Assert.IsType<NotFoundResult>(await Controller(h).GetSettings(h.WallId, default));
    }

    [Fact]
    public async Task AnAdmin_PatchesWhatTheySend_AndTheRestStays()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        await Controller(h).PatchSettings(h.WallId, Json("""{ "description": "Board", "angle": 40 }"""), default);

        var after = Settings(await Controller(h).PatchSettings(h.WallId, Json("""
            { "name": "Attic", "underMaintenance": true, "allowAnonymousKiosk": true, "allowKioskKeyboardShortcuts": true, "description": null }
            """), default));

        Assert.Equal(("Attic", "Board", 40, true, true, true), (after.Name, after.Description, after.Angle, after.UnderMaintenance, after.AllowAnonymousKiosk, after.AllowKioskKeyboardShortcuts));
        var cleared = Settings(await Controller(h).PatchSettings(h.WallId, Json("""{ "description": "" }"""), default));
        Assert.Null(cleared.Description);
        Assert.Equal(("Attic", 40), (cleared.Name, cleared.Angle));
    }

    [Theory]
    [InlineData("""{ "angle": 91 }""")]
    [InlineData("""{ "angle": "steep" }""")]
    [InlineData("""{ "name": "  " }""")]
    [InlineData("""{ "underMaintenance": "yes" }""")]
    [InlineData("""{ "colour": "red" }""")]
    [InlineData("""[]""")]
    public async Task ABadPatch_Is400_AndChangesNothing(string body)
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);

        var refused = Assert.IsType<BadRequestObjectResult>(await Controller(h).PatchSettings(h.WallId, Json(body), default));
        Assert.IsType<ApiErrorResponse>(refused.Value);
        Assert.Equal("Test Wall", Settings(await Controller(h).GetSettings(h.WallId, default)).Name);
    }

    [Fact]
    public async Task AMemberOrModerator_Gets403_AnOutsider404()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        var member = await h.AddMemberAsync("member@test", WallRole.Member);
        h.ActingUser = await h.AddMemberAsync("moderator@test", WallRole.Moderator);

        Refused(await Controller(h).PatchSettings(h.WallId, Json("""{ "name": "Mine" }"""), default), 403);
        Refused(await Controller(h).SetRole(h.WallId, member.Id, new SetMemberRoleRequest { Role = "Admin" }, default), 403);
        h.ActingUser = await OutsiderAsync(h);
        Assert.IsType<NotFoundResult>(await Controller(h).PatchSettings(h.WallId, Json("""{ "name": "Mine" }"""), default));
        Assert.IsType<NotFoundResult>(await Controller(h).SetRole(h.WallId, member.Id, new SetMemberRoleRequest { Role = "Admin" }, default));
    }

    [Fact]
    public async Task AnAdmin_SetsAMembersRole_NeverTheOwners()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        var member = await h.AddMemberAsync("member@test", WallRole.Member);
        h.ActingUser = await h.AddMemberAsync("admin@test", WallRole.Admin);

        var ok = Assert.IsType<OkObjectResult>(await Controller(h).SetRole(h.WallId, member.Id, new SetMemberRoleRequest { Role = "moderator" }, default));
        Assert.Equal(new SetMemberRoleResponse(member.Id, "Moderator"), ok.Value);
        Refused(await Controller(h).SetRole(h.WallId, h.Owner.Id, new SetMemberRoleRequest { Role = "Member" }, default), 403);
        Refused(await Controller(h).SetRole(h.WallId, Guid.NewGuid(), new SetMemberRoleRequest { Role = "Member" }, default), 404);
        Assert.IsType<BadRequestObjectResult>(await Controller(h).SetRole(h.WallId, member.Id, new SetMemberRoleRequest { Role = "Boss" }, default));
        Assert.IsType<BadRequestObjectResult>(await Controller(h).SetRole(h.WallId, member.Id, new SetMemberRoleRequest { Role = "1" }, default));

        await using var db = h.CreateContext();
        Assert.Equal(WallRole.Moderator, (await db.WallMembers.SingleAsync(m => m.UserId == member.Id)).Role);
    }

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static DeviceWallSettingsResponse Settings(IActionResult result) =>
        Assert.IsType<DeviceWallSettingsResponse>(Assert.IsType<OkObjectResult>(result).Value);

    private static void Refused(IActionResult result, int status)
    {
        var refused = Assert.IsType<ObjectResult>(result);
        Assert.Equal(status, refused.StatusCode);
        Assert.IsType<ApiErrorResponse>(refused.Value);
    }

    private static async Task<User> OutsiderAsync(WallTestHarness h)
    {
        await using var db = h.CreateContext();
        var user = new User { Identifier = "outsider@test", DisplayName = "Outsider" };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    private static DeviceWallSettingsController Controller(WallTestHarness h)
    {
        var panels = Substitute.For<IWallPanelService>();
        panels.GetPanelsAsync(Arg.Any<Guid>()).Returns(Task.FromResult<IReadOnlyList<WallPanelInfo>>([]));
        var reader = new DeviceWallSettingsReader(h.DbContextFactory, new AssetHashReader(h.DbContextFactory, h.WallService), panels);
        return new DeviceWallSettingsController(h.DbContextFactory, h.CurrentUser, h.WallService, reader)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = DeviceKeyTestPrincipals.Device() },
            },
        };
    }
}
