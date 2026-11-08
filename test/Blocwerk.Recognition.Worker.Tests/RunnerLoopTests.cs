// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Text.Json.Nodes;
using Blocwerk.Recognition.Worker.Jobs;
using Blocwerk.Recognition.Worker.Runtime;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Blocwerk.Recognition.Worker.Tests;

/// <summary>The runner loop against a fake server: the hello gate, claims and readiness.</summary>
public class RunnerLoopTests
{
    private readonly FakeRunnerServer server = new();
    private readonly IHostApplicationLifetime lifetime = Substitute.For<IHostApplicationLifetime>();
    private readonly StubHandler handler = new("presence", (_, _) => Task.FromResult(PresenceHandler.ToResult([0.7])));

    [Fact]
    public async Task AServerThatConfirmsNoRecognitionKindStopsTheWorker()
    {
        server.HelloAnswer = ContractFixtures.Text("runner/hello-response/valid/current-server.json");
        var (loop, health) = Loop();

        await loop.StartAsync(CancellationToken.None);
        await loop.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(10));

        lifetime.Received(1).StopApplication();
        Assert.Equal(RunnerLoop.ExitNotConfirmed, Environment.ExitCode);
        Assert.DoesNotContain(server.Calls, c => c.Path == "/api/runners/claim");
        Assert.False(health.Ready);
        Environment.ExitCode = 0;
    }

    [Fact]
    public async Task AConfirmedRunnerClaimsAndRunsJobsOneAtATime()
    {
        var bundle = TestBundles.Envelope(
            "presence", new { version = 1, queries = Array.Empty<object>() }, ("old", "o.png", TestBundles.Png()), ("new", "n.png", TestBundles.Png()));
        server.Bundle = bundle;
        server.Claims.Enqueue(FakeRunnerServer.ClaimFor(bundle, "presence"));
        using var stop = new CancellationTokenSource();
        server.OnClaim = () =>
        {
            if (server.Claims.Count == 0 && server.Calls.Any(c => c.Path.EndsWith("/result", StringComparison.Ordinal)))
            {
                stop.Cancel();
            }
        };
        var (loop, health) = Loop();

        await loop.StartAsync(CancellationToken.None);
        await Task.Delay(TimeSpan.FromSeconds(10), stop.Token).ContinueWith(_ => { }, TaskScheduler.Default);
        Assert.True(stop.IsCancellationRequested);
        Assert.True(health.Ready);
        await loop.StopAsync(CancellationToken.None);

        var hello = JsonNode.Parse(server.Body("/hello")!)!;
        Assert.Equal(["presence"], hello["capabilities"]!.AsArray().Select(c => (string?)c));
        Assert.True((long)hello["memoryBudgetMb"]! > 0);
        Assert.Equal(["presence"], health.Kinds);
        Assert.NotNull(server.Body("/result"));
        lifetime.DidNotReceive().StopApplication();
    }

    private (RunnerLoop Loop, WorkerHealth Health) Loop()
    {
        var options = TestOptions.Create();
        var api = server.Client(options);
        var health = new WorkerHealth(options, TimeProvider.System);
        var executor = new JobExecutor(api, [handler], handler, options, TimeProvider.System, NullLogger<JobExecutor>.Instance);
        var loop = new RunnerLoop(api, executor, handler, options, health, lifetime, NullLogger<RunnerLoop>.Instance);
        return (loop, health);
    }
}
