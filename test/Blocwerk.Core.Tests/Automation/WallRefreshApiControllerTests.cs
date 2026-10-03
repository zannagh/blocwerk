// <copyright file="WallRefreshApiControllerTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Reflection;
using Blocwerk.Authentication.Authorization;
using Blocwerk.Authentication.Handlers;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Refresh;
using Blocwerk.Core.Services;
using Blocwerk.Core.Tests.Refresh;
using Blocwerk.Web.Controllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Blocwerk.Core.Tests.Automation;

/// <summary>
/// "Update panels + 3D" over the API: a personal write key only, the page's own service calls end to end, Apply bound
/// to the summary's version, and every write in the change journal.
/// </summary>
public class WallRefreshApiControllerTests
{
    [Theory]
    [MemberData(nameof(AutomationApiFixture.KeyNames), MemberType = typeof(AutomationApiFixture))]
    public async Task OnlyAPersonalWriteKey_ReachesTheService(string keyName)
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        var refreshes = Substitute.For<IWallRefreshService>();
        var api = Api(h, refreshes).As(AutomationApiFixture.Key(keyName, h.WallId));

        var begin = await api.Begin(h.WallId);
        var current = await api.Current(h.WallId);

        if (keyName == "personal write")
        {
            Assert.Equal(StatusCodes.Status200OK, AutomationApiFixture.Status(begin));
            await refreshes.Received(1).BeginAsync(h.WallId);
            return;
        }

        Assert.Equal(StatusCodes.Status403Forbidden, AutomationApiFixture.Status(begin));
        Assert.Equal(StatusCodes.Status403Forbidden, AutomationApiFixture.Status(current));
        Assert.Empty(refreshes.ReceivedCalls());
        Assert.Empty(await AutomationApiFixture.ApiBatchesAsync(h));
    }

    [Fact]
    public void ThePolicies_KeepWallKeysOutOfThePanelUpdate_AndAdmitThemForWallPrep()
    {
        var refresh = typeof(WallRefreshApiController).GetCustomAttribute<AuthorizeAttribute>()!;
        var markers = typeof(WallMarkersController).GetCustomAttribute<AuthorizeAttribute>()!;

        Assert.Equal((BlocwerkPolicies.HumanOrUserApiKey, ApiKeyAuthenticationHandler.SchemeName), (refresh.Policy, refresh.AuthenticationSchemes));
        Assert.Equal((BlocwerkPolicies.AnyApiKey, ApiKeyAuthenticationHandler.SchemeName), (markers.Policy, markers.AuthenticationSchemes));
        Assert.True(typeof(WallAdminApiController).IsAssignableFrom(typeof(WallRefreshApiController)));
        Assert.True(typeof(WallAdminApiController).IsAssignableFrom(typeof(WallMarkersController)));
    }

    [Fact]
    public async Task AMemberWhoIsNotAWallAdmin_IsRefused_AndNothingIsAudited()
    {
        using var h = new WallTestHarness();
        using var s = new RefreshScenario(h);
        await s.SeedWallAsync();
        h.ActingUser = await h.AddMemberAsync("member@test", WallRole.Member);

        var result = await Api(h, s.Service).As(ApiKeys.Personal()).Begin(h.WallId);

        Assert.Equal(StatusCodes.Status403Forbidden, AutomationApiFixture.Status(result));
        Assert.Empty(await AutomationApiFixture.ApiBatchesAsync(h));
    }

    [Fact]
    public async Task APanelUpdate_RunsEndToEnd_AndEveryWriteIsJournalled()
    {
        using var h = new WallTestHarness();
        using var s = new RefreshScenario(h);
        s.Capture.Client.IsConfigured = false;
        await s.SeedWallAsync();
        var journal = AutomationApiFixture.Journal(h);
        var key = ApiKeys.Personal();
        var api = Api(h, s.Service, journal).As(key);

        var run = Value<WallRefreshView>(await api.Begin(h.WallId));
        await RefreshApiFlow.UploadAsync(h, s, journal, key, run.Id, 2);
        Assert.Equal(StatusCodes.Status202Accepted, AutomationApiFixture.Status(await api.Sort(h.WallId, run.Id)));
        await s.RunQueuedAsync();
        Assert.Equal(StatusCodes.Status202Accepted, AutomationApiFixture.Status(await api.Start(h.WallId, run.Id, null)));
        await s.RunQueuedAsync();

        var summary = Value<RefreshSummaryResponse>(await api.Summary(h.WallId, run.Id));
        Assert.True(summary.CanApply);
        Assert.Equal(WallRefreshStatus.ReadyToApply, summary.Status);
        Assert.False(string.IsNullOrEmpty(summary.DecisionsVersion));
        Assert.Equal(0, await RefreshApiFlow.GenerationAsync(h));

        var apply = await api.Apply(h.WallId, run.Id, new RefreshApplyRequest(summary.DecisionsVersion));
        Assert.Equal(StatusCodes.Status202Accepted, AutomationApiFixture.Status(apply));
        await s.RunQueuedAsync();

        Assert.Equal(WallRefreshStatus.Done, Value<WallRefreshView>(await api.Get(h.WallId, run.Id)).Status);
        Assert.Equal(1, await RefreshApiFlow.GenerationAsync(h));
        var batches = await AutomationApiFixture.ApiBatchesAsync(h);
        Assert.Equal<string[]>(
            ["refresh.begin", "refresh.upload", "refresh.sort", "refresh.start", "refresh.apply"],
            batches.Select(b => b.Label[ApiWriteAudit.LabelPrefix.Length..].Split(' ')[0]).ToArray());
        Assert.All(batches, b => Assert.Equal(h.Owner.Id.ToString(), b.Actor));
        Assert.All(batches, b => Assert.EndsWith($"key:{key.GetApiKeyId()}", b.Label));
        Assert.All(batches, b => Assert.Equal((ChangeJournalScopeKind.Wall, (Guid?)h.WallId), (b.ScopeKind, b.ScopeId)));
        Assert.All(batches, b => Assert.Equal(ChangeJournalStatus.Recorded, b.Status));
        Assert.StartsWith($"api:refresh.upload run:{run.Id} ", batches[1].Label);
    }

    [Fact]
    public async Task Apply_WithAStaleVersion_IsRefused_PromotesNothing_AndNamesTheCurrentVersion()
    {
        using var h = new WallTestHarness();
        using var s = new RefreshScenario(h);
        s.Capture.Client.IsConfigured = false;
        await s.SeedWallAsync();
        var view = await s.PrepareAsync();
        var api = Api(h, s.Service).As(ApiKeys.Personal());

        var stale = await api.Apply(h.WallId, view.Id, new RefreshApplyRequest("0123456789ABCDEF01234567"));
        var missing = await api.Apply(h.WallId, view.Id, new RefreshApplyRequest(null));

        var refused = Assert.IsType<RefreshApplyRefused>(Assert.IsType<ConflictObjectResult>(stale).Value);
        Assert.Equal(view.Summary!.DecisionsVersion, refused.CurrentDecisionsVersion);
        Assert.Equal(WallRefreshStatus.ReadyToApply, refused.Status);
        Assert.Contains("summary changed", refused.Error);
        Assert.Equal(StatusCodes.Status400BadRequest, AutomationApiFixture.Status(missing));
        Assert.Equal(WallRefreshStatus.ReadyToApply, (await s.CurrentAsync()).Status);
        Assert.Equal(0, await RefreshApiFlow.GenerationAsync(h));
        Assert.Empty(await AutomationApiFixture.ApiBatchesAsync(h));
    }

    [Fact]
    public async Task ARunThatIsNotTheWallsCurrentOne_IsNotFound()
    {
        using var h = new WallTestHarness();
        using var s = new RefreshScenario(h);
        await s.SeedWallAsync();
        await s.Service.BeginAsync(h.WallId);
        var api = Api(h, s.Service).As(ApiKeys.Personal());

        var result = await api.Discard(h.WallId, Guid.NewGuid());

        Assert.Equal(StatusCodes.Status404NotFound, AutomationApiFixture.Status(result));
        Assert.Equal(WallRefreshStatus.Uploading, (await s.CurrentAsync()).Status);
    }

    [Fact]
    public async Task Discard_ThrowsTheRunAway_AndIsJournalled()
    {
        using var h = new WallTestHarness();
        using var s = new RefreshScenario(h);
        await s.SeedWallAsync();
        var run = await s.Service.BeginAsync(h.WallId);
        var api = Api(h, s.Service).As(ApiKeys.Personal());

        Assert.Equal(StatusCodes.Status204NoContent, AutomationApiFixture.Status(await api.Discard(h.WallId, run.Id)));

        Assert.Null(await s.Service.GetCurrentAsync(h.WallId));
        Assert.StartsWith("api:refresh.discard key:", Assert.Single(await AutomationApiFixture.ApiBatchesAsync(h)).Label);
    }

    private static WallRefreshApiController Api(WallTestHarness h, IWallRefreshService refreshes, ChangeJournal? journal = null) =>
        new(refreshes, AutomationApiFixture.Audit(h, journal ?? AutomationApiFixture.Journal(h)), NullLogger<WallRefreshApiController>.Instance);

    private static T Value<T>(IActionResult result) => Assert.IsType<T>(Assert.IsAssignableFrom<ObjectResult>(result).Value);
}
