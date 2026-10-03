// <copyright file="WallRefreshApiReadTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Refresh;
using Blocwerk.Core.Services;
using Blocwerk.Core.Tests.Refresh;
using Blocwerk.Web.Controllers;
using Blocwerk.Web.Endpoints;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Blocwerk.Core.Tests.Automation;

/// <summary>
/// Polling the panel update over the API changes nothing: reads neither queue the 3D re-check nor record a swept run as
/// discarded. The re-check is started explicitly (POST …/recheck); the page keeps starting it on its own.
/// </summary>
public class WallRefreshApiReadTests
{
    [Fact]
    public async Task Reads_DoNotQueueTheRecheck_TheRecheckRouteDoes()
    {
        using var h = new WallTestHarness();
        using var s = new RefreshScenario(h);
        await s.SeedMarkerWallWithEarlierCaptureAsync();
        var prepared = await s.PrepareAsync();
        var model = await WallRefreshRecheckTests.MakeModelReadyAsync(h, prepared.CaptureId!.Value);
        var api = Api(h, s.Service);

        var summary = Value<RefreshSummaryResponse>(await api.Summary(h.WallId, prepared.Id));
        Assert.True(summary.Check3DPending);
        Assert.False(summary.CanApply);
        await AssertQueueEmptyAsync(s);

        var recheck = Value<RefreshRecheckResponse>(await api.Recheck(h.WallId, prepared.Id));
        Assert.True(recheck.Pending);
        await s.RunQueuedAsync();

        var after = Value<RefreshSummaryResponse>(await api.Summary(h.WallId, prepared.Id));
        Assert.Equal(model, after.Summary!.Attempted3DModelId);
        Assert.True(after.CanApply);
        Assert.StartsWith("api:refresh.recheck ", Assert.Single(await AutomationApiFixture.ApiBatchesAsync(h)).Label);
    }

    [Fact]
    public async Task ARecheckWithNothingDue_Answers200_AndIsNotAudited()
    {
        using var h = new WallTestHarness();
        using var s = new RefreshScenario(h);
        s.Capture.Client.IsConfigured = false;
        await s.SeedWallAsync();
        var prepared = await s.PrepareAsync();

        var result = await Api(h, s.Service).Recheck(h.WallId, prepared.Id);

        Assert.False(Assert.IsType<RefreshRecheckResponse>(Assert.IsType<OkObjectResult>(result).Value).Pending);
        await AssertQueueEmptyAsync(s);
        Assert.Empty(await AutomationApiFixture.ApiBatchesAsync(h));
    }

    [Fact]
    public async Task AnUploadByAKeyWhoseOwnerMayNotUpload_IsRefusedBeforeAnyAuditRow()
    {
        using var h = new WallTestHarness();
        using var s = new RefreshScenario(h);
        await s.SeedWallAsync();
        var run = await s.Service.BeginAsync(h.WallId);
        h.ActingUser = await h.AddMemberAsync("member@test", WallRole.Member);
        var journal = new ChangeJournal(() => throw new InvalidOperationException("No audit row may be written."));
        var http = new DefaultHttpContext { User = ApiKeys.Personal() };
        http.Request.Body = new UnreadableStream();

        var result = await WallRefreshUploadEndpoint.HandleAsync(
            run.Id, "IMG_1.jpg", http, s.Service, s.Capture.Options, CancellationToken.None, AutomationApiFixture.Audit(h, journal));

        Assert.IsType<ForbidHttpResult>(result);
    }

    [Fact]
    public async Task ARunWhosePhotosWereSwept_IsNotDiscardedByAnApiRead()
    {
        using var h = new WallTestHarness();
        using var s = new RefreshScenario(h);
        await s.SeedWallAsync();
        var run = await s.Service.BeginAsync(h.WallId);
        await using (var db = h.CreateContext())
        {
            db.WallCaptures.Remove(await db.WallCaptures.SingleAsync(c => c.Id == run.CaptureId));
            await db.SaveChangesAsync();
        }

        var read = await Api(h, s.Service).Current(h.WallId);

        Assert.Equal(StatusCodes.Status404NotFound, AutomationApiFixture.Status(read));
        Assert.Equal(WallRefreshStatus.Uploading, await StatusAsync(h, run.Id));
        Assert.Null(await s.Service.GetCurrentAsync(h.WallId));
        Assert.Equal(WallRefreshStatus.Discarded, await StatusAsync(h, run.Id));
    }

    private static async Task AssertQueueEmptyAsync(RefreshScenario s)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => s.Queue.DequeueAsync(cts.Token).AsTask());
    }

    private static async Task<WallRefreshStatus> StatusAsync(WallTestHarness h, Guid id)
    {
        await using var db = h.CreateContext();
        return await db.WallRefreshes.Where(r => r.Id == id).Select(r => r.Status).SingleAsync();
    }

    private static WallRefreshApiController Api(WallTestHarness h, IWallRefreshService refreshes) =>
        new WallRefreshApiController(
            refreshes, AutomationApiFixture.Audit(h, AutomationApiFixture.Journal(h)), NullLogger<WallRefreshApiController>.Instance)
            .As(ApiKeys.Personal());

    private static T Value<T>(IActionResult result) => Assert.IsType<T>(Assert.IsAssignableFrom<ObjectResult>(result).Value);
}
