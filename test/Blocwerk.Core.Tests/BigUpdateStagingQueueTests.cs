// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Configuration;
using Blocwerk.Core.Detection.Gating;
using Blocwerk.Core.Recognition;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>
/// Staging through the recognition queue: every panel's detection is queued before any is answered, so two runners
/// share the wall, and each panel gets its own detections whatever order the answers come in.
/// </summary>
public sealed class BigUpdateStagingQueueTests : IDisposable
{
    private readonly string modelFile = Path.Combine(Path.GetTempPath(), $"holds-model-{Guid.NewGuid():N}.onnx");

    public BigUpdateStagingQueueTests() => File.WriteAllBytes(modelFile, [1, 2, 3, 4]);

    public void Dispose() => File.Delete(modelFile);

    [Fact]
    public async Task Stage_QueuesEveryPanelAtOnce_AndKeepsEachPanelsResult()
    {
        using var h = new WallTestHarness();
        using var f = await FakeRecognitionClaimant.FixtureAsync(h, RecognitionMode.Queue);
        var first = await FakeRecognitionClaimant.CreateAsync(f, "pod");
        var second = await FakeRecognitionClaimant.CreateAsync(f, "macbook");
        var service = new WallBigUpdateService(
            h.DbContextFactory, h.CurrentUser, FakeRecognitionClaimant.Detector(f, h.HoldDetection, modelFile),
            Substitute.For<IHoldOverlapMatcher>(), NullLogger<WallBigUpdateService>.Instance);
        var photos = WallUpdateSessionFixture.CentreAndNeighbour();

        var staging = service.StageAsync(h.WallId, photos);
        Assert.Equal(2, (await FakeRecognitionClaimant.QueuedAsync(f, 2)).Count);
        var jobs = new[] { await first.ClaimAsync(), await second.ClaimAsync() };
        var neighbour = Array.FindIndex(jobs, j => FakeRecognitionClaimant.PhotoOf(f, j).SequenceEqual(photos[1].Image));
        var claimants = new[] { first, second };
        await claimants[neighbour].CompleteAsync(jobs[neighbour], FakeRecognitionClaimant.Boxes(2));
        await claimants[1 - neighbour].CompleteAsync(jobs[1 - neighbour], FakeRecognitionClaimant.Boxes(1));
        await staging;

        await using var db = h.CreateContext();
        var holds = await db.Holds.AsNoTracking().Where(x => x.WallPanelId != null)
            .Join(db.WallPanels, x => x.WallPanelId, p => p.Id, (x, p) => new { p.Col, p.Row })
            .ToListAsync();
        Assert.Equal(1, holds.Count(x => (x.Col, x.Row) == (0, 0)));
        Assert.Equal(2, holds.Count(x => (x.Col, x.Row) == (1, 0)));
        await h.HoldDetection.DidNotReceiveWithAnyArgs().DetectHoldsAsync(default!, default);
    }

    /// <summary>
    /// No pod (nothing is queued) or a pod that never claims (every job times out at once): either way each panel is detected
    /// in the app inside its own gate slot, so eight panels never race for the one slot and none fails as busy.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Stage_WithFallback_NoRunnerServes_EightPanelsAreDetectedInTheApp_WithoutBusy(bool podAlive)
    {
        using var h = new WallTestHarness();
        using var f = await FakeRecognitionClaimant.FixtureAsync(h, RecognitionMode.QueueWithFallback);
        if (podAlive)
        {
            await FakeRecognitionClaimant.InstanceAsync(f);
        }

        using var gate = new ImageRecognitionGate(1, TimeSpan.FromMilliseconds(400), NullLogger.Instance);
        h.HoldDetection.DetectHoldsAsync(default!, default).ReturnsForAnyArgs(async _ =>
        {
            await Task.Delay(100);
            return new List<DetectedHold> { new(0.5, 0.5, 0.05, "red", 0.9) };
        });
        var inApp = new GatedHoldDetectionService(h.HoldDetection, gate, new HoldDetectionSettings(), NullLogger.Instance);
        var service = new WallBigUpdateService(
            h.DbContextFactory, h.CurrentUser, FakeRecognitionClaimant.Detector(f, inApp, modelFile),
            Substitute.For<IHoldOverlapMatcher>(), NullLogger<WallBigUpdateService>.Instance, recognitionGate: gate);
        var photos = Enumerable.Range(0, 8).Select(i => new BigUpdatePhoto(CaptureScenario.TinyJpeg(i), "image/jpeg", i, 0)).ToList();

        var staging = service.StageAsync(h.WallId, photos);
        if (podAlive)
        {
            await FakeRecognitionClaimant.QueuedAsync(f, 8);
            f.Clock.Advance(f.Options.InteractiveClaimWait + TimeSpan.FromSeconds(1));
        }

        await staging;

        await using var db = h.CreateContext();
        Assert.Equal(8, await db.Holds.AsNoTracking().CountAsync(x => x.WallPanelId != null));
        Assert.Equal(podAlive ? 8 : 0, await db.RecognitionJobs.CountAsync());
    }
}
