// <copyright file="WallGeometryPlacementControllerTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Reflection;
using System.Security.Claims;
using Blocwerk.Authentication.Authorization;
using Blocwerk.Authentication.Handlers;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Blocwerk.Web.Controllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Blocwerk.Core.Tests;

/// <summary>
/// POST /api/walls/{id}/geometry/place-holds (and its revert) against the real service: a personal key with
/// write access of a wall admin places and reverts, the same key without write access, a member's key and a wall
/// key of another wall are refused, and the route admits API keys only.
/// </summary>
public class WallGeometryPlacementControllerTests
{
    [Fact]
    public void Route_IsWallOrPersonalKeyOnly()
    {
        var authorize = typeof(WallGeometryPlacementController).GetCustomAttribute<AuthorizeAttribute>();
        var route = typeof(WallGeometryPlacementController).GetCustomAttribute<RouteAttribute>();

        Assert.NotNull(authorize);
        Assert.Equal(BlocwerkPolicies.AnyApiKey, authorize!.Policy);
        Assert.Equal(ApiKeyAuthenticationHandler.SchemeName, authorize.AuthenticationSchemes);
        Assert.Equal("api/walls/{wallId:guid}/geometry/place-holds", route!.Template);
        Assert.True(typeof(WallScopedApiController).IsAssignableFrom(typeof(WallGeometryPlacementController)));
    }

    [Fact]
    public async Task PersonalKeyWithWriteAccess_OfAnAdmin_PlacesAndReverts()
    {
        using var h = new WallTestHarness();
        var s = await HoldPlacementScenario.CreateAsync(h);
        var id = await s.AddHoldAsync(0.25, 0.5);
        var api = Bind(new WallGeometryPlacementController(s.Service(), NullLogger<WallGeometryPlacementController>.Instance), keyWallId: null);

        var placed = Body<HoldPlacementResult>(await api.Place(h.WallId, default));
        var status = Body<HoldPlacementStatus>(await api.Status(h.WallId, default));
        HoldTexturePlacementTests.AssertPlaced((await s.LoadHoldsAsync())[id], "0", 1000, 1500);
        var reverted = Body<HoldPlacementRevertResult>(await api.Revert(h.WallId, placed.RunId, default));

        Assert.Equal(1, placed.Placed);
        Assert.Equal(placed.RunId, status.LatestRun!.Id);
        Assert.Equal(HoldPlacementTrigger.Api, status.LatestRun.Trigger);
        Assert.Equal(1, reverted.Reverted);
        Assert.Null((await s.LoadHoldsAsync())[id].FacetId);
        Assert.IsType<ConflictObjectResult>(await api.Revert(h.WallId, placed.RunId, default));
    }

    [Fact]
    public async Task PersonalKeyWithoutWriteAccess_IsForbidden_EvenForAnAdmin()
    {
        using var h = new WallTestHarness();
        var s = await HoldPlacementScenario.CreateAsync(h);
        await s.AddHoldAsync(0.25, 0.5);
        var api = Bind(
            new WallGeometryPlacementController(s.Service(), NullLogger<WallGeometryPlacementController>.Instance),
            keyWallId: null,
            allowWrite: false);

        var result = await api.Place(h.WallId, default);

        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsType<ObjectResult>(result).StatusCode);
        await AssertNothingPlacedAsync(h);
    }

    [Fact]
    public async Task PersonalKeyOfAMember_IsForbidden_ByTheSameWallAdminCheckAsTheBrowser()
    {
        using var h = new WallTestHarness();
        var s = await HoldPlacementScenario.CreateAsync(h);
        await s.AddHoldAsync(0.25, 0.5);
        h.ActingUser = await h.AddMemberAsync("member@test", WallRole.Member);
        var api = Bind(new WallGeometryPlacementController(s.Service(), NullLogger<WallGeometryPlacementController>.Instance), keyWallId: null);

        var place = await api.Place(h.WallId, default);
        var status = await api.Status(h.WallId, default);

        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsType<ObjectResult>(place).StatusCode);
        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsType<ObjectResult>(status).StatusCode);
        await AssertNothingPlacedAsync(h);
    }

    [Fact]
    public async Task WallKey_WorksOnItsOwnWallOnly()
    {
        using var h = new WallTestHarness();
        var s = await HoldPlacementScenario.CreateAsync(h);
        await s.AddHoldAsync(0.25, 0.5);
        var foreign = Bind(new WallGeometryPlacementController(s.Service(), NullLogger<WallGeometryPlacementController>.Instance), Guid.NewGuid());
        var own = Bind(new WallGeometryPlacementController(s.Service(), NullLogger<WallGeometryPlacementController>.Instance), h.WallId);

        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsType<ObjectResult>(await foreign.Place(h.WallId, default)).StatusCode);
        await AssertNothingPlacedAsync(h);
        Assert.Equal(1, Body<HoldPlacementResult>(await own.Place(h.WallId, default)).Placed);
    }

    private static async Task AssertNothingPlacedAsync(WallTestHarness h)
    {
        await using var db = h.CreateContext();
        Assert.False(await db.Holds.AnyAsync(x => x.FacetId != null));
        Assert.False(await db.HoldPlacementRuns.AnyAsync());
    }

    private static T Body<T>(IActionResult result) => Assert.IsType<T>(Assert.IsType<OkObjectResult>(result).Value);

    /// <summary>
    /// A wall key bound to <paramref name="keyWallId"/>, or a personal key when it is null (with write access
    /// unless <paramref name="allowWrite"/> is false).
    /// </summary>
    private static WallGeometryPlacementController Bind(WallGeometryPlacementController controller, Guid? keyWallId, bool allowWrite = true)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, "1"),
            new(ApiKeyClaimTypes.Scope, (keyWallId is null ? ApiKeyScope.User : ApiKeyScope.Wall).ToString()),
            new(ApiKeyClaimTypes.ApiKeyId, Guid.NewGuid().ToString()),
        };
        if (keyWallId is { } wallId)
        {
            claims.Add(new Claim(ApiKeyClaimTypes.WallId, wallId.ToString()));
        }
        else if (allowWrite)
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
