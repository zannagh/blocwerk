// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Detection.Outlines;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Recognition;
using Blocwerk.Core.Recognition.Remote;
using Blocwerk.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The wall-update shape recognition through the recognition queue: one background <c>outline-holds</c> job per staged
/// panel photo, its outlines written as proposals; with <c>queue+fallback</c> a job the pod never claims runs in the app.
/// </summary>
public sealed class ShapeRecognitionQueueTests
{
    private static readonly List<BigUpdatePhoto> Staged = [new(CaptureScenario.FlatJpeg(0, 200, 100), "image/jpeg", 0, 0)];

    [Fact]
    public async Task OnePanelIsOneBackgroundJob_AndItsOutlinesBecomeProposals()
    {
        using var h = new WallTestHarness();
        using var f = await FakeRecognitionClaimant.FixtureAsync(h, RecognitionMode.Queue);
        var pod = await FakeRecognitionClaimant.CreateAsync(f);
        var inApp = Substitute.For<IHoldOutlineBatch>();
        var shapes = new ShapeStepFixture(h, outlineBatch: new QueuedHoldOutlineBatch(inApp, FakeRecognitionClaimant.Caller(f)));
        await shapes.StageAsync(Staged, ShapeStepFixture.AutoHold(0.9), ShapeStepFixture.AutoHold(0.2), ShapeStepFixture.AutoHold(0.5));

        var run = shapes.RecogniseAsync();
        var (job, _) = await FakeOutlineWorker.ServeAsync(pod, f, new SessionHoldOutlineBatch(ShapeStepFixture.Outliner(), null));
        var status = await run;
        var proposals = await shapes.Service.GetProposalsAsync(h.WallId);

        Assert.Equal((RecognitionKinds.OutlineHolds, RecognitionLane.Background), (job.Kind, job.Lane));
        Assert.StartsWith($"shape-outlines:{shapes.SessionId:N}:", job.CallerKey, StringComparison.Ordinal);
        Assert.Equal((ShapeRecognitionStatus.Completed, 3, 3), (status.Status, status.Total, status.Done));
        Assert.Equal(HoldOutlineMethod.CircleFallback, proposals[0].Method);
        Assert.Equal(2, proposals.Count(p => p.Method == HoldOutlineMethod.Contour));
        await inApp.DidNotReceiveWithAnyArgs().OutlineAsync(default!, default!, default!, default);
    }

    [Fact]
    public async Task WithFallback_AnUnclaimedJobOfAGonePod_RunsInTheApp()
    {
        using var h = new WallTestHarness();
        using var f = await FakeRecognitionClaimant.FixtureAsync(h, RecognitionMode.QueueWithFallback);
        await FakeRecognitionClaimant.InstanceAsync(f);
        var caller = new RecognitionCaller(f.Queue, NullLogger<RecognitionCaller>.Instance) { BackgroundSlice = TimeSpan.FromMilliseconds(100) };
        var shapes = new ShapeStepFixture(h, outlineBatch: new QueuedHoldOutlineBatch(new SessionHoldOutlineBatch(ShapeStepFixture.Outliner(), null), caller));
        await shapes.StageAsync(Staged, ShapeStepFixture.AutoHold(0.9), ShapeStepFixture.AutoHold(0.5));

        var run = shapes.RecogniseAsync();
        var job = Assert.Single(await FakeRecognitionClaimant.QueuedAsync(f));
        f.Clock.Advance(f.Options.InstanceStaleAfter + TimeSpan.FromSeconds(1));
        var status = await run;

        Assert.Equal((ShapeRecognitionStatus.Completed, 2), (status.Status, status.Done));
        Assert.Equal((RecognitionLane.Background, GpuJobStatus.Cancelled), (job.Lane, (await f.RowAsync(job.Id)).Status));
    }
}
