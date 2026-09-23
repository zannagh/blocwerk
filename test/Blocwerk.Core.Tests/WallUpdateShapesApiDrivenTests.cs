// <copyright file="WallUpdateShapesApiDrivenTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Security.Claims;
using Blocwerk.Authentication.Authorization;
using Blocwerk.Authentication.Handlers;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Blocwerk.Web.Components.Shared;
using Blocwerk.Web.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The shape step driven ONLY through the API moves the update's resume cursor exactly as the wizard would,
/// so opening the wizard afterwards lands on the right step. Also: an interrupted run resumes on startup.
/// </summary>
public class WallUpdateShapesApiDrivenTests
{
    [Fact]
    public async Task ApiOnly_StartReviewComplete_ThenTheWizardResumesAtConfirm()
    {
        using var h = new WallTestHarness();
        var f = new ShapeStepFixture(h);
        var ids = await f.StageAsync(ShapeStepFixture.AutoHold(0.9), ShapeStepFixture.AutoHold(0.6));
        var api = Bind(new WallUpdateShapesController(f.Service, NullLogger<WallUpdateShapesController>.Instance), h.WallId);

        await api.Start(h.WallId, new ShapeRecognitionStartRequest(SessionId: f.SessionId), default);
        await f.Runner.WhenIdleAsync(f.SessionId);
        Assert.Equal(WallUpdatePhase.ShapeReview, await CursorAsync(h));
        Assert.Equal("ShapeReview", Body<ShapeRecognitionStatusResponse>(await api.Status(h.WallId, default)).Phase);

        await api.Decide(h.WallId, new ShapeDecisionsRequest([new(ids[0], "Accepted")], f.SessionId), default);
        Assert.Equal(WallUpdatePhase.ShapeReview, await CursorAsync(h));

        var done = Body<ShapeRecognitionStatusResponse>(
            await api.CompleteReview(h.WallId, new ShapeSessionRequest(f.SessionId), default));

        Assert.Equal("Confirm", done.Phase);
        Assert.Equal(WallUpdatePhase.Confirm, WallUpdateResume.TargetFor(await CursorAsync(h)));
        Assert.Equal("Resume at the confirmation", WallUpdateResume.LabelFor(WallUpdatePhase.Confirm));
    }

    [Fact]
    public async Task ApiSkip_PutsTheWizardOnConfirm_AndFinishesTheStep()
    {
        using var h = new WallTestHarness();
        var f = new ShapeStepFixture(h);
        await f.StageAsync(ShapeStepFixture.AutoHold(0.9));
        var api = Bind(new WallUpdateShapesController(f.Service, NullLogger<WallUpdateShapesController>.Instance), h.WallId);

        await api.Skip(h.WallId, new ShapeSessionRequest(f.SessionId), default);

        Assert.Equal(WallUpdatePhase.Confirm, WallUpdateResume.TargetFor(await CursorAsync(h)));
        var afterSkip = await api.CompleteReview(h.WallId, new ShapeSessionRequest(f.SessionId), default);
        Assert.IsType<OkObjectResult>(afterSkip);
    }

    [Fact]
    public async Task CompleteReview_BeforeTheRunFinished_IsRefused()
    {
        using var h = new WallTestHarness();
        var f = new ShapeStepFixture(h);
        await f.StageAsync(ShapeStepFixture.AutoHold(0.9));
        var api = Bind(new WallUpdateShapesController(f.Service, NullLogger<WallUpdateShapesController>.Instance), h.WallId);

        Assert.IsType<ConflictObjectResult>(await api.CompleteReview(h.WallId, new ShapeSessionRequest(f.SessionId), default));
    }

    [Fact]
    public async Task InterruptedRun_IsResumedOnStartup()
    {
        using var h = new WallTestHarness();
        var f = new ShapeStepFixture(h);
        await f.StageAsync(ShapeStepFixture.AutoHold(0.9), ShapeStepFixture.AutoHold(0.6));
        await using (var db = h.CreateContext())
        {
            var session = await db.WallUpdateSessions.SingleAsync(s => s.Id == f.SessionId);
            session.ShapeStatus = ShapeRecognitionStatus.Running;
            session.Phase = WallUpdatePhase.Shapes;
            await db.SaveChangesAsync();
        }

        var resumer = new WallShapeRecognitionResumer(h.RootContextFactory, f.Runner, NullLogger<WallShapeRecognitionResumer>.Instance);
        Assert.Equal(1, await resumer.ResumeInterruptedAsync(default));
        await f.Runner.WhenIdleAsync(f.SessionId);

        var status = await f.Service.GetStatusAsync(h.WallId);
        Assert.Equal((ShapeRecognitionStatus.Completed, 2), (status.Status, status.Done));
        Assert.Equal(WallUpdatePhase.ShapeReview, status.Phase);
    }

    private static async Task<WallUpdatePhase> CursorAsync(WallTestHarness h)
    {
        var info = await WallUpdateSessionFixture.Sessions(h).GetOpenSessionAsync(h.WallId);
        return info!.Phase;
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
