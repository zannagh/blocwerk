// <copyright file="WallUpdateShapesControllerTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Reflection;
using System.Security.Claims;
using Blocwerk.Authentication.Authorization;
using Blocwerk.Authentication.Handlers;
using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Web.Controllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The shape step over the machine API, against the real service: an admin's wall key drives the whole
/// round trip, a member's key and a kiosk are refused, and the route is pinned to wall-scoped keys.
/// </summary>
public class WallUpdateShapesControllerTests
{
    [Fact]
    public void Route_IsWallApiKeyOnly_SoKioskKeysNeverReachIt()
    {
        var authorize = typeof(WallUpdateShapesController).GetCustomAttribute<AuthorizeAttribute>();

        Assert.NotNull(authorize);
        Assert.Equal(BlocwerkPolicies.WallApiKey, authorize!.Policy);
        Assert.Equal(ApiKeyAuthenticationHandler.SchemeName, authorize.AuthenticationSchemes);
        Assert.True(typeof(WallScopedApiController).IsAssignableFrom(typeof(WallUpdateShapesController)));
    }

    [Fact]
    public async Task AdminKey_RoundTrip_StartPollListDecideAndPromote()
    {
        using var h = new WallTestHarness();
        var f = new ShapeStepFixture(h);
        var ids = await f.StageAsync(ShapeStepFixture.AutoHold(0.9), ShapeStepFixture.AutoHold(0.2), ShapeStepFixture.AutoHold(0.6));
        var api = Bind(new WallUpdateShapesController(f.Service, NullLogger<WallUpdateShapesController>.Instance), h.WallId);

        var started = await api.Start(h.WallId, new ShapeRecognitionStartRequest("new", SessionId: f.SessionId), default);
        Assert.Equal(StatusCodes.Status202Accepted, Assert.IsType<AcceptedResult>(started).StatusCode);
        await f.Runner.WhenIdleAsync(f.SessionId);

        var status = Body<ShapeRecognitionStatusResponse>(await api.Status(h.WallId, default));
        Assert.Equal("Completed", status.Status);
        var list = Body<List<ShapeProposalResponse>>(await api.Proposals(h.WallId, null, default));
        Assert.Equal(ids[1], list[0].HoldId);

        var adjusted = new List<ShapePointDto> { new(0, -0.02), new(0.02, 0.02), new(-0.02, 0.02) };
        var decided = await api.Decide(
            h.WallId,
            new ShapeDecisionsRequest([new(ids[1], "circle"), new(ids[2], "Adjusted", adjusted)], f.SessionId),
            default);
        Assert.Equal(2, Body<ShapeWriteResponse>(decided).Count);
        Assert.Equal(1, Body<ShapeWriteResponse>(await api.AcceptAbove(h.WallId, new ShapeAcceptAboveRequest(0.7, f.SessionId), default)).Count);

        await f.PromoteAsync(ids);
        await using var db = h.CreateContext();
        var holds = await db.Holds.AsNoTracking().Where(x => ids.Contains(x.Id)).ToDictionaryAsync(x => x.Id);
        Assert.Equal(HoldOutlineSource.AutoContour, holds[ids[0]].OutlineSource);
        Assert.Null(holds[ids[1]].ShapePoints);
        Assert.Equal(0.02, holds[ids[2]].ShapePoints![1].Dx, 6);
    }

    [Fact]
    public async Task MemberKey_IsForbidden()
    {
        using var h = new WallTestHarness();
        var f = new ShapeStepFixture(h);
        await f.StageAsync(ShapeStepFixture.AutoHold(0.6));
        h.ActingUser = await h.AddMemberAsync("member@test", WallRole.Member);
        var api = Bind(new WallUpdateShapesController(f.Service, NullLogger<WallUpdateShapesController>.Instance), h.WallId);

        var result = await api.Start(h.WallId, new ShapeRecognitionStartRequest(), default);

        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsType<ObjectResult>(result).StatusCode);
        h.ActingUser = h.Owner;
        Assert.Equal(ShapeRecognitionStatus.NotStarted, (await f.Service.GetStatusAsync(h.WallId)).Status);
    }

    [Fact]
    public async Task KioskSession_IsForbidden()
    {
        using var h = new WallTestHarness();
        await new ShapeStepFixture(h).StageAsync(ShapeStepFixture.AutoHold(0.6));
        var kiosk = Substitute.For<IKioskContext>();
        kiosk.IsKiosk.Returns(true);
        kiosk.KioskWallId.Returns(h.WallId);
        var api = Bind(new WallUpdateShapesController(new ShapeStepFixture(h, kiosk).Service, NullLogger<WallUpdateShapesController>.Instance), h.WallId);

        var result = await api.Status(h.WallId, default);

        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsType<ObjectResult>(result).StatusCode);
    }

    [Fact]
    public async Task KeyForAnotherWall_StaleSession_AndBadInput_AreRefused()
    {
        using var h = new WallTestHarness();
        var f = new ShapeStepFixture(h);
        var ids = await f.StageAsync(ShapeStepFixture.AutoHold(0.6));
        var api = Bind(new WallUpdateShapesController(f.Service, NullLogger<WallUpdateShapesController>.Instance), h.WallId);

        var otherWall = await api.Status(Guid.NewGuid(), default);
        var stale = await api.Start(h.WallId, new ShapeRecognitionStartRequest(SessionId: Guid.NewGuid()), default);
        var badScope = await api.Start(h.WallId, new ShapeRecognitionStartRequest("everything"), default);
        var badDecision = await api.Decide(h.WallId, new ShapeDecisionsRequest([new(ids[0], "maybe")]), default);

        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsType<ObjectResult>(otherWall).StatusCode);
        Assert.IsType<ConflictObjectResult>(stale);
        Assert.IsType<BadRequestObjectResult>(badScope);
        Assert.IsType<BadRequestObjectResult>(badDecision);
    }

    private static T Body<T>(IActionResult result) => Assert.IsType<T>(Assert.IsType<OkObjectResult>(result).Value);

    private static WallUpdateShapesController Bind(WallUpdateShapesController controller, Guid keyWallId)
    {
        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, "1"),
                new Claim(ApiKeyClaimTypes.Scope, ApiKeyScope.Wall.ToString()),
                new Claim(ApiKeyClaimTypes.ApiKeyId, Guid.NewGuid().ToString()),
                new Claim(ApiKeyClaimTypes.WallId, keyWallId.ToString()),
            ],
            ApiKeyAuthenticationHandler.SchemeName);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) },
        };
        return controller;
    }
}
