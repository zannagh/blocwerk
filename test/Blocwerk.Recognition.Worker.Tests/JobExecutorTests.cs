// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.IO.Compression;
using System.Net;
using System.Text.Json.Nodes;
using Blocwerk.Recognition.Worker.Envelope;
using Blocwerk.Recognition.Worker.Jobs;
using Blocwerk.Recognition.Worker.Runtime;
using Microsoft.Extensions.Logging.Abstractions;

namespace Blocwerk.Recognition.Worker.Tests;

/// <summary>One claimed job against a fake server: result, failures, 410 and the memory watchdog.</summary>
public class JobExecutorTests
{
    private static readonly object PresenceRequest = new { version = 1, queries = new[] { new { newX = 1, newY = 1, oldX = 1, oldY = 1, scale = 1 } } };

    private readonly FakeRunnerServer server = new();

    [Fact]
    public async Task ASucceededJobPutsTheStatusZip()
    {
        var claim = Serve(PresenceBundle());

        await Executor(new StubHandler("presence", (e, _) => Task.FromResult(PresenceHandler.ToResult([0.5])))).RunAsync(claim, CancellationToken.None);

        using var zip = new ZipArchive(new MemoryStream(server.Body("/result")!));
        var status = JsonNode.Parse(zip.GetEntry(EnvelopeResult.StatusName)!.Open())!;
        Assert.Equal("succeeded", (string?)status["status"]);
        Assert.Equal(0.5, (double)status["result"]!["scores"]![0]!);
        Assert.Equal(claim.JobId.ToString("N"), (string?)status["jobId"]);
        Assert.Null(server.Body("/fail"));
    }

    [Fact]
    public async Task AGoneJobIsAbortedWithoutResultOrFail()
    {
        var claim = Serve(PresenceBundle(), leaseSeconds: 1);
        server.ProgressStatus = HttpStatusCode.Gone;

        await Executor(Waiting()).RunAsync(claim, CancellationToken.None);

        Assert.NotNull(server.Body("/progress"));
        Assert.Null(server.Body("/result"));
        Assert.Null(server.Body("/fail"));
    }

    [Fact]
    public async Task MemoryAboveTheLimitFailsTheJobAsResource()
    {
        var claim = Serve(PresenceBundle());
        var handler = Waiting();
        handler.MemoryBytes = 2L * 1024 * 1024 * 1024;

        await Executor(handler, maxMemoryMb: 1024).RunAsync(claim, CancellationToken.None);

        var failure = Failure();
        Assert.Equal(("resource", true), ((string?)failure["failureKind"], (bool)failure["retryable"]!));
        Assert.Null(server.Body("/result"));
    }

    [Fact]
    public async Task ABundleWithAnotherHashIsRetryable()
    {
        var claim = Serve(PresenceBundle()) with { BundleSha256 = new string('0', 64) };
        var ran = false;

        await Executor(new StubHandler("presence", (_, _) => Task.FromResult<object>(ran = true))).RunAsync(claim, CancellationToken.None);

        Assert.False(ran);
        Assert.True((bool)Failure()["retryable"]!);
    }

    [Fact]
    public async Task ABadEnvelopeFailsForGood()
    {
        var claim = Serve(TestBundles.Envelope("presence", PresenceRequest, ("photos", "a.png", TestBundles.Png())));

        await Executor(Waiting()).RunAsync(claim, CancellationToken.None);

        Assert.False((bool)Failure()["retryable"]!);
    }

    [Fact]
    public async Task StoppingHandsTheJobBack()
    {
        var claim = Serve(PresenceBundle());
        using var stopping = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        await Executor(Waiting()).RunAsync(claim, stopping.Token);

        Assert.Equal((true, false), ((bool)Failure()["shutdown"]!, (bool)Failure()["retryable"]!));
    }

    private static byte[] PresenceBundle() =>
        TestBundles.Envelope("presence", PresenceRequest, ("old", "old.png", TestBundles.Png()), ("new", "new.png", TestBundles.Png()));

    private static StubHandler Waiting() => new("presence", async (_, ct) =>
    {
        await Task.Delay(Timeout.Infinite, ct);
        return new object();
    });

    private Protocol.RunnerClaim Serve(byte[] bundle, int leaseSeconds = 30)
    {
        server.Bundle = bundle;
        return FakeRunnerServer.ClaimFor(bundle, "presence", leaseSeconds);
    }

    private JsonNode Failure() => JsonNode.Parse(server.Body("/fail") ?? throw new InvalidOperationException("no fail call"))!;

    private JobExecutor Executor(StubHandler handler, long maxMemoryMb = 0)
    {
        var options = TestOptions.Create() with { JobMaxMemoryMb = maxMemoryMb };
        return new JobExecutor(server.Client(options), [handler], handler, options, TimeProvider.System, NullLogger<JobExecutor>.Instance);
    }
}
