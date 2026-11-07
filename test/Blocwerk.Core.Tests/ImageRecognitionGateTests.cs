// <copyright file="ImageRecognitionGateTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Detection.Gating;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The process-wide recognition gate: one slot by default, reentrant within a flow (enrichment opening an outline
/// session must not wait for itself), session leases hold their slot until disposed, and a wait past the timeout runs
/// without a slot instead of hanging.
/// </summary>
public class ImageRecognitionGateTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task OneSlot_SecondRunWaitsForTheFirst()
    {
        using var gate = Gate();
        var release = new TaskCompletionSource();
        var first = gate.RunAsync("first", async () => { await release.Task; return 1; });

        var secondStarted = false;
        var second = OtherFlow(() => gate.RunAsync("second", () => { secondStarted = true; return Task.FromResult(2); }));
        await Task.Delay(100);
        Assert.False(secondStarted);

        release.SetResult();
        Assert.Equal(1, await first.WaitAsync(Deadline));
        Assert.Equal(2, await second.WaitAsync(Deadline));
    }

    [Fact]
    public async Task NestedRunsAndSessions_InsideARun_DoNotWaitForThemselves()
    {
        using var gate = Gate();

        var result = await gate.RunAsync("enrich", async () =>
        {
            using var session = gate.Enter("outline-session");
            return await Task.Run(() => gate.RunAsync("detect", () => Task.FromResult(42)));
        }).WaitAsync(Deadline);

        Assert.Equal(42, result);
    }

    [Fact]
    public async Task SessionLease_HoldsTheSlotUntilDisposed()
    {
        using var gate = Gate();
        var outlines = new GatedHoldOutlineService(EnrichmentFakes.Outlines(), gate);
        var session = OtherFlowSync(() => outlines.OpenSession([1, 2, 3]));

        var detect = OtherFlow(() => gate.RunAsync("detect", () => Task.FromResult(7)));
        await Task.Delay(100);
        Assert.False(detect.IsCompleted);

        session.Dispose();
        Assert.Equal(7, await detect.WaitAsync(Deadline));
    }

    [Fact]
    public async Task FailedSessionOpen_GivesTheSlotBack()
    {
        using var gate = Gate();
        var outlines = new GatedHoldOutlineService(EnrichmentFakes.ThrowingOutlines(), gate);

        Assert.Throws<InvalidOperationException>(() => outlines.OpenSession([1, 2, 3]));

        Assert.Equal(7, await OtherFlow(() => gate.RunAsync("detect", () => Task.FromResult(7))).WaitAsync(Deadline));
    }

    [Fact]
    public async Task WaitPastTheTimeout_FailsAsBusy_InsteadOfRunningUngated()
    {
        using var gate = new ImageRecognitionGate(1, TimeSpan.FromMilliseconds(50), NullLogger.Instance);
        using var held = OtherFlowSync(() => gate.Enter("stuck"));
        var ran = false;

        var ex = await Assert.ThrowsAsync<ImageRecognitionUnavailableException>(
            () => OtherFlow(() => gate.RunAsync("detect", () => { ran = true; return Task.FromResult(3); })));

        Assert.Equal(ImageRecognitionUnavailableException.BusyMessage, ex.Message);
        Assert.False(ran);
    }

    [Fact]
    public async Task CancelledWait_Throws_AndTheFlowDoesNotCountAsHolding()
    {
        using var gate = Gate();
        using var held = OtherFlowSync(() => gate.Enter("stuck"));
        using var cts = new CancellationTokenSource(50);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => gate.EnterAsync("detect", cts.Token));

        held.Dispose();
        using var next = await gate.EnterAsync("detect").WaitAsync(Deadline);
    }

    [Fact]
    public async Task AwaitedSlot_CoversSyncRecognitionOnThePool()
    {
        using var gate = Gate();
        var probe = new GatedHoldPresenceProbe(Substitute.For<IHoldPresenceProbe>(), gate);

        // With one slot, a probe call that did not see the awaited slot as its own would wait for it until the deadline.
        var calls = await gate.RecogniseAsync("removal-check", () =>
        {
            probe.Score([1], [2], []);
            probe.Score([1], [2], []);
            return 2;
        }).WaitAsync(Deadline);

        Assert.Equal(2, calls);
    }

    [Fact]
    public void SwitchedOff_EveryGatedCallFails()
    {
        using var gate = new ImageRecognitionGate(1, TimeSpan.FromMinutes(1), NullLogger.Instance, enabled: false);
        var outlines = new GatedHoldOutlineService(EnrichmentFakes.Outlines(), gate);

        var ex = Assert.Throws<ImageRecognitionUnavailableException>(() => outlines.OpenSession([1, 2, 3]));

        Assert.Equal(ImageRecognitionUnavailableException.DisabledMessage, ex.Message);
    }

    [Fact]
    public void CarryPass_FallsBackToMatch_WhenTheInnerMatcherHasNone()
    {
        using var gate = Gate();
        var inner = Substitute.For<IHoldOverlapMatcher>();
        var matcher = new GatedHoldOverlapMatcher(inner, gate);

        matcher.MatchCarry([1], [], [2], [], HoldOverlapDirection.Right);

        inner.ReceivedWithAnyArgs(1).Match(default!, default!, default!, default!, default);
    }

    private static ImageRecognitionGate Gate() => new(1, TimeSpan.FromMinutes(1), NullLogger.Instance);

    /// <summary>Runs <paramref name="work"/> as another caller would: without this flow's gate lease.</summary>
    private static Task<T> OtherFlow<T>(Func<Task<T>> work)
    {
        using (ExecutionContext.SuppressFlow())
        {
            return Task.Run(work);
        }
    }

    private static T OtherFlowSync<T>(Func<T> work) => OtherFlow(() => Task.FromResult(work())).GetAwaiter().GetResult();
}
