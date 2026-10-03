// <copyright file="WallVolumesControllerTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Reflection;
using System.Security.Claims;
using Blocwerk.Authentication.Authorization;
using Blocwerk.Authentication.Handlers;
using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Enums;
using Blocwerk.Web.Controllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The volume corrections over the machine API against the real service: a wall admin's personal key with write access
/// removes, restores and sets flat sides; the same key without write access, a member's key and a kiosk are refused.
/// </summary>
public class WallVolumesControllerTests
{
    [Fact]
    public void Routes_AreApiKeyOnly_AndWallScoped()
    {
        var authorize = typeof(WallVolumesController).GetCustomAttribute<AuthorizeAttribute>()!;

        Assert.Equal(BlocwerkPolicies.AnyApiKey, authorize.Policy);
        Assert.Equal(ApiKeyAuthenticationHandler.SchemeName, authorize.AuthenticationSchemes);
        Assert.True(typeof(WallScopedApiController).IsAssignableFrom(typeof(WallVolumesController)));
        Assert.Equal("{volumeId:guid}/remove", Template(nameof(WallVolumesController.Remove)));
        Assert.Equal("{volumeId:guid}/restore", Template(nameof(WallVolumesController.Restore)));
        Assert.Equal("{volumeId:guid}/flat-sides", Template(nameof(WallVolumesController.SetFlatSides)));
        Assert.Equal("flat-sides", Template(nameof(WallVolumesController.SetWallFlatSides)));
    }

    [Fact]
    public async Task AdminKeyWithWriteAccess_RemovesRestoresAndSetsFlatSides()
    {
        using var h = new WallTestHarness();
        using var s = await VolumeEditScenario.CreateAsync(h);
        await s.Service().DetectAsync(h.WallId);
        var id = (await s.VolumeAsync()).Id;
        var api = Bind(new WallVolumesController(s.Service(), NullLogger<WallVolumesController>.Instance));

        var flat = Body<WallVolumeShapeResult>(await api.SetFlatSides(h.WallId, id, new FlatSidesRequest(true), default));
        var removed = Body<WallVolumeRunResult>(await api.Remove(h.WallId, id, default));
        var restored = Body<WallVolumeRunResult>(await api.Restore(h.WallId, id, default));
        var wall = Body<WallVolumeShapeResult>(await api.SetWallFlatSides(h.WallId, new FlatSidesRequest(false, ApplyToAll: true), default));

        Assert.Equal(1, flat.FlatSided);
        Assert.Equal(0, removed.Volumes);
        Assert.Equal(1, restored.Volumes);
        Assert.Equal(0, wall.FlatSided);
        Assert.False(Body<FlatSidesRequest>(await api.GetWallFlatSides(h.WallId, default)).Value);
    }

    [Fact]
    public async Task KeyWithoutWriteAccess_Member_AndKiosk_AreRefused()
    {
        using var h = new WallTestHarness();
        using var s = await VolumeEditScenario.CreateAsync(h);
        await s.Service().DetectAsync(h.WallId);
        var id = (await s.VolumeAsync()).Id;
        var readOnly = Bind(new WallVolumesController(s.Service(), NullLogger<WallVolumesController>.Instance), allowWrite: false);
        var kiosk = Bind(new WallVolumesController(
            s.Service(AnonymousSettingFixture.KioskContextFor(true, h.WallId, Guid.NewGuid())), NullLogger<WallVolumesController>.Instance));

        AssertForbidden(await readOnly.SetFlatSides(h.WallId, id, new FlatSidesRequest(true), default));
        AssertForbidden(await readOnly.Remove(h.WallId, id, default));
        AssertForbidden(await kiosk.SetFlatSides(h.WallId, id, new FlatSidesRequest(true), default));
        AssertForbidden(await kiosk.Remove(h.WallId, id, default));
        h.ActingUser = await h.AddMemberAsync("member@test", WallRole.Member);
        var member = Bind(new WallVolumesController(s.Service(), NullLogger<WallVolumesController>.Instance));
        AssertForbidden(await member.SetFlatSides(h.WallId, id, new FlatSidesRequest(true), default));
        AssertForbidden(await member.Remove(h.WallId, id, default));
        AssertForbidden(await member.SetWallFlatSides(h.WallId, new FlatSidesRequest(true, true), default));

        await using var db = h.CreateContext();
        var volume = await db.WallVolumes.SingleAsync();
        Assert.False(volume.HasFlatSides);
        Assert.False(volume.IsRemoved);
    }

    private static string? Template(string action) =>
        typeof(WallVolumesController).GetMethod(action)!.GetCustomAttributes().OfType<Microsoft.AspNetCore.Mvc.Routing.HttpMethodAttribute>().Single().Template;

    private static void AssertForbidden(IActionResult result) =>
        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsType<ObjectResult>(result).StatusCode);

    private static T Body<T>(IActionResult result) => Assert.IsType<T>(Assert.IsType<OkObjectResult>(result).Value);

    /// <summary>A personal key (with write access unless <paramref name="allowWrite"/> is false).</summary>
    private static WallVolumesController Bind(WallVolumesController controller, bool allowWrite = true)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, "1"),
            new(ApiKeyClaimTypes.Scope, ApiKeyScope.User.ToString()),
            new(ApiKeyClaimTypes.ApiKeyId, Guid.NewGuid().ToString()),
        };
        if (allowWrite)
        {
            claims.Add(new Claim(ApiKeyClaimTypes.AllowWrite, "true"));
        }

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, ApiKeyAuthenticationHandler.SchemeName)) },
        };
        return controller;
    }
}
