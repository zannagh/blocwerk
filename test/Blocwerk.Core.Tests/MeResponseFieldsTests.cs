// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Entities;
using Blocwerk.Core.Services;
using Blocwerk.Web.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The fields the app asked for on existing <c>/api/v1/me</c> answers: the wall's name on a session, the TopLogger gym's
/// name on an activity, and an empty <c>clientRequestId</c> refused before anything is written.
/// </summary>
public sealed class MeResponseFieldsTests
{
    [Fact]
    public async Task AStartedSession_NamesItsWall()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        var controller = new MeSessionsController(h.SessionService);

        var ok = Assert.IsType<OkObjectResult>(await controller.Start(new StartSessionRequest { WallId = h.WallId }));

        Assert.Equal("Test Wall", Assert.IsType<SessionResponse>(ok.Value).WallName);
    }

    [Fact]
    public void AnActivity_NamesItsTopLoggerGym()
    {
        var at = DateTimeOffset.UtcNow;
        var summary = new ActivitySummary(Guid.NewGuid(), DateOnly.FromDateTime(at.UtcDateTime), at, 60, 3, 0, 0, null, "Boulderhalle");
        var detail = new ActivityDetail(Guid.NewGuid(), at, 60, false, [], [], [], null, "Boulderhalle");

        Assert.Equal("Boulderhalle", summary.ToResponse().GymName);
        Assert.Equal("Boulderhalle", detail.ToResponse().GymName);
    }

    [Fact]
    public async Task TheEmptyClientRequestId_IsA400_OnTrainingAndSessions()
    {
        var training = Substitute.For<ITrainingService>();
        var sessions = Substitute.For<ISessionService>();
        var trainingController = new MeTrainingController(training, Substitute.For<IProgressionService>())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };

        Assert.IsType<BadRequestObjectResult>(await trainingController.SaveHangboard(
            new SaveHangboardRequest { EdgeSizeMm = 20, DurationSeconds = 10, ClientRequestId = Guid.Empty }));
        Assert.IsType<BadRequestObjectResult>(await trainingController.SavePullups(
            new SavePullupRequest { Repetitions = 5, ClientRequestId = Guid.Empty }));
        Assert.IsType<BadRequestObjectResult>(await new MeSessionsController(sessions).Start(
            new StartSessionRequest { WallId = Guid.NewGuid(), ClientRequestId = Guid.Empty }));
        await sessions.DidNotReceiveWithAnyArgs().StartSessionAsync(default, default);
    }
}
