// <copyright file="WallCaptureCoverageControllerTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Reflection;
using Blocwerk.Authentication.Authorization;
using Blocwerk.Authentication.Handlers;
using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Capture.Coverage;
using Blocwerk.Core.Enums;
using Blocwerk.Web.Controllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>
/// GET /api/walls/{wallId}/captures/{captureId}/coverage: authorised like the other capture routes (a wall key for the
/// wall or a personal key with write access, whose owner is a wall admin; members, read-only keys and kiosk sessions
/// are refused), a capture only under its own wall, and a done capture's report computed on first read.
/// </summary>
public class WallCaptureCoverageControllerTests
{
    [Fact]
    public void Route_IsAuthorisedLikeTheOtherCaptureRoutes()
    {
        var authorize = typeof(WallCaptureCoverageController).GetCustomAttribute<AuthorizeAttribute>();
        var get = typeof(WallCaptureCoverageController).GetMethod(nameof(WallCaptureCoverageController.Get))!.GetCustomAttribute<HttpGetAttribute>();

        Assert.Equal(BlocwerkPolicies.AnyApiKey, authorize!.Policy);
        Assert.Equal(ApiKeyAuthenticationHandler.SchemeName, authorize.AuthenticationSchemes);
        Assert.Equal("api/walls/{wallId:guid}/captures", typeof(WallCaptureCoverageController).GetCustomAttribute<RouteAttribute>()!.Template);
        Assert.Equal("{captureId:guid}/coverage", get!.Template);
        Assert.True(typeof(WallScopedApiController).IsAssignableFrom(typeof(WallCaptureCoverageController)));
    }

    [Fact]
    public async Task AnAdminsKey_GetsTheReport_ComputedOnFirstReadForADoneCapture()
    {
        using var h = new WallTestHarness();
        var (captureId, modelId) = await CaptureFollowUpChainTests.SeedAsync(h);
        await CoverageReportFollowUpStepTests.SetDoneAsync(h, captureId);

        var personal = Assert.IsType<ContentResult>(await Api(h, ApiKeys.Personal()).Get(h.WallId, captureId, default));
        var wallKey = Assert.IsType<ContentResult>(await Api(h, ApiKeys.Wall(h.WallId)).Get(h.WallId, captureId, default));

        var report = CaptureCoverageReport.Parse(personal.Content);
        Assert.Equal("application/json", personal.ContentType);
        Assert.Equal(modelId, report!.ModelId);
        Assert.Equal(report.ComputedAt, CaptureCoverageReport.Parse(wallKey.Content)!.ComputedAt);
    }

    [Fact]
    public async Task ACaptureIsOnlyServedUnderItsWall_AndAnUnfinishedOneHasNoReport()
    {
        using var h = new WallTestHarness();
        var (captureId, _) = await CaptureFollowUpChainTests.SeedAsync(h);
        var api = Api(h, ApiKeys.Personal());

        Assert.IsType<NotFoundObjectResult>(await api.Get(h.WallId, Guid.NewGuid(), default));
        Assert.IsType<NotFoundObjectResult>(await api.Get(h.WallId, captureId, default));
        Assert.Equal(StatusCodes.Status403Forbidden, Status(await api.Get(Guid.NewGuid(), captureId, default)));
    }

    [Fact]
    public async Task ReadOnlyKeys_MembersKeys_OtherWallsKeys_AndKiosks_AreRefused()
    {
        using var h = new WallTestHarness();
        var (captureId, _) = await CaptureFollowUpChainTests.SeedAsync(h);
        await CoverageReportFollowUpStepTests.SetDoneAsync(h, captureId);
        var kiosk = Substitute.For<IKioskContext>();
        kiosk.IsKiosk.Returns(true);
        kiosk.KioskWallId.Returns(_ => h.WallId);

        Assert.Equal(StatusCodes.Status403Forbidden, Status(await Api(h, ApiKeys.Personal(allowWrite: false)).Get(h.WallId, captureId, default)));
        Assert.Equal(StatusCodes.Status403Forbidden, Status(await Api(h, ApiKeys.Wall(Guid.NewGuid())).Get(h.WallId, captureId, default)));
        Assert.Equal(StatusCodes.Status403Forbidden, Status(await Api(h, ApiKeys.Personal(), kiosk).Get(h.WallId, captureId, default)));
        h.ActingUser = await h.AddMemberAsync("member@test", WallRole.Member);
        Assert.Equal(StatusCodes.Status403Forbidden, Status(await Api(h, ApiKeys.Personal()).Get(h.WallId, captureId, default)));
        await using var db = h.CreateContext();
        Assert.Null(db.WallCaptures.Single(c => c.Id == captureId).CoverageJson);
    }

    private static WallCaptureCoverageController Api(WallTestHarness h, System.Security.Claims.ClaimsPrincipal key, IKioskContext? kiosk = null)
    {
        var controller = new WallCaptureCoverageController(
            CoverageReportFollowUpStepTests.Service(h, kiosk), NullLogger<WallCaptureCoverageController>.Instance);
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = key } };
        return controller;
    }

    private static int? Status(IActionResult result) => Assert.IsAssignableFrom<ObjectResult>(result).StatusCode;
}
