// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Security.Claims;
using System.Text.Json;
using Blocwerk.Authentication.Services;
using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Configuration;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Web.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>
/// <c>GET</c>/<c>PATCH /api/v1/me/profile</c>: the web profile's identity card and server-side App preferences, written
/// through the real <see cref="CurrentUserService"/> the page uses, so what the test reads back is what the database holds.
/// </summary>
public sealed class MeProfileControllerTests
{
    [Fact]
    public async Task Get_ShowsTheIdentityAndThePreferences()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);

        var profile = Body(await Controller(h, h.Owner).Get());

        Assert.Equal(h.Owner.Id, profile.Id);
        Assert.Equal("font", profile.GradeScale);
        Assert.Null(profile.HomeWallId);
        Assert.All(profile.Notifications.Values, Assert.True);
        Assert.Contains("BoulderAdded", profile.Notifications.Keys);
        Assert.DoesNotContain("None", profile.Notifications.Keys);
    }

    [Fact]
    public async Task Patch_ChangesOnlyTheFieldsSent_AndNullClears()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        var controller = Controller(h, h.Owner);

        var changed = Body(await controller.Patch(Json(
            $$"""{ "displayName": "  Crimp Queen ", "homeWallId": "{{h.WallId}}", "gradeScale": "v", "notifications": { "BoulderAdded": false } }""")));

        Assert.Equal("Crimp Queen", changed.Name);
        Assert.Equal(h.WallId, changed.HomeWallId);
        Assert.Equal("v", changed.GradeScale);
        Assert.False(changed.Notifications["BoulderAdded"]);
        Assert.True(changed.Notifications["MemberJoined"]);

        var cleared = Body(await controller.Patch(Json("""{ "displayName": null, "homeWallId": null }""")));
        Assert.Null(cleared.CustomDisplayName);
        Assert.Null(cleared.HomeWallId);
        Assert.Equal("v", cleared.GradeScale);

        await using var db = h.CreateContext();
        var stored = await db.Users.SingleAsync(u => u.Id == h.Owner.Id);
        Assert.False(stored.PreferFontGrades);
        Assert.Equal(NotificationType.BoulderAdded, stored.DisabledNotifications);
    }

    [Fact]
    public async Task Patch_RefusesAHomeWallTheCallerIsNoMemberOf_AndChangesNothing()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        var outsider = await AddUserAsync(h, "outsider@test");

        var result = await Controller(h, outsider).Patch(Json(
            $$"""{ "displayName": "Sneaky", "homeWallId": "{{h.WallId}}" }"""));

        Assert.IsType<BadRequestObjectResult>(result);
        await using var db = h.CreateContext();
        var stored = await db.Users.SingleAsync(u => u.Id == outsider.Id);
        Assert.Null(stored.CustomDisplayName);
        Assert.Null(stored.HomeWallId);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("""{ "email": "x@y" }""")]
    [InlineData("""{ "gradeScale": "yds" }""")]
    [InlineData("""{ "gradeScale": null }""")]
    [InlineData("""{ "notifications": { "Everything": false } }""")]
    [InlineData("""{ "notifications": { "None": false } }""")]
    [InlineData("""{ "notifications": { "BoulderAdded": "no" } }""")]
    [InlineData("""{ "homeWallId": "not-a-guid" }""")]
    [InlineData("""{ "displayName": 7 }""")]
    public async Task Patch_RefusesABadBody(string body)
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);

        Assert.IsType<BadRequestObjectResult>(await Controller(h, h.Owner).Patch(Json(body)));
    }

    [Fact]
    public async Task Patch_RefusesADisplayNameLongerThanTheWebField()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        var body = JsonSerializer.Serialize(new { displayName = new string('a', 257) });

        Assert.IsType<BadRequestObjectResult>(await Controller(h, h.Owner).Patch(Json(body)));
    }

    internal static CurrentUserService RealCurrentUser(WallTestHarness h, User user)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, "subject-" + user.Id),
            new(ClaimTypes.Name, user.DisplayName),
            new("uid", user.Id.ToString()),
        };
        var accessor = Substitute.For<IHttpContextAccessor>();
        accessor.HttpContext.Returns(new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Cookies")) });
        return new CurrentUserService(
            new BlocwerkSettings(),
            h.DbContextFactory,
            Substitute.For<IPasswordLoginService>(),
            Substitute.For<ITotpService>(),
            authenticationStateProvider: null,
            accessor: accessor);
    }

    internal static async Task<User> AddUserAsync(WallTestHarness h, string identifier)
    {
        await using var db = h.CreateContext();
        var user = new User { Identifier = identifier, DisplayName = identifier };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    private static MeProfileResponse Body(IActionResult result) =>
        Assert.IsType<MeProfileResponse>(Assert.IsType<OkObjectResult>(result).Value);

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    private static MeProfileController Controller(WallTestHarness h, User user) =>
        new(RealCurrentUser(h, user))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = DeviceKeyTestPrincipals.Device() },
            },
        };
}
