// <copyright file="SessionHoldOutlineBatchTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Detection.Gating;
using Blocwerk.Core.Detection.Outlines;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The in-process outline batch: one session per call, results in seed order exactly as the session gives them,
/// joint outlining where the session offers it, and the whole call inside one recognition-gate slot.
/// </summary>
public class SessionHoldOutlineBatchTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    private static readonly HoldSeed[] Seeds = [new(0.2, 0.3, 0.02), new(0.5, 0.7, 0.03), new(0.8, 0.1, 0.01)];

    [Fact]
    public async Task Separately_GivesEachSeedsSessionOutline_InSeedOrder()
    {
        var session = PlainSession();
        var service = Service(session);

        var batch = await new SessionHoldOutlineBatch(service, null).OutlineAsync([1, 2, 3], Seeds, HoldOutlineBatchOptions.Separately);

        Assert.Equal(Seeds.Select(session.Outline), batch.Outlines);
        Assert.Equal((640, 480), (batch.ImageWidth, batch.ImageHeight));
        service.Received(1).OpenSession(Arg.Any<byte[]>());
        session.Received(1).Dispose();
    }

    [Fact]
    public async Task Jointly_UsesTheSessionsJointOutlining()
    {
        var joint = Seeds.Select(Result).ToList();
        var session = Substitute.For<IHoldBatchOutlineSession>();
        session.OutlineAll(Arg.Any<IReadOnlyList<HoldSeed>>()).Returns(joint);

        var batch = await new SessionHoldOutlineBatch(Service(session), null).OutlineAsync([1], Seeds, HoldOutlineBatchOptions.Jointly);

        Assert.Same(joint, batch.Outlines);
        session.DidNotReceive().Outline(Arg.Any<HoldSeed>());
    }

    [Fact]
    public async Task Jointly_OnASessionWithoutJointOutlining_OutlinesOneByOne()
    {
        var session = PlainSession();

        var batch = await new SessionHoldOutlineBatch(Service(session), null).OutlineAsync([1], Seeds, HoldOutlineBatchOptions.Jointly);

        Assert.Equal(Seeds.Select(session.Outline), batch.Outlines);
    }

    [Fact]
    public async Task FailingOutline_StillDisposesTheSession()
    {
        var session = Substitute.For<IHoldOutlineSession>();
        session.Outline(Arg.Any<HoldSeed>()).Returns(_ => throw new InvalidOperationException("outliner exploded"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => new SessionHoldOutlineBatch(Service(session), null).OutlineAsync([1], Seeds, HoldOutlineBatchOptions.Separately));

        session.Received(1).Dispose();
    }

    [Fact]
    public async Task InsideAHeldSlot_RunsWithoutWaitingForItself()
    {
        using var gate = new ImageRecognitionGate(1, TimeSpan.FromMinutes(1), NullLogger.Instance);
        var outlines = new GatedHoldOutlineService(Service(PlainSession()), gate);
        using var lease = await gate.EnterAsync("enrich");

        var batch = await new SessionHoldOutlineBatch(outlines, gate)
            .OutlineAsync([1], Seeds, HoldOutlineBatchOptions.Separately)
            .WaitAsync(Deadline);

        Assert.Equal(Seeds.Length, batch.Outlines.Count);
    }

    [Fact]
    public async Task SwitchedOffGate_OutlinesNothing()
    {
        using var gate = new ImageRecognitionGate(1, TimeSpan.FromMinutes(1), NullLogger.Instance, enabled: false);
        var service = Service(PlainSession());

        await Assert.ThrowsAsync<ImageRecognitionUnavailableException>(
            () => new SessionHoldOutlineBatch(service, gate).OutlineAsync([1], Seeds, HoldOutlineBatchOptions.Separately));

        service.DidNotReceive().OpenSession(Arg.Any<byte[]>());
    }

    private static IHoldOutlineService Service(IHoldOutlineSession session)
    {
        var service = Substitute.For<IHoldOutlineService>();
        service.OpenSession(Arg.Any<byte[]>()).Returns(session);
        return service;
    }

    // One fixed result per seed, so "the same outline" is reference equality.
    private static IHoldOutlineSession PlainSession()
    {
        var results = Seeds.ToDictionary(s => s, Result);
        var session = Substitute.For<IHoldOutlineSession>();
        session.ImageWidth.Returns(640);
        session.ImageHeight.Returns(480);
        session.Outline(Arg.Any<HoldSeed>()).Returns(ci => results[ci.Arg<HoldSeed>()]);
        return session;
    }

    private static HoldOutlineResult Result(HoldSeed seed) => new(
        [], seed.X, seed.Y, null, 0, default, 0.1, HoldOutlineMethod.CircleFallback, new HoldFingerprint { AreaPx = seed.Radius });
}
