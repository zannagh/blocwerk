// <copyright file="WallHoldColorsApiControllerTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Reflection;
using Blocwerk.Authentication.Authorization;
using Blocwerk.Authentication.Handlers;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Blocwerk.Core.Tests.HoldColors;
using Blocwerk.Web.Controllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Blocwerk.Core.Tests.Automation;

/// <summary>
/// "Suggest colours for holds without one" over the API: the panel update's key (a personal write key whose owner is a
/// wall admin), the page's own service call, and the write in the change journal.
/// </summary>
public class WallHoldColorsApiControllerTests
{
    [Theory]
    [MemberData(nameof(AutomationApiFixture.KeyNames), MemberType = typeof(AutomationApiFixture))]
    public async Task OnlyAPersonalWriteKey_ReachesTheService(string keyName)
    {
        using var s = await HoldColorScenario.CreateAsync();
        var proposals = Substitute.For<IHoldColorProposalService>();
        proposals.SuggestMissingAsync(s.WallId, Arg.Any<CancellationToken>()).Returns(new HoldColorProposalReport());

        var result = await Api(s, proposals).As(AutomationApiFixture.Key(keyName, s.WallId)).Suggest(s.WallId);

        if (keyName == "personal write")
        {
            Assert.Equal(StatusCodes.Status200OK, AutomationApiFixture.Status(result));
            await proposals.Received(1).SuggestMissingAsync(s.WallId, Arg.Any<CancellationToken>());
            return;
        }

        Assert.Equal(StatusCodes.Status403Forbidden, AutomationApiFixture.Status(result));
        Assert.Empty(proposals.ReceivedCalls());
        Assert.Empty(await AutomationApiFixture.ApiBatchesAsync(s.Harness));
    }

    [Fact]
    public void ThePolicy_IsThePanelUpdates()
    {
        var authorize = typeof(WallHoldColorsApiController).GetCustomAttribute<AuthorizeAttribute>()!;

        Assert.Equal((BlocwerkPolicies.HumanOrUserApiKey, ApiKeyAuthenticationHandler.SchemeName), (authorize.Policy, authorize.AuthenticationSchemes));
        Assert.True(typeof(WallAdminApiController).IsAssignableFrom(typeof(WallHoldColorsApiController)));
    }

    [Fact]
    public async Task ARun_AnswersTheCounts_AndIsJournalled()
    {
        using var s = await HoldColorScenario.CreateAsync();
        var panel = await s.AddPanelAsync(0);
        var blue = await s.AddHoldAsync(panel, HoldColorScenario.BlueSpot);
        await s.AddHoldAsync(panel, HoldColorScenario.WallSpot);

        var result = await Api(s, s.Service()).As(ApiKeys.Personal()).Suggest(s.WallId);

        var body = Assert.IsType<HoldColorSuggestionResponse>(Assert.IsAssignableFrom<ObjectResult>(result).Value);
        Assert.Equal((2, 1, 0, 1), (body.Considered, body.Proposed, body.FromLinked, body.LeftUnset));
        Assert.Equal(1, body.ProposedByColor["blue"]);
        Assert.Equal(1, body.LeftUnsetByReason["LikeSurroundings"]);
        Assert.Equal("blue", (await s.GetAsync(blue)).Color);
        var batch = Assert.Single(await AutomationApiFixture.ApiBatchesAsync(s.Harness));
        Assert.StartsWith("api:holds.suggest-colors key:", batch.Label);
    }

    [Fact]
    public async Task AMemberWhoIsNotAWallAdmin_IsRefused_AndNothingIsAudited()
    {
        using var s = await HoldColorScenario.CreateAsync();
        var hold = await s.AddHoldAsync(await s.AddPanelAsync(0), HoldColorScenario.BlueSpot);
        s.Harness.ActingUser = await s.Harness.AddMemberAsync("member@test", WallRole.Member);

        var result = await Api(s, s.Service()).As(ApiKeys.Personal()).Suggest(s.WallId);

        Assert.Equal(StatusCodes.Status403Forbidden, AutomationApiFixture.Status(result));
        Assert.Null((await s.GetAsync(hold)).Color);
        Assert.Empty(await AutomationApiFixture.ApiBatchesAsync(s.Harness));
    }

    private static WallHoldColorsApiController Api(HoldColorScenario s, IHoldColorProposalService proposals) =>
        new(proposals, AutomationApiFixture.Audit(s.Harness, AutomationApiFixture.Journal(s.Harness)), NullLogger<WallHoldColorsApiController>.Instance);
}
