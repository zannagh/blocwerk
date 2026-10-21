// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Enums;
using Blocwerk.Core.Feed;
using Blocwerk.Core.Pulse;

namespace Blocwerk.Core.Tests.Pulse;

/// <summary>
/// The in-process pulse channel (PR 1.7): audience per delivery, the per-job throttle with its latest-wins trailing pulse,
/// ended pulses, and subscriptions that go or throw. A sentinel pulse of another job proves the loop drained.
/// </summary>
public sealed class WallPulseHubTests : IDisposable
{
    private static readonly Guid WallId = Guid.NewGuid();
    private readonly PulseTestClock clock = new();
    private readonly WallPulseHub hub;

    public WallPulseHubTests() => hub = new WallPulseHub(clock: clock);

    public void Dispose() => hub.Dispose();

    [Fact]
    public async Task ASettersPulse_ReachesSettersOnly_NotMembersKiosksOrCircuitsWithoutAccess()
    {
        var setter = Inbox(FeedViewer.Setter(Guid.NewGuid()));
        var member = Inbox(FeedViewer.Member(Guid.NewGuid()));
        var kiosk = Inbox(FeedViewer.Kiosk());
        var none = Inbox(null);

        hub.Publish(new WallPulse(WallId, PulseTopics.Capture, Guid.NewGuid(), FeedAudience.Setters, 0.5));
        await DrainAsync();

        Assert.Single(setter.Received);
        Assert.Empty(member.Received);
        Assert.Empty(kiosk.Received);
        Assert.Empty(none.Received);
    }

    [Fact]
    public async Task AWallPulse_ReachesAKiosk_AndAUserPulseReachesNobody()
    {
        var kiosk = Inbox(FeedViewer.Kiosk());
        var user = Guid.NewGuid();
        var member = Inbox(FeedViewer.Member(user));

        hub.Publish(new WallPulse(WallId, PulseTopics.Refresh, Guid.NewGuid(), FeedAudience.Wall));
        hub.Publish(new WallPulse(WallId, PulseTopics.Refresh, Guid.NewGuid(), FeedAudience.User));
        await DrainAsync();

        Assert.Single(kiosk.Received);
        Assert.Single(member.Received); // the Wall one: a pulse never carries a user, so User reaches no one
        Assert.All(member.Received, p => Assert.Equal(FeedAudience.Wall, p.Audience));
    }

    [Fact]
    public async Task PulsesInsideTheInterval_CollapseIntoTheLatest_SentWhenItEnds()
    {
        var setter = Inbox(FeedViewer.Setter(Guid.NewGuid()));
        var job = Guid.NewGuid();

        foreach (var progress in new[] { 0.1, 0.2, 0.3, 0.4 })
        {
            hub.Publish(Job(job, progress));
        }

        await DrainAsync();
        Assert.Equal([0.1], setter.Progress());

        clock.Advance(hub.Interval);
        await DrainAsync();
        Assert.Equal([0.1, 0.4], setter.Progress());

        // A quiet interval later the next one goes at once again.
        clock.Advance(hub.Interval);
        hub.Publish(Job(job, 0.5));
        await DrainAsync();
        Assert.Equal([0.1, 0.4, 0.5], setter.Progress());
    }

    [Fact]
    public async Task TheThrottle_IsPerJob_SoTwoJobsOfAWallDoNotHoldEachOtherBack()
    {
        var setter = Inbox(FeedViewer.Setter(Guid.NewGuid()));

        hub.Publish(Job(Guid.NewGuid(), 0.1));
        hub.Publish(Job(Guid.NewGuid(), 0.2));
        hub.Publish(new WallPulse(WallId, PulseTopics.Enhancement, Guid.NewGuid(), Progress: 0.3));
        await DrainAsync();

        Assert.Equal([0.1, 0.2, 0.3], setter.Progress());
    }

    [Fact]
    public async Task AnEndedPulse_GoesAtOnce_DropsTheHeldOne_AndFreesTheSlot()
    {
        var setter = Inbox(FeedViewer.Setter(Guid.NewGuid()));
        var job = Guid.NewGuid();

        hub.Publish(Job(job, 0.1));
        hub.Publish(Job(job, 0.9)); // held
        hub.Publish(Job(job, 1, ended: true));
        await DrainAsync();
        Assert.Equal([0.1, 1.0], setter.Progress());

        clock.Advance(hub.Interval);
        await DrainAsync();
        Assert.Equal([0.1, 1.0], setter.Progress()); // the held 0.9 never arrives after the end
        Assert.Equal(0, hub.SlotCount);
    }

    [Fact]
    public async Task AViewerReadAtDelivery_StopsPulsesTheMomentItIsDemoted()
    {
        var viewer = FeedViewer.Setter(Guid.NewGuid());
        var received = new List<WallPulse>();
        using var subscription = hub.Subscribe(WallId, () => viewer, p => Add(received, p));

        hub.Publish(Job(Guid.NewGuid(), 0.1));
        await DrainAsync();
        viewer = FeedViewer.Member(Guid.NewGuid());
        hub.Publish(Job(Guid.NewGuid(), 0.2));
        await DrainAsync();

        Assert.Equal([0.1], Snapshot(received).Select(p => p.Progress ?? double.NaN));
    }

    [Fact]
    public async Task ADisposedSubscription_HearsNothing_AndAThrowingOneDoesNotStopTheOthers()
    {
        var gone = Inbox(FeedViewer.Setter(Guid.NewGuid()));
        gone.Subscription.Dispose();
        gone.Subscription.Dispose(); // twice is harmless
        using var throwing = hub.Subscribe(WallId, () => FeedViewer.Setter(Guid.NewGuid()), _ => throw new InvalidOperationException("boom"));
        var after = Inbox(FeedViewer.Setter(Guid.NewGuid()));

        hub.Publish(Job(Guid.NewGuid(), 0.5));
        await DrainAsync();

        Assert.Empty(gone.Received);
        Assert.Single(after.Received);
    }

    [Fact]
    public async Task AHandlerThatDisposesItsOwnSubscription_GetsNoLaterPulse()
    {
        var received = new List<WallPulse>();
        IDisposable? subscription = null;
        subscription = hub.Subscribe(WallId, () => FeedViewer.Setter(Guid.NewGuid()), p =>
        {
            Add(received, p);
            subscription!.Dispose();
        });

        hub.Publish(Job(Guid.NewGuid(), 0.1));
        hub.Publish(Job(Guid.NewGuid(), 0.2));
        await DrainAsync();

        Assert.Single(Snapshot(received));
    }

    [Fact]
    public void PublishingAfterTheHubWent_NeverThrows()
    {
        hub.Dispose();
        hub.Publish(Job(Guid.NewGuid(), 0.5));
        hub.Publish(new WallPulse(Guid.Empty, PulseTopics.Capture, Guid.NewGuid()));
        Assert.Equal(0, hub.SlotCount);
    }

    private static WallPulse Job(Guid job, double progress, bool ended = false) =>
        new(WallId, PulseTopics.GpuJob, job, FeedAudience.Setters, progress, ended);

    private static void Add(List<WallPulse> list, WallPulse pulse)
    {
        if (pulse.Topic == PulseInbox.Sentinel)
        {
            return;
        }

        lock (list)
        {
            list.Add(pulse);
        }
    }

    private static List<WallPulse> Snapshot(List<WallPulse> list)
    {
        lock (list)
        {
            return [.. list];
        }
    }

    private PulseInbox Inbox(FeedViewer? viewer)
    {
        var inbox = new PulseInbox();
        inbox.Subscription = hub.Subscribe(WallId, () => viewer, inbox.Add);
        return inbox;
    }

    // Single loop, publish order: once a sentinel of its own job arrives, everything published before it was handed out.
    private async Task DrainAsync()
    {
        var sentinel = Guid.NewGuid();
        var seen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var watch = hub.Subscribe(WallId, () => FeedViewer.Setter(Guid.NewGuid()), p =>
        {
            if (p.JobId == sentinel)
            {
                seen.TrySetResult();
            }
        });
        hub.Publish(new WallPulse(WallId, PulseInbox.Sentinel, sentinel, Ended: true));
        await seen.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }
}
