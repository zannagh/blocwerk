// <copyright file="WallHoldShapesControllerTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Reflection;
using System.Security.Claims;
using Blocwerk.Authentication.Authorization;
using Blocwerk.Authentication.Handlers;
using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Services;
using Blocwerk.Web.Controllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The outline upgrade (preview, apply, revert) and "refine 3D hold shapes" over the API: the services' own answers
/// for a writable personal key; a key without write access never reaches them; the services' admin and kiosk
/// refusals become 403 and their written refusals 409.
/// </summary>
public class WallHoldShapesControllerTests
{
    private readonly IHoldOutlineUpgradeService outlines = Substitute.For<IHoldOutlineUpgradeService>();
    private readonly IHoldFootprintService footprints = Substitute.For<IHoldFootprintService>();
    private readonly Guid wallId = Guid.NewGuid();

    [Fact]
    public void Route_IsWallOrPersonalKeyOnly()
    {
        var authorize = typeof(WallHoldShapesController).GetCustomAttribute<AuthorizeAttribute>();

        Assert.Equal(BlocwerkPolicies.AnyApiKey, authorize!.Policy);
        Assert.Equal(ApiKeyAuthenticationHandler.SchemeName, authorize.AuthenticationSchemes);
        Assert.Equal("api/walls/{wallId:guid}/holds", typeof(WallHoldShapesController).GetCustomAttribute<RouteAttribute>()!.Template);
    }

    [Fact]
    public async Task WritablePersonalKey_GetsTheServicesAnswers()
    {
        var runId = Guid.NewGuid();
        var preview = new HoldOutlineUpgradePreview { Photos = 3, Eligible = 40 };
        var applied = new HoldOutlineUpgradeResult(runId, 40, 30, 10, 0, 30, 30, 0);
        var reverted = new HoldOutlineRevertResult(30, [], 0);
        var refined = new HoldFootprintRunResult(25, 5, 0, 12);
        outlines.PreviewAsync(wallId, new HoldOutlineUpgradeOptions(true), Arg.Any<CancellationToken>()).Returns(preview);
        outlines.ApplyAsync(wallId, new HoldOutlineUpgradeOptions(false), Arg.Any<CancellationToken>()).Returns(applied);
        outlines.RevertAsync(wallId, runId, Arg.Any<CancellationToken>()).Returns(reverted);
        footprints.RefineAsync(wallId, Arg.Any<CancellationToken>()).Returns(refined);
        var api = Api(ApiKeys.Personal());

        Assert.Same(preview, Body(await api.PreviewOutlines(wallId, new OutlineUpgradeRequest(IncludeManual: true), default)));
        Assert.Same(applied, Body(await api.ApplyOutlines(wallId, null, default)));
        Assert.Same(reverted, Body(await api.RevertOutlines(wallId, runId, default)));
        Assert.Same(refined, Body(await api.RefineShapes(wallId, default)));
    }

    [Fact]
    public async Task PersonalKeyWithoutWriteAccess_NeverReachesTheServices()
    {
        var api = Api(ApiKeys.Personal(allowWrite: false));

        Assert.Equal(StatusCodes.Status403Forbidden, Status(await api.ApplyOutlines(wallId, null, default)));
        Assert.Equal(StatusCodes.Status403Forbidden, Status(await api.RefineShapes(wallId, default)));
        await outlines.DidNotReceiveWithAnyArgs().ApplyAsync(default, default!, default);
        await footprints.DidNotReceiveWithAnyArgs().RefineAsync(default, default);
    }

    [Fact]
    public async Task NonAdminAndKiosk_Are403_WrittenRefusals409()
    {
        outlines.ApplyAsync(wallId, Arg.Any<HoldOutlineUpgradeOptions>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new UnauthorizedAccessException());
        outlines.PreviewAsync(wallId, Arg.Any<HoldOutlineUpgradeOptions>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new KioskRestrictedException("Upgrading hold outlines is not available from a kiosk device."));
        footprints.RefineAsync(wallId, Arg.Any<CancellationToken>())
            .ThrowsAsync(new UserFacingException("This wall has no active geometry model, or outline detection is off."));
        var api = Api(ApiKeys.Personal());

        Assert.Equal(StatusCodes.Status403Forbidden, Status(await api.ApplyOutlines(wallId, null, default)));
        Assert.Equal(StatusCodes.Status403Forbidden, Status(await api.PreviewOutlines(wallId, null, default)));
        var conflict = Assert.IsType<ConflictObjectResult>(await api.RefineShapes(wallId, default));
        Assert.Contains("no active geometry model", Assert.IsType<ApiErrorResponse>(conflict.Value).Message);
    }

    private static object? Body(IActionResult result) => Assert.IsType<OkObjectResult>(result).Value;

    private static int? Status(IActionResult result) => Assert.IsAssignableFrom<ObjectResult>(result).StatusCode;

    private WallHoldShapesController Api(ClaimsPrincipal key) => new(outlines, footprints, NullLogger<WallHoldShapesController>.Instance)
    {
        ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = key } },
    };
}
