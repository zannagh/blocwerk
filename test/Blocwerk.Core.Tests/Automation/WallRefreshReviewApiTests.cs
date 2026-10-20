// <copyright file="WallRefreshReviewApiTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Text.Json;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Refresh.Review;
using Blocwerk.Core.Tests.Refresh;
using Blocwerk.Web.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace Blocwerk.Core.Tests.Automation;

/// <summary>
/// The one review over the API: read it (groups and answers by name), answer items by key, accept a whole group, and a
/// refused answer answers 409 with where it stopped; every write is audited like the page's other writes.
/// </summary>
public class WallRefreshReviewApiTests
{
    [Fact]
    public async Task TheReview_IsReadAnswered_AndAccepted_OverTheApi()
    {
        using var h = new WallTestHarness();
        using var s = await ReviewScenario.PrepareAsync(h);
        var api = Api(h, s).As(ApiKeys.Personal());

        var read = Assert.IsType<JsonResult>(await api.Review(h.WallId, s.View.Id, s.Review));
        var json = JsonSerializer.Serialize(read.Value, ReviewJson.Options);
        Assert.Contains("\"group\":\"notFound\"", json);
        Assert.Contains("\"answers\":[\"stillThere\",\"gone\"]", json);

        var answered = await api.AnswerReview(
            h.WallId, s.View.Id, new ReviewAnswersRequest([new($"k-{s.Old[0].Id}", ReviewAnswer.StillThere)]), s.Review);
        var accepted = await api.AcceptReviewGroup(h.WallId, s.View.Id, new ReviewAcceptRequest(ReviewGroup.NewHold, null), s.Review);

        Assert.Equal(StatusCodes.Status202Accepted, AutomationApiFixture.Status(answered));
        var body = Assert.IsType<ReviewAnswered>(Assert.IsType<JsonResult>(accepted).Value);
        Assert.Equal((2, 1, 1), (body.Answered, body.Counts!.Open, body.Counts.ToCheck));
        var labels = (await AutomationApiFixture.ApiBatchesAsync(h)).Select(b => b.Label[ApiWriteAudit.LabelPrefix.Length..].Split(' ')[0]).ToList();
        Assert.Equal<string[]>(["refresh.review", "refresh.review.accept"], [.. labels]);
    }

    [Fact]
    public async Task ARefusedAnswer_Is409_AndSaysWhichItem()
    {
        using var h = new WallTestHarness();
        using var s = await ReviewScenario.PrepareAsync(h);
        var api = Api(h, s).As(ApiKeys.Personal());
        var key = $"k-{s.Old[0].Id}";

        var refused = await api.AnswerReview(h.WallId, s.View.Id, new ReviewAnswersRequest([new(key, ReviewAnswer.KeepNew)]), s.Review);
        var empty = await api.AnswerReview(h.WallId, s.View.Id, new ReviewAnswersRequest([]), s.Review);

        Assert.Equal(StatusCodes.Status409Conflict, AutomationApiFixture.Status(refused));
        var stopped = Assert.IsType<ReviewAnswered>(Assert.IsType<JsonResult>(refused).Value);
        Assert.Equal((0, key), (stopped.Answered, stopped.FailedKey));
        Assert.Equal(StatusCodes.Status400BadRequest, AutomationApiFixture.Status(empty));
    }

    private static WallRefreshApiController Api(WallTestHarness h, ReviewScenario s) =>
        new(s.Refresh.Service, AutomationApiFixture.Audit(h, AutomationApiFixture.Journal(h)), NullLogger<WallRefreshApiController>.Instance);
}
