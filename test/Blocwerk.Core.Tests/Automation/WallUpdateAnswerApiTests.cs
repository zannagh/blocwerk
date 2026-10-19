// <copyright file="WallUpdateAnswerApiTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Reflection;
using System.Text.Json;
using Blocwerk.Authentication.Authorization;
using Blocwerk.Authentication.Handlers;
using Blocwerk.Core.Enums;
using Blocwerk.Core.LookOver;
using Blocwerk.Core.Refresh;
using Blocwerk.Core.Tests.Refresh;
using Blocwerk.Web.Controllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Blocwerk.Core.Tests.Automation;

/// <summary>
/// The human answers of an update over the API: the confirm screen's cards (list, answer one, answer several) and the
/// look-over (answer, undo, finish). Same key as the rest of the panel update (a personal write key), the page's own
/// service calls, every write audited.
/// </summary>
public class WallUpdateAnswerApiTests
{
    [Fact]
    public async Task TheCards_AreListedWithTheirAnswers_AndAnsweredOneOrSeveral()
    {
        using var h = new WallTestHarness();
        using var s = new RefreshScenario(h);
        s.Capture.Client.IsConfigured = false;
        await s.SeedWallAsync();
        var view = await s.PrepareAsync();
        var refreshes = Substitute.For<IWallRefreshService>();
        refreshes.PeekCurrentAsync(h.WallId).Returns(view);
        var card = Guid.NewGuid();
        refreshes.GetChecksAsync(view.Id).Returns([new RefreshCheck(card, UpdateExceptionKind.PossiblyRemoved, UpdateExceptionStatus.Kept, true, false, 1, "Crimp", null)]);
        refreshes.DecideChecksAsync(view.Id, Arg.Any<IReadOnlyList<RefreshCheckAnswer>>()).Returns(ci => ci.Arg<IReadOnlyList<RefreshCheckAnswer>>().Count);
        var api = RefreshApi(h, refreshes).As(ApiKeys.Personal());

        var listed = Value<IReadOnlyList<RefreshCheck>>(await api.Checks(h.WallId, view.Id));
        var one = await api.AnswerCheck(h.WallId, view.Id, card, new RefreshCheckAnswerRequest(UpdateExceptionAnswer.Remove));
        var many = await api.AnswerChecks(h.WallId, view.Id, new RefreshCheckAnswersRequest(
            [new RefreshCheckAnswerItem(card, UpdateExceptionAnswer.Keep), new RefreshCheckAnswerItem(Guid.NewGuid(), UpdateExceptionAnswer.Undo)]));

        Assert.Equal(UpdateExceptionStatus.Kept, Assert.Single(listed).Status);
        Assert.Equal((StatusCodes.Status202Accepted, 1), (AutomationApiFixture.Status(one), Value<RefreshCheckAnswered>(one).Answered));
        Assert.Equal((StatusCodes.Status202Accepted, 2), (AutomationApiFixture.Status(many), Value<RefreshCheckAnswered>(many).Answered));
        await refreshes.Received(1).DecideChecksAsync(view.Id, Arg.Is<IReadOnlyList<RefreshCheckAnswer>>(a => a.Count == 1 && a[0].Answer == UpdateExceptionAnswer.Remove));
        Assert.Equal(2, (await AutomationApiFixture.ApiBatchesAsync(h)).Count);
    }

    [Fact]
    public async Task AnAnswer_IsRefusedWithoutAKnownAnswer_OrWithAWallKey()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        var refreshes = Substitute.For<IWallRefreshService>();

        var empty = await RefreshApi(h, refreshes).As(ApiKeys.Personal()).AnswerChecks(h.WallId, Guid.NewGuid(), new RefreshCheckAnswersRequest([]));
        var wallKey = await RefreshApi(h, refreshes).As(AutomationApiFixture.Key("wall write", h.WallId))
            .AnswerCheck(h.WallId, Guid.NewGuid(), Guid.NewGuid(), new RefreshCheckAnswerRequest(UpdateExceptionAnswer.Keep));

        Assert.Equal((StatusCodes.Status400BadRequest, StatusCodes.Status403Forbidden), (AutomationApiFixture.Status(empty), AutomationApiFixture.Status(wallKey)));
        Assert.Empty(refreshes.ReceivedCalls());
    }

    [Fact]
    public async Task TheCardAnswers_RunEndToEnd_ThroughThePagesService()
    {
        using var h = new WallTestHarness();
        using var s = new RefreshScenario(h);
        s.Capture.Client.IsConfigured = false;
        await s.SeedWallAsync();
        var view = await s.PrepareAsync();
        var api = RefreshApi(h, s.Service).As(ApiKeys.Personal());

        var listed = Value<IReadOnlyList<RefreshCheck>>(await api.Checks(h.WallId, view.Id));
        var unknown = await api.AnswerCheck(h.WallId, view.Id, Guid.NewGuid(), new RefreshCheckAnswerRequest(UpdateExceptionAnswer.Keep));

        Assert.Equal(listed.Count, (await s.Service.GetChecksAsync(view.Id)).Count);
        Assert.Equal(StatusCodes.Status409Conflict, AutomationApiFixture.Status(unknown));
    }

    [Fact]
    public async Task SeveralAnswers_StoppingPartWay_SayWhereToResume()
    {
        using var h = new WallTestHarness();
        using var s = new RefreshScenario(h);
        s.Capture.Client.IsConfigured = false;
        await s.SeedWallAsync();
        var view = await s.PrepareAsync();
        var refreshes = Substitute.For<IWallRefreshService>();
        refreshes.PeekCurrentAsync(h.WallId).Returns(view);
        var failed = Guid.NewGuid();
        refreshes.DecideChecksAsync(view.Id, Arg.Any<IReadOnlyList<RefreshCheckAnswer>>())
            .Returns<int>(_ => throw new RefreshCheckAnswersStoppedException(3, failed, new Core.Services.UserFacingException("gone")));
        var api = RefreshApi(h, refreshes).As(ApiKeys.Personal());

        var result = await api.AnswerChecks(h.WallId, view.Id, new RefreshCheckAnswersRequest([new RefreshCheckAnswerItem(Guid.NewGuid(), UpdateExceptionAnswer.Keep)]));
        var tooMany = await api.AnswerChecks(h.WallId, view.Id, new RefreshCheckAnswersRequest(
            Enumerable.Range(0, WallRefreshService.MaxCheckAnswers + 1).Select(_ => new RefreshCheckAnswerItem(Guid.NewGuid(), UpdateExceptionAnswer.Keep)).ToList()));

        var body = Value<RefreshCheckAnswered>(result);
        Assert.Equal((StatusCodes.Status202Accepted, 3, (Guid?)failed, "gone"), (AutomationApiFixture.Status(result), body.Answered, body.FailedCheckId, body.Error));
        Assert.Equal(StatusCodes.Status400BadRequest, AutomationApiFixture.Status(tooMany));
    }

    [Fact]
    public void TheAnswerBodies_TakeNamesOrNumbers()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        var named = JsonSerializer.Deserialize<RefreshCheckAnswerRequest>("{\"answer\":\"remove\"}", options)!;
        var numbered = JsonSerializer.Deserialize<RefreshCheckAnswersRequest>("{\"answers\":[{\"checkId\":\"" + Guid.Empty + "\",\"answer\":2}]}", options)!;
        var lookOver = JsonSerializer.Deserialize<LookOverAnswerRequest>("{\"answer\":\"Settle\"}", options)!;

        Assert.Equal(UpdateExceptionAnswer.Remove, named.Answer);
        Assert.Equal(UpdateExceptionAnswer.Undo, Assert.Single(numbered.Answers!).Answer);
        Assert.Equal(LookOverAnswer.Settle, lookOver.Answer);
    }

    [Fact]
    public async Task TheLookOver_IsAnsweredUndoneAndFinished_ThroughTheService()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        var lookOver = Substitute.For<IWallLookOverService>();
        var state = new WallLookOverState(true, DateTimeOffset.UtcNow, 3, 2, 5, null, null, true);
        lookOver.GetStateAsync(h.WallId).Returns(state);
        var api = LookOverApi(h, lookOver).As(ApiKeys.Personal());
        var finding = Guid.NewGuid();
        var boulder = Guid.NewGuid();

        var answer = await api.Answer(h.WallId, finding, new LookOverAnswerRequest(LookOverAnswer.RemoveHold, [boulder]));
        var undo = await api.Undo(h.WallId, finding);
        var finish = await api.Finish(h.WallId, new LookOverFinishRequest(LeaveOpen: true));
        var missing = await api.Answer(h.WallId, finding, new LookOverAnswerRequest(null));

        Assert.Equal(
            (StatusCodes.Status200OK, StatusCodes.Status200OK, StatusCodes.Status200OK, StatusCodes.Status400BadRequest),
            (AutomationApiFixture.Status(answer), AutomationApiFixture.Status(undo), AutomationApiFixture.Status(finish), AutomationApiFixture.Status(missing)));
        await lookOver.Received(1).AnswerAsync(h.WallId, finding, LookOverAnswer.RemoveHold, Arg.Is<IReadOnlyCollection<Guid>>(b => b.Single() == boulder), Arg.Any<CancellationToken>());
        await lookOver.Received(1).UndoAsync(h.WallId, finding, Arg.Any<CancellationToken>());
        await lookOver.Received(1).FinishAsync(h.WallId, true, Arg.Any<CancellationToken>());
        Assert.Equal(3, (await AutomationApiFixture.ApiBatchesAsync(h)).Count);
    }

    [Theory]
    [MemberData(nameof(AutomationApiFixture.KeyNames), MemberType = typeof(AutomationApiFixture))]
    public async Task OnlyAPersonalWriteKey_AnswersTheLookOver(string keyName)
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        var lookOver = Substitute.For<IWallLookOverService>();
        var api = LookOverApi(h, lookOver).As(AutomationApiFixture.Key(keyName, h.WallId));

        var finish = await api.Finish(h.WallId, new LookOverFinishRequest());

        if (keyName != "personal write")
        {
            Assert.Equal(StatusCodes.Status403Forbidden, AutomationApiFixture.Status(finish));
            Assert.Empty(lookOver.ReceivedCalls());
        }
    }

    [Fact]
    public void TheLookOverWrites_HaveThePanelUpdatesPolicy()
    {
        var authorize = typeof(WallLookOverAnswersApiController).GetCustomAttribute<AuthorizeAttribute>()!;

        Assert.Equal((BlocwerkPolicies.HumanOrUserApiKey, ApiKeyAuthenticationHandler.SchemeName), (authorize.Policy, authorize.AuthenticationSchemes));
        Assert.True(typeof(WallAdminApiController).IsAssignableFrom(typeof(WallLookOverAnswersApiController)));
    }

    private static WallRefreshApiController RefreshApi(WallTestHarness h, IWallRefreshService refreshes) =>
        new(refreshes, AutomationApiFixture.Audit(h, AutomationApiFixture.Journal(h)), NullLogger<WallRefreshApiController>.Instance);

    private static WallLookOverAnswersApiController LookOverApi(WallTestHarness h, IWallLookOverService lookOver) =>
        new(lookOver, AutomationApiFixture.Audit(h, AutomationApiFixture.Journal(h)), NullLogger<WallLookOverAnswersApiController>.Instance);

    private static T Value<T>(IActionResult result) => Assert.IsAssignableFrom<T>(Assert.IsAssignableFrom<ObjectResult>(result).Value);
}
