// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.IO.Compression;
using System.Text.Json;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Recognition;
using Blocwerk.Core.Runners;

namespace Blocwerk.Core.Tests;

/// <summary>
/// What a runner is offered from the recognition queue: interactive before background, oldest first; only kinds it
/// advertises, within its memory budget, of walls it may serve; one claim at a time; a job it failed goes to another first.
/// </summary>
public class RecognitionQueueClaimTests
{
    [Fact]
    public async Task Enqueue_StoresARecognitionEnvelope_AndTheLeaseOfItsLane()
    {
        using var h = new WallTestHarness();
        using var f = await RecognitionFixture.CreateAsync(h);

        var interactive = await f.EnqueueAsync(RecognitionLane.Interactive);
        var background = await f.EnqueueAsync(RecognitionLane.Background);

        Assert.Equal(30, interactive.LeaseSeconds);
        Assert.Equal(f.Clock.GetUtcNow() - TimeSpan.FromSeconds(1) + f.Options.InteractiveClaimWait, interactive.LeaseExpiresAt);
        Assert.Equal(120, background.LeaseSeconds);
        Assert.Null(background.LeaseExpiresAt);
        var path = f.Runners.Files.ResolvePhysicalPath(interactive.InputPath!)!;
        Assert.Equal(interactive.InputBytes, new FileInfo(path).Length);
        using var zip = ZipFile.OpenRead(path);
        using var manifest = JsonDocument.Parse(zip.GetEntry("parts.json")!.Open());
        Assert.Equal("recognition", manifest.RootElement.GetProperty("service").GetString());
        Assert.Equal(RecognitionKinds.Markers, manifest.RootElement.GetProperty("kind").GetString());
        Assert.NotNull(zip.GetEntry("photo.jpg"));
    }

    [Fact]
    public async Task Enqueue_RefusesAKindThatIsNotARecognitionKind()
    {
        using var h = new WallTestHarness();
        using var f = await RecognitionFixture.CreateAsync(h);

        await Assert.ThrowsAsync<ArgumentException>(() => f.EnqueueAsync(RecognitionLane.Interactive, kind: "splat"));
    }

    [Fact]
    public async Task Claim_TakesInteractiveBeforeBackground_ThenOldestFirst()
    {
        using var h = new WallTestHarness();
        using var f = await RecognitionFixture.CreateAsync(h);
        var pod = await f.RunnerAsync("pod");
        var oldBackground = await f.EnqueueAsync(RecognitionLane.Background);
        var firstInteractive = await f.EnqueueAsync(RecognitionLane.Interactive);
        var secondInteractive = await f.EnqueueAsync(RecognitionLane.Interactive);

        List<Guid> order = [];
        for (var i = 0; i < 3; i++)
        {
            var job = await f.Queue.TryClaimAsync(pod, CancellationToken.None);
            order.Add(job!.Id);
            await f.Queue.ResultAsync(pod, job.Id, "{}", CancellationToken.None);
        }

        Assert.Equal([firstInteractive.Id, secondInteractive.Id, oldBackground.Id], order);
    }

    [Fact]
    public async Task Claim_OffersOnlyAdvertisedKinds_WithinTheMemoryBudget()
    {
        using var h = new WallTestHarness();
        using var f = await RecognitionFixture.CreateAsync(h);
        var markersOnly = await f.RunnerAsync("markers only", capabilities: RecognitionKinds.Markers);
        var small = await f.RunnerAsync("small", memoryBudgetMb: 1000);
        var splatOnly = await f.RunnerAsync("3D only", capabilities: RunnerCapabilities.Splat);
        var detect = await f.EnqueueAsync(RecognitionLane.Interactive, RecognitionKinds.DetectHolds, memoryMb: 900);
        var big = await f.EnqueueAsync(RecognitionLane.Interactive, RecognitionKinds.Markers, memoryMb: 1500);

        Assert.Null(await f.Queue.TryClaimAsync(splatOnly, CancellationToken.None));
        Assert.Equal(big.Id, (await f.Queue.TryClaimAsync(markersOnly, CancellationToken.None))?.Id);
        Assert.Equal(detect.Id, (await f.Queue.TryClaimAsync(small, CancellationToken.None))?.Id);
    }

    [Fact]
    public async Task Claim_OffersOnlyJobsOfWallsTheRunnerMayServe()
    {
        using var h = new WallTestHarness();
        using var f = await RecognitionFixture.CreateAsync(h);
        var foreignWall = await f.Runners.AddWallAsync("foreign", foreign: true);
        var pod = await f.RunnerAsync("own runner");
        var foreign = await f.EnqueueAsync(RecognitionLane.Interactive, wallId: foreignWall);

        Assert.Null(await f.Queue.TryClaimAsync(pod, CancellationToken.None));
        var own = await f.EnqueueAsync(RecognitionLane.Background);
        Assert.Equal(own.Id, (await f.Queue.TryClaimAsync(pod, CancellationToken.None))?.Id);
        Assert.Equal(GpuJobStatus.Queued, (await f.RowAsync(foreign.Id)).Status);
    }

    [Fact]
    public async Task Claim_HoldsOneJobAtATime_AndRecordsTheClaim()
    {
        using var h = new WallTestHarness();
        using var f = await RecognitionFixture.CreateAsync(h);
        var pod = await f.RunnerAsync("pod");
        pod.ClaimToken = "process-1";
        var first = await f.EnqueueAsync(RecognitionLane.Interactive);
        await f.EnqueueAsync(RecognitionLane.Interactive);

        var claimed = await f.Queue.TryClaimAsync(pod, CancellationToken.None);

        Assert.Equal(first.Id, claimed?.Id);
        Assert.Null(await f.Queue.TryClaimAsync(pod, CancellationToken.None));
        Assert.Equal(GpuJobStatus.Claimed, claimed!.Status);
        Assert.Equal((pod.Id, "process-1", 1), (claimed.ClaimedByRunnerId!.Value, claimed.ClaimToken, claimed.Attempts));
        Assert.Equal(f.Clock.GetUtcNow() + TimeSpan.FromSeconds(30), claimed.LeaseExpiresAt);
    }

    [Fact]
    public async Task ARetryableFailure_GoesToAnotherOnlineRunnerFirst_ElseBackToTheSameOne()
    {
        using var h = new WallTestHarness();
        using var f = await RecognitionFixture.CreateAsync(h);
        var pod = await f.RunnerAsync("pod");
        var laptop = await f.RunnerAsync("laptop");
        var job = await f.EnqueueAsync(RecognitionLane.Background);

        await f.Queue.TryClaimAsync(pod, CancellationToken.None);
        await f.Queue.FailAsync(pod, job.Id, new RunnerFailure("out of memory", Retryable: true), CancellationToken.None);
        Assert.Null(await f.Queue.TryClaimAsync(pod, CancellationToken.None));
        Assert.Equal([pod.Id], GpuJobFailedRunners.Parse((await f.RowAsync(job.Id)).FailedRunnerIdsJson));

        // The laptop goes offline: the pod gets it back.
        await f.Runners.MarkOnlineAsync(laptop, f.Clock.GetUtcNow() - TimeSpan.FromMinutes(5));
        Assert.Equal(job.Id, (await f.Queue.TryClaimAsync(pod, CancellationToken.None))?.Id);
    }

    [Fact]
    public async Task ARunnerThatLostTheWall_IsToldTheJobIsGone_AndTheJobIsRequeuedFree()
    {
        using var h = new WallTestHarness();
        using var f = await RecognitionFixture.CreateAsync(h);
        var pod = await f.RunnerAsync("pod");
        var job = await f.EnqueueAsync(RecognitionLane.Background);
        await f.Queue.TryClaimAsync(pod, CancellationToken.None);

        await using (var db = h.CreateContext())
        {
            db.GpuRunnerWalls.RemoveRange(db.GpuRunnerWalls);
            await db.SaveChangesAsync();
        }

        Assert.Equal(RunnerJobOutcome.Gone, await f.Queue.ProgressAsync(pod, job.Id, new RunnerProgress(0.5, null, null, "run", null), CancellationToken.None));
        var row = await f.RowAsync(job.Id);
        Assert.Equal((GpuJobStatus.Queued, 0, (Guid?)null), (row.Status, row.Attempts, row.ClaimedByRunnerId));
    }
}
