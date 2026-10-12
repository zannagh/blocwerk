// <copyright file="LookOverApiTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Reflection;
using Blocwerk.Authentication.Authorization;
using Blocwerk.Authentication.Handlers;
using Blocwerk.Core.Enums;
using Blocwerk.Core.LookOver;
using Blocwerk.Core.Refresh;
using Blocwerk.Core.Tests.Automation;
using Blocwerk.Web.Controllers;
using Blocwerk.Web.Endpoints;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using static Blocwerk.Core.Tests.LookOver.LookOverFixture;

namespace Blocwerk.Core.Tests.LookOver;

/// <summary>
/// The look-over over the machine API is read-only and takes the key of the panel update: a personal key with write
/// access whose owner may look over the wall. The pictures follow the same rule through the service.
/// </summary>
public class LookOverApiTests
{
    [Theory]
    [MemberData(nameof(AutomationApiFixture.KeyNames), MemberType = typeof(AutomationApiFixture))]
    public async Task OnlyAPersonalWriteKey_ReachesTheService(string keyName)
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        var service = Substitute.For<IWallLookOverService>();
        service.GetStateAsync(h.WallId).Returns(new WallLookOverState(true, DateTimeOffset.UtcNow, 3, 2, 5, null, null, true));
        var api = new WallLookOverApiController(service, NullLogger<WallLookOverApiController>.Instance).As(AutomationApiFixture.Key(keyName, h.WallId));

        var state = await api.State(h.WallId);
        var findings = await api.Findings(h.WallId);

        var expected = keyName == "personal write" ? StatusCodes.Status200OK : StatusCodes.Status403Forbidden;
        Assert.Equal((expected, expected), (AutomationApiFixture.Status(state), AutomationApiFixture.Status(findings)));
        if (expected == StatusCodes.Status403Forbidden)
        {
            Assert.Empty(service.ReceivedCalls());
        }
    }

    [Fact]
    public void ThePolicy_IsThePanelUpdates_AndTheControllerOnlyReads()
    {
        var authorize = typeof(WallLookOverApiController).GetCustomAttribute<AuthorizeAttribute>()!;

        Assert.Equal((BlocwerkPolicies.HumanOrUserApiKey, ApiKeyAuthenticationHandler.SchemeName), (authorize.Policy, authorize.AuthenticationSchemes));
        var actions = typeof(WallLookOverApiController).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
        Assert.All(actions, a => Assert.NotNull(a.GetCustomAttribute<HttpGetAttribute>()));
    }

    [Fact]
    public async Task AScript_ReadsTheFlagSinceWhenAndTheFindings_AfterAnApply()
    {
        using var h = new WallTestHarness();
        var w = await ApplyAsync(h, targetDetected: true, score: 0.1);
        var api = new WallLookOverApiController(Service(h), NullLogger<WallLookOverApiController>.Instance).As(ApiKeys.Personal());

        var state = Value<WallLookOverState>(await api.State(w.WallId));
        var findings = Value<LookOverFindingsResponse>(await api.Findings(w.WallId));

        Assert.Equal((true, 1, (int?)3), (state.Pending, state.OpenFindings, state.Generation));
        Assert.NotNull(state.PendingSince);
        var finding = Assert.Single(findings.Findings);
        Assert.Equal((LookOverFindingKind.ReplacedByOther, LookOverFindingStatus.Open, "Crimp line"), (finding.Kind, finding.Status, finding.Boulders.Single().Name));
        Assert.Equal(1, findings.State.OpenFindings);
    }

    [Fact]
    public async Task AKeyWhoseOwnerIsOnlyAMember_IsRefused()
    {
        using var h = new WallTestHarness();
        var w = await ApplyAsync(h, targetDetected: true, score: 0.1);
        h.ActingUser = await AddMemberAsync(h, w.WallId, WallRole.Member);
        var api = new WallLookOverApiController(Service(h), NullLogger<WallLookOverApiController>.Instance).As(ApiKeys.Personal());

        Assert.Equal(StatusCodes.Status403Forbidden, AutomationApiFixture.Status(await api.State(w.WallId)));
        Assert.Equal(StatusCodes.Status403Forbidden, AutomationApiFixture.Status(await api.Findings(w.WallId)));
    }

    [Fact]
    public async Task ThePictures_AreMadeFromTheRetiredAndTheLivePhoto_AndOnlyForWhoMayLookOver()
    {
        using var h = new WallTestHarness();
        var w = await ApplyAsync(h, targetDetected: true, score: 0.1);
        var findingId = (await FindingAsync(h, w.WallId)).Id;
        var service = Service(h);

        // The old hold sits on the retired panel, which keeps its photo; the new photo is the live panel's.
        Assert.NotNull(await service.GetCropAsync(w.WallId, findingId, CheckCropView.Old));
        Assert.NotNull(await service.GetCropAsync(w.WallId, findingId, CheckCropView.New));
        Assert.Null(await service.GetCropAsync(w.WallId, Guid.NewGuid(), CheckCropView.New));
        var picture = await WallLookOverCropEndpoint.HandleAsync(w.WallId, findingId, "new", new DefaultHttpContext(), service, CancellationToken.None);
        Assert.Equal("image/jpeg", Assert.IsType<FileContentHttpResult>(picture).ContentType);
        Assert.IsType<NotFound>(await WallLookOverCropEndpoint.HandleAsync(w.WallId, findingId, "sideways", new DefaultHttpContext(), service, CancellationToken.None));

        h.ActingUser = await AddMemberAsync(h, w.WallId, WallRole.Member);
        var refused = await WallLookOverCropEndpoint.HandleAsync(w.WallId, findingId, "new", new DefaultHttpContext(), Service(h), CancellationToken.None);
        Assert.IsType<ForbidHttpResult>(refused);
    }

    private static T Value<T>(IActionResult result) => Assert.IsType<T>(Assert.IsAssignableFrom<ObjectResult>(result).Value);
}
