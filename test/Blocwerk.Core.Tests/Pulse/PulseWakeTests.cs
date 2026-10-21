// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Contracts.Feed;
using Blocwerk.Contracts.Feed.Payloads;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Feed;
using Blocwerk.Core.Pulse;
using Blocwerk.Web.State.Live;

namespace Blocwerk.Core.Tests.Pulse;

/// <summary>
/// A circuit's job pulses (PR 1.7) over a real feed subscription: the viewer the circuit's feed decided (setter, member,
/// kiosk), a demotion that stops them on the spot, the wake-up's coalescing, and a page that went.
/// </summary>
public sealed class PulseWakeTests : IAsyncLifetime
{
    private static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(300);
    private readonly CircuitCacheFixture f = new();
    private readonly WallPulseHub hub = new(interval: TimeSpan.Zero);

    public Task InitializeAsync() => f.StartAsync();

    public async Task DisposeAsync()
    {
        hub.Dispose();
        await f.DisposeAsync();
    }

    [Fact]
    public async Task ASetterCircuit_WakesOnItsTopics_AndNotOnOthers()
    {
        using var wake = await PulseWake.OpenAsync(f.Cache, hub, f.WallId, [PulseTopics.Capture]);

        hub.Publish(Pulse(PulseTopics.Refresh));
        Assert.False(await wake.WaitAsync(Quiet, default));
        hub.Publish(Pulse(PulseTopics.Capture));
        Assert.True(await wake.WaitAsync(TimeSpan.FromSeconds(10), default));
        Assert.Equal(1, wake.Received);
    }

    [Theory]
    [InlineData("member")]
    [InlineData("kiosk")]
    public async Task AMemberOrKioskCircuit_NeverHearsASettersPulse(string who)
    {
        f.Fake.Viewer = who == "kiosk" ? FeedViewer.Kiosk() : FeedViewer.Member(f.Harness.Owner.Id);
        using var wake = await PulseWake.OpenAsync(f.Cache, hub, f.WallId, [PulseTopics.Capture]);

        hub.Publish(Pulse(PulseTopics.Capture));
        Assert.False(await wake.WaitAsync(Quiet, default));
        Assert.Equal(0, wake.Received);
    }

    [Fact]
    public async Task ADemotion_StopsThePulses_BeforeTheResubscribeLands()
    {
        GatedWallFeedHub? gated = null;
        await using var demoted = new CircuitCacheFixture(wrapHub: h => gated = new GatedWallFeedHub(h));
        await demoted.StartAsync();
        using var wake = await PulseWake.OpenAsync(demoted.Cache, hub, demoted.WallId, [PulseTopics.Capture]);
        demoted.Fake.Viewer = FeedViewer.Member(demoted.Harness.Owner.Id);
        gated!.Close(); // the resubscribe as a member waits here

        await demoted.DeliverAsync(demoted.Row(
            FeedKinds.AccessRevoked, demoted.WallId, new MemberPayload(nameof(WallRole.Member)), FeedAudience.User, user: demoted.Harness.Owner.Id));
        await CircuitCacheFixture.WaitUntilAsync(() => gated.Waiting == 1);
        hub.Publish(new WallPulse(demoted.WallId, PulseTopics.Capture, Guid.NewGuid()));

        Assert.False(await wake.WaitAsync(Quiet, default));
        gated.Open();
    }

    [Fact]
    public async Task PulsesInsideTheGap_JoinOneWakeUp()
    {
        var gap = TimeSpan.FromMilliseconds(400);
        using var wake = await PulseWake.OpenAsync(f.Cache, hub, f.WallId, [PulseTopics.Capture], gap);
        hub.Publish(Pulse(PulseTopics.Capture));
        Assert.True(await wake.WaitAsync(TimeSpan.FromSeconds(10), default));

        var started = DateTimeOffset.UtcNow;
        hub.Publish(Pulse(PulseTopics.Capture));
        hub.Publish(Pulse(PulseTopics.Capture));
        Assert.True(await wake.WaitAsync(TimeSpan.FromSeconds(10), default));
        Assert.True(DateTimeOffset.UtcNow - started >= gap - TimeSpan.FromMilliseconds(50));
        Assert.False(await wake.WaitAsync(Quiet, default)); // both joined the one wake-up
    }

    [Fact]
    public async Task ADisposedWake_IgnoresTheLatePulse_AndLeavesTheHub()
    {
        var wake = await PulseWake.OpenAsync(f.Cache, hub, f.WallId, [PulseTopics.Capture]);
        wake.Dispose();
        wake.Dispose();

        hub.Publish(Pulse(PulseTopics.Capture));
        await Task.Delay(Quiet);
        Assert.Equal(0, wake.Received);
    }

    [Fact]
    public async Task APulseLoop_RefreshesOnAPulse_AndOnANudge_AndStopsWhenDisposed()
    {
        var refreshes = 0;
        var loop = PulseLoop.Start(
            f.Cache, hub, f.WallId, [PulseTopics.Enhancement], () => false, work => work(),
            () =>
            {
                Interlocked.Increment(ref refreshes);
                return Task.CompletedTask;
            },
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        await CircuitCacheFixture.WaitUntilAsync(() => loop.Wake is not null);

        hub.Publish(Pulse(PulseTopics.Enhancement));
        await CircuitCacheFixture.WaitUntilAsync(() => Volatile.Read(ref refreshes) == 1);
        loop.Nudge();
        await CircuitCacheFixture.WaitUntilAsync(() => Volatile.Read(ref refreshes) == 2);

        loop.Dispose();
        hub.Publish(Pulse(PulseTopics.Enhancement));
        await Task.Delay(TimeSpan.FromSeconds(1.5));
        Assert.Equal(2, Volatile.Read(ref refreshes));
    }

    private WallPulse Pulse(string topic) => new(f.WallId, topic, Guid.NewGuid(), FeedAudience.Setters, 0.5);
}
