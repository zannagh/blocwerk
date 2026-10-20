// <copyright file="LookOverNewHoldApiTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Text.Json;
using Blocwerk.Core.Enums;
using Blocwerk.Core.LookOver;
using Blocwerk.Web.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Blocwerk.Core.Tests.Automation;

/// <summary>
/// The answer route takes the per-boulder "use the new hold" (<c>newHoldBoulderIds</c>) and hands it to the service;
/// a list that is not among the boulders read, or that goes with another answer, is a bad request.
/// </summary>
public class LookOverNewHoldApiTests
{
    [Fact]
    public void TheBody_ReadsNewHoldBoulderIds()
    {
        var id = Guid.NewGuid();
        var json = $"{{\"answer\":\"UseNewHold\",\"expectedBoulderIds\":[\"{id}\"],\"newHoldBoulderIds\":[\"{id}\"]}}";

        var body = JsonSerializer.Deserialize<LookOverAnswerRequest>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

        Assert.Equal((LookOverAnswer?)LookOverAnswer.UseNewHold, body.Answer);
        Assert.Equal([id], body.NewHoldBoulderIds);
    }

    [Fact]
    public async Task TheRoute_PassesTheChosenBoulders_AndRefusesAListOutsideTheBouldersRead()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        var lookOver = Substitute.For<IWallLookOverService>();
        lookOver.GetStateAsync(h.WallId).Returns(new WallLookOverState(true, DateTimeOffset.UtcNow, 3, 2, 5, null, null, true));
        var api = new WallLookOverAnswersApiController(
            lookOver, AutomationApiFixture.Audit(h, AutomationApiFixture.Journal(h)), NullLogger<WallLookOverAnswersApiController>.Instance).As(ApiKeys.Personal());
        var (finding, yes, no) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        var mixed = await api.Answer(h.WallId, finding, new LookOverAnswerRequest(LookOverAnswer.UseNewHold, [yes, no], [yes]));
        var outside = await api.Answer(h.WallId, finding, new LookOverAnswerRequest(LookOverAnswer.UseNewHold, [yes], [no]));
        var otherAnswer = await api.Answer(h.WallId, finding, new LookOverAnswerRequest(LookOverAnswer.RemoveHold, [yes], [yes]));

        Assert.Equal(
            (StatusCodes.Status200OK, StatusCodes.Status400BadRequest, StatusCodes.Status400BadRequest),
            (AutomationApiFixture.Status(mixed), AutomationApiFixture.Status(outside), AutomationApiFixture.Status(otherAnswer)));
        await lookOver.Received(1).AnswerAsync(
            h.WallId,
            finding,
            LookOverAnswer.UseNewHold,
            Arg.Is<IReadOnlyCollection<Guid>>(b => b.Count == 2),
            Arg.Is<IReadOnlyCollection<Guid>>(b => b.Single() == yes),
            Arg.Any<CancellationToken>());
        Assert.Single(lookOver.ReceivedCalls(), c => c.GetMethodInfo().Name == nameof(IWallLookOverService.AnswerAsync));
    }
}
