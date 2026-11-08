// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Detection.Outlines;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Geometry;
using Blocwerk.Core.Recognition;
using Blocwerk.Core.Recognition.Remote;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>
/// Hold outlines and texture matching through the recognition queue: a runner's outlines read back equal the in-process
/// ones, texture pairs are read back, and an unserved match runs in the app with <c>queue+fallback</c>.
/// </summary>
public sealed class RecognitionOutlineTextureClientTests
{
    private static readonly byte[] Photo = CaptureScenario.FlatJpeg(0, EnrichmentFakes.ImageSize, EnrichmentFakes.ImageSize);

    [Fact]
    public async Task Outlines_ARunnerAnswers_TheyEqualTheInProcessOutlines()
    {
        using var h = new WallTestHarness();
        using var f = await FakeRecognitionClaimant.FixtureAsync(h, RecognitionMode.Queue);
        var pod = await FakeRecognitionClaimant.CreateAsync(f);
        var inApp = Substitute.For<IHoldOutlineBatch>();
        var batch = new QueuedHoldOutlineBatch(inApp, FakeRecognitionClaimant.Caller(f));
        HoldSeed[] seeds = [new(0.3, 0.2, 0.02, 0.03, 0.04), new(0.5, 0.7, 0.01)];
        using var wall = RecognitionWallScope.Begin(h.WallId);

        var call = batch.OutlineAsync(Photo, seeds, HoldOutlineBatchOptions.Jointly);
        var (job, worker) = await FakeOutlineWorker.ServeAsync(pod, f, new SessionHoldOutlineBatch(EnrichmentFakes.Outlines(), null));
        var remote = await call;

        Assert.Equal((RecognitionKinds.OutlineHolds, RecognitionLane.Interactive), (job.Kind, job.Lane));
        Assert.Equal(HoldOutlineMethod.CircleFallback, remote.Outlines[1].Method);
        Assert.Equivalent(await new SessionHoldOutlineBatch(EnrichmentFakes.Outlines(), null).OutlineAsync(Photo, seeds, HoldOutlineBatchOptions.Jointly), remote, strict: true);
        Assert.Equivalent(worker, remote, strict: true);
        await inApp.DidNotReceiveWithAnyArgs().OutlineAsync(default!, default!, default!, default);
    }

    [Fact]
    public async Task Outlines_ARunnerAnswersWithAFailure_InQueueMode_RecognitionIsUnavailable()
    {
        using var h = new WallTestHarness();
        using var f = await FakeRecognitionClaimant.FixtureAsync(h, RecognitionMode.Queue);
        var pod = await FakeRecognitionClaimant.CreateAsync(f);
        var batch = new QueuedHoldOutlineBatch(Substitute.For<IHoldOutlineBatch>(), FakeRecognitionClaimant.Caller(f));
        using var wall = RecognitionWallScope.Begin(h.WallId);

        var call = batch.OutlineAsync(Photo, [new HoldSeed(0.3, 0.2, 0.02)], HoldOutlineBatchOptions.Separately);
        await pod.CompleteAsync(await pod.ClaimAsync(), """{"version":1,"outlines":[{"id":0,"failure":"seed outside the photo"}]}""");

        await Assert.ThrowsAsync<ImageRecognitionUnavailableException>(() => call);
    }

    [Fact]
    public async Task TextureMatch_ARunnerAnswers_ThePairsAreRead_AndTheMaskIsSent()
    {
        using var h = new WallTestHarness();
        using var f = await FakeRecognitionClaimant.FixtureAsync(h, RecognitionMode.Queue);
        var pod = await FakeRecognitionClaimant.CreateAsync(f);
        var inApp = Substitute.For<IPhotoTextureMatcher>();
        using var wall = RecognitionWallScope.Begin(h.WallId);
        using var session = new QueuedPhotoTextureMatcher(inApp, FakeRecognitionClaimant.Caller(f)).OpenQueued(Photo);

        var call = session.MatchAsync(CaptureScenario.TinyJpeg(1), CaptureScenario.TinyJpeg(2), 2.0, [1, 0, 0, 0, 1, 0, 0, 0, 1], new PhotoTextureAttempt(7, null, 1.05), CancellationToken.None);
        var job = await pod.ClaimAsync();
        var (request, _) = FakeOutlineWorker.Read(f, job);
        await pod.CompleteAsync(job, """{"version":1,"pairs":[{"srcX":1200.5,"srcY":800.25,"dstX":640.0,"dstY":410.5}],"coarseInliers":57}""");
        var match = await call;

        Assert.Equal((EnrichmentFakes.ImageSize, EnrichmentFakes.ImageSize), (session.Width, session.Height));
        Assert.Equal((RecognitionKinds.TextureMatch, 7, 9), (job.Kind, request.GetProperty("attempt").GetProperty("ransacSeed").GetInt32(), request.GetProperty("seed").GetArrayLength()));
        Assert.Equal([new PointCorrespondence(1200.5, 800.25, 640.0, 410.5)], match.Pairs);
        Assert.Equal((57, (string?)null), (match.CoarseInliers, match.Failure));
        inApp.DidNotReceiveWithAnyArgs().OpenPhoto(default!);
    }

    [Fact]
    public async Task TextureMatch_NobodyClaims_WithFallback_TheAppMatches()
    {
        using var h = new WallTestHarness();
        using var f = await FakeRecognitionClaimant.FixtureAsync(h, RecognitionMode.QueueWithFallback);
        await FakeRecognitionClaimant.InstanceAsync(f);
        var inAppSession = Substitute.For<IPhotoTextureSession>();
        var inAppMatch = PhotoTextureMatch.Failed("in the app");
        inAppSession.Match(default!, default, default, default, default).ReturnsForAnyArgs(inAppMatch);
        var inApp = Substitute.For<IPhotoTextureMatcher>();
        inApp.OpenPhoto(default!).ReturnsForAnyArgs(inAppSession);
        using var wall = RecognitionWallScope.Begin(h.WallId);
        using var session = new QueuedPhotoTextureMatcher(inApp, FakeRecognitionClaimant.Caller(f)).OpenQueued(Photo);

        var call = session.MatchAsync(CaptureScenario.TinyJpeg(1), null, 2.0, null, null, CancellationToken.None);
        await FakeRecognitionClaimant.QueuedAsync(f);
        f.Clock.Advance(f.Options.InteractiveClaimWait + TimeSpan.FromSeconds(1));

        Assert.Same(inAppMatch, await call);
        session.Dispose();
        inAppSession.Received(1).Dispose();
    }
}
