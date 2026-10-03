// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Net;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Runners;
using Blocwerk.Web.Endpoints;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The runner API's access rules over HTTP: the address limit cannot be dodged by rotating keys, each runner has its
/// own limit, a runner that lost its eligibility gets 410 on the bundle (and the job goes back), and with
/// <c>RUNNERS__MODE=off</c> the API does not exist.
/// </summary>
public class RunnerApiHardeningTests
{
    private const string Api = RunnerApiEndpoints.Prefix;

    [Fact]
    public async Task RotatingKeyPrefixes_IsStillLimited_ByAddress()
    {
        using var h = new WallTestHarness();
        using var f = await RunnerFixture.CreateAsync(h);
        await using var host = await RunnerApiTestHost.StartAsync(f);

        HttpResponseMessage? last = null;
        var sent = 0;
        while (sent < RunnerApiEndpoints.AddressBurst + 10 && last?.StatusCode != HttpStatusCode.TooManyRequests)
        {
            last = await host.PostJsonAsync($"{Api}/hello", GpuRunnerTokens.Create().Token, "{}"); // a new prefix every time
            sent++;
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, last!.StatusCode);
        Assert.Equal(RunnerApiEndpoints.AddressBurst + 1, sent);
    }

    [Fact]
    public async Task AnAuthenticatedRunner_HasItsOwnLimit()
    {
        using var h = new WallTestHarness();
        using var f = await RunnerFixture.CreateAsync(h);
        await using var host = await RunnerApiTestHost.StartAsync(f);
        var (_, key) = await f.AddRunnerAsync("chatty", walls: h.WallId);

        HttpResponseMessage? last = null;
        var sent = 0;
        while (sent < RunnerApiEndpoints.AddressBurst && last?.StatusCode != HttpStatusCode.TooManyRequests)
        {
            last = await host.PostJsonAsync($"{Api}/hello", key, "{}");
            sent++;
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, last!.StatusCode);
        Assert.True(sent < RunnerApiEndpoints.AddressBurst, $"the runner's own bucket should empty first ({sent} calls)");
        Assert.NotNull(last.Headers.RetryAfter);
    }

    [Fact]
    public async Task WithdrawnApproval_MakesTheBundleCall410_AndTheJobGoesBackForFree()
    {
        using var h = new WallTestHarness();
        using var f = await RunnerFixture.CreateAsync(h);
        await using var host = await RunnerApiTestHost.StartAsync(f);
        var other = await f.AddWallAsync("Other wall");
        var (shared, key) = await f.AddRunnerAsync("shared", shared: true, walls: other);
        await f.ApproveAsync(h.WallId, shared);
        var job = await f.AddJobAsync(h.WallId);
        Assert.Equal(HttpStatusCode.OK, (await host.PostJsonAsync($"{Api}/claim", key, "{}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await host.SendAsync(HttpMethod.Get, $"{Api}/jobs/{job.Id}/bundle", key)).StatusCode);

        await using (var db = h.CreateContext())
        {
            await db.GpuRunnerApprovals.ExecuteDeleteAsync();
        }

        Assert.Equal(HttpStatusCode.Gone, (await host.SendAsync(HttpMethod.Get, $"{Api}/jobs/{job.Id}/bundle", key)).StatusCode);
        await using var check = h.CreateContext();
        var row = await check.GpuJobs.SingleAsync();
        Assert.Equal((GpuJobStatus.Queued, (Guid?)null, 0), (row.Status, row.ClaimedByRunnerId, row.FailureCount));
    }

    [Fact]
    public async Task OwnerLosingWallAdmin_MakesProgress410()
    {
        using var h = new WallTestHarness();
        using var f = await RunnerFixture.CreateAsync(h);
        await using var host = await RunnerApiTestHost.StartAsync(f);
        var admin = await h.AddMemberAsync("co-admin@test", Enums.WallRole.Admin);
        var (_, key) = await f.AddRunnerAsync("theirs", ownerId: admin.Id, walls: h.WallId);
        var job = await f.AddJobAsync(h.WallId);
        Assert.Equal(HttpStatusCode.OK, (await host.PostJsonAsync($"{Api}/claim", key, "{}")).StatusCode);

        await using (var db = h.CreateContext())
        {
            await db.WallMembers.Where(m => m.UserId == admin.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.Role, Enums.WallRole.Member));
        }

        Assert.Equal(HttpStatusCode.Gone, (await host.PostJsonAsync($"{Api}/jobs/{job.Id}/progress", key, "{\"fraction\":0.2}")).StatusCode);
    }

    [Fact]
    public async Task ModeOff_DoesNotMapTheRunnerApi()
    {
        using var h = new WallTestHarness();
        using var f = await RunnerFixture.CreateAsync(h, new GpuRunnerOptions { Mode = GpuRunnerMode.Off });
        await using var host = await RunnerApiTestHost.StartAsync(f);
        var (_, key) = await f.AddRunnerAsync("gpu", walls: h.WallId);

        Assert.Equal(HttpStatusCode.NotFound, (await host.PostJsonAsync($"{Api}/hello", key, "{}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await host.PostJsonAsync($"{Api}/claim", key, "{}")).StatusCode);
    }
}
