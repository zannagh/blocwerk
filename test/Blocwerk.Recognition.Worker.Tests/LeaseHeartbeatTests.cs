// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Net;
using Blocwerk.Recognition.Worker.Runtime;
using Microsoft.Extensions.Logging.Abstractions;

namespace Blocwerk.Recognition.Worker.Tests;

/// <summary>The lease heartbeat: a hung or failed beat is retried, never the end of the loop; an answered one is contact.</summary>
public class LeaseHeartbeatTests
{
    private readonly FakeRunnerServer server = new();
    private readonly WorkerOptions options = TestOptions.Create();

    [Fact]
    public async Task AHungBeatTimesOutAndTheNextOneIsSent_AnsweredBeatsCountAsContact()
    {
        server.ProgressDelays.Enqueue(TimeSpan.FromMinutes(1));
        var health = new WorkerHealth(options, TimeProvider.System);
        using var job = new CancellationTokenSource();

        var heartbeat = await BeatAsync(health, job, beats: 3);

        Assert.False(heartbeat.Gone);
        Assert.False(job.IsCancellationRequested);
        Assert.NotNull(health.LastContact);
        Assert.True(health.Live);
    }

    [Fact]
    public async Task AFailingBeatIsRetried_WithoutContact()
    {
        server.ProgressStatus = HttpStatusCode.InternalServerError;
        var health = new WorkerHealth(options, TimeProvider.System);
        using var job = new CancellationTokenSource();

        var heartbeat = await BeatAsync(health, job, beats: 2);

        Assert.False(heartbeat.Gone);
        Assert.False(job.IsCancellationRequested);
        Assert.Null(health.LastContact);
    }

    /// <summary>Runs a heartbeat on a 6 s lease (a beat every second, each given a second) until <paramref name="beats"/> calls.</summary>
    private async Task<LeaseHeartbeat> BeatAsync(WorkerHealth health, CancellationTokenSource job, int beats)
    {
        var claim = FakeRunnerServer.ClaimFor([1], "presence", leaseSeconds: 6);
        var heartbeat = LeaseHeartbeat.Start(server.Client(options), claim, job, health, NullLogger.Instance);
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (server.Calls.Count(c => c.Path.EndsWith("/progress", StringComparison.Ordinal)) < beats && DateTime.UtcNow < until)
        {
            await Task.Delay(50);
        }

        await heartbeat.DisposeAsync();
        Assert.True(server.Calls.Count(c => c.Path.EndsWith("/progress", StringComparison.Ordinal)) >= beats);
        return heartbeat;
    }
}
