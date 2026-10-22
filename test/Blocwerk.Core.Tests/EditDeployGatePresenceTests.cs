// <copyright file="EditDeployGatePresenceTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Web.HealthChecks;
using Blocwerk.Web.State;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Xunit;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The deploy gate waits for people, not sockets. The 2026-10-21 incident: a MacBook asleep with the wall editor
/// open reconnected its circuit on every dark wake, the reconnect traffic was counted as user activity, and
/// autodeploy deferred for as long as the laptop slept.
/// </summary>
public class EditDeployGatePresenceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 17, 15, 18, TimeSpan.Zero);
    private static readonly Guid Wall = Guid.NewGuid();
    private static readonly EditPresenceOptions Defaults = new();

    /// <summary>The incident, replayed: heartbeats and dark-wake reconnects for half an hour, no input.</summary>
    [Fact]
    public void ASleepingClientThatKeepsReconnectingLetsGoOfTheGate()
    {
        var registry = new EditActivityRegistry();
        var activity = new CircuitEditActivity(registry);
        activity.BeginWallEdit(Wall, userId: null, Now);
        var input = Now + TimeSpan.FromSeconds(7);
        activity.RecordActivity(input);

        var clock = input;
        for (var tick = 0; tick < 58; tick++)
        {
            clock += EditActivityPolicy.HeartbeatInterval;
            activity.Touch(clock);
            if (tick % 6 == 0)
            {
                activity.Resume(clock);
                activity.RecordVisibility(visible: true, clock);
            }
        }

        // 29 minutes of reconnect traffic did not move presence at all...
        var entry = Assert.Single(registry.Snapshot(clock));
        Assert.Equal(input, entry.LastActivityUtc);
        Assert.Equal(TimeSpan.FromMinutes(1), EditDeployGatePolicy.Remaining(entry, clock, Defaults).Remaining);

        // ...so the gate lets go on the presence clock, and a shorter configured window lets go sooner.
        Assert.False(EditDeployGatePolicy.HoldsGate(entry, input + Defaults.PresenceWindow, Defaults));
        Assert.False(EditDeployGatePolicy.HoldsGate(entry, clock, new EditPresenceOptions { PresenceWindow = TimeSpan.FromMinutes(10) }));
        Assert.Equal("presence", EditDeployGatePolicy.Remaining(entry, input + Defaults.PresenceWindow, Defaults).Clock);
    }

    /// <summary>Somebody at the editor keeps it, and loses it exactly one presence window after their last input.</summary>
    [Fact]
    public void RecentInputHoldsTheGateForThePresenceWindow()
    {
        var registry = new EditActivityRegistry();
        var activity = new CircuitEditActivity(registry);
        activity.BeginBoulderCreate(Wall, userId: null, Now);
        var input = Now + TimeSpan.FromMinutes(4);
        activity.RecordActivity(input);

        // The heartbeat keeps the socket side alive meanwhile; only the presence clock is under test.
        var end = input + Defaults.PresenceWindow;
        var entry = Assert.Single(registry.Snapshot(input)) with { LastSeenUtc = end - TimeSpan.FromSeconds(1) };
        Assert.True(EditDeployGatePolicy.HoldsGate(entry, end - TimeSpan.FromSeconds(1), Defaults));
        Assert.False(EditDeployGatePolicy.HoldsGate(entry, end, Defaults));
    }

    /// <summary>
    /// A locked phone between two attempts keeps the gate: the circuit is retained for exactly that, with the unsaved
    /// boulder in it. The hidden mark is diagnostics only; input clears it, showing the page again clears it too.
    /// </summary>
    [Fact]
    public void AHiddenEditorStillHoldsTheGateUntilThePresenceWindow()
    {
        var registry = new EditActivityRegistry();
        var activity = new CircuitEditActivity(registry);
        activity.BeginWallEdit(Wall, userId: null, Now);
        Assert.Equal(1, activity.RecordVisibility(visible: false, Now));

        var locked = Now + EditActivityPolicy.DisconnectedCircuitRetention;
        var entry = Assert.Single(registry.Snapshot(Now)) with { LastSeenUtc = locked };
        Assert.Equal(Now, entry.HiddenSinceUtc);
        Assert.True(entry.PresenceReported);
        Assert.True(EditDeployGatePolicy.HoldsGate(entry, locked, Defaults));
        Assert.NotEqual("hidden", EditDeployGatePolicy.Remaining(entry, locked, Defaults).Clock);

        var shown = Now + TimeSpan.FromMinutes(3);
        activity.RecordVisibility(visible: true, shown);
        entry = Assert.Single(registry.Snapshot(shown));
        Assert.Null(entry.HiddenSinceUtc);
        Assert.Equal(Now, entry.LastActivityUtc);

        activity.RecordVisibility(visible: false, shown);
        var back = shown + TimeSpan.FromMinutes(5);
        activity.RecordActivity(back);
        entry = Assert.Single(registry.Snapshot(back));
        Assert.Null(entry.HiddenSinceUtc);
        Assert.True(EditDeployGatePolicy.HoldsGate(entry, back, Defaults));
    }

    /// <summary>The cutoff never drops a present editor before the coming midnight, whenever in the day they started.</summary>
    [Theory]
    [InlineData(0, 1)]
    [InlineData(6, 0)]
    [InlineData(12, 30)]
    [InlineData(21, 59)]
    public void APresentEditorHoldsUntilTheComingMidnight(int startHourUtc, int startMinute)
    {
        var started = new DateTimeOffset(2026, 7, 1, startHourUtc, startMinute, 0, TimeSpan.Zero);
        var midnight = EditDeployCutoff.NextAfter(started, EditDeployCutoff.Zone(Defaults), TimeSpan.Zero);
        Assert.Equal(0, TimeZoneInfo.ConvertTime(midnight, EditDeployCutoff.Zone(Defaults)).TimeOfDay.Ticks);
        Assert.InRange(midnight - started, TimeSpan.FromTicks(1), TimeSpan.FromDays(1));

        for (var at = started; at < midnight; at += TimeSpan.FromMinutes(7))
        {
            var present = new EditActivityEntry(EditKind.BoulderCreate, Wall, null, started, at, at);
            Assert.True(EditDeployGatePolicy.HoldsGate(present, at, Defaults), $"released at {at:o}");
        }

        var atMidnight = new EditActivityEntry(EditKind.BoulderCreate, Wall, null, started, midnight, midnight);
        Assert.False(EditDeployGatePolicy.HoldsGate(atMidnight, midnight, Defaults));
    }

    /// <summary>Only a report from the page counts as "presence reported"; a heartbeat or reconnect does not.</summary>
    [Fact]
    public void ALeaseWithoutAnyPresenceReportIsFlaggedOnceAfterAFewMinutes()
    {
        var registry = new EditActivityRegistry();
        var activity = new CircuitEditActivity(registry);
        activity.BeginBoulderCreate(Wall, userId: null, Now);
        activity.Touch(Now + TimeSpan.FromMinutes(1));
        activity.Resume(Now + TimeSpan.FromMinutes(2));

        var within = EditActivityPolicy.PresenceReportExpectedWithin;
        Assert.Empty(activity.TakeSilentLeases(within, Now + within - TimeSpan.FromSeconds(1)));
        var silent = Assert.Single(activity.TakeSilentLeases(within, Now + within));
        Assert.False(silent.PresenceReported);
        Assert.Empty(activity.TakeSilentLeases(within, Now + within + TimeSpan.FromMinutes(1)));

        var reported = new CircuitEditActivity(registry);
        reported.BeginWallEdit(Wall, userId: null, Now);
        reported.RecordVisibility(visible: true, Now + TimeSpan.FromSeconds(2));
        Assert.Empty(reported.TakeSilentLeases(within, Now + within));
    }

    /// <summary>A torn-down circuit hands out no new interop reference.</summary>
    [Fact]
    public void NoPresenceReferenceAfterTheCircuitIsGone()
    {
        var activity = new CircuitEditActivity(new EditActivityRegistry());
        Assert.NotNull(activity.PresenceReference);
        activity.Dispose();
        Assert.Null(activity.PresenceReference);
    }

    /// <summary>A second hidden report keeps the first timestamp (how long it has really been out of sight).</summary>
    [Fact]
    public void RepeatedHiddenReportsKeepTheFirstTimestamp()
    {
        var registry = new EditActivityRegistry();
        var activity = new CircuitEditActivity(registry);
        activity.BeginWallEdit(Wall, userId: null, Now);
        activity.RecordVisibility(visible: false, Now);
        activity.RecordVisibility(visible: false, Now + TimeSpan.FromMinutes(1));

        Assert.Equal(Now, Assert.Single(registry.Snapshot(Now + TimeSpan.FromMinutes(1))).HiddenSinceUtc);
    }

    /// <summary>A hidden page survives a lease restore, so a reconnect cannot clear it.</summary>
    [Fact]
    public void AReconnectKeepsTheHiddenMark()
    {
        var registry = new EditActivityRegistry();
        var activity = new CircuitEditActivity(registry);
        activity.BeginWallEdit(Wall, userId: null, Now);
        activity.RecordVisibility(visible: false, Now);

        var late = Now + EditActivityPolicy.LeaseTimeToLive + TimeSpan.FromSeconds(10);
        Assert.Equal(1, activity.Resume(late));
        Assert.Equal(Now, Assert.Single(registry.Snapshot(late)).HiddenSinceUtc);
    }

    /// <summary>However busy the user is, no lease holds the gate past the first midnight (Berlin) after it started.</summary>
    [Fact]
    public void OneLeaseCannotHoldTheGatePastTheCutoff()
    {
        var registry = new EditActivityRegistry();
        var activity = new CircuitEditActivity(registry);
        activity.BeginWallEdit(Wall, userId: null, Now);

        // 17:15 UTC on 2026-10-21 is 19:15 CEST, so the cutoff is 22:00 UTC.
        var cutoff = new DateTimeOffset(2026, 10, 21, 22, 0, 0, TimeSpan.Zero);
        var clock = Now;
        while (clock < cutoff)
        {
            clock += TimeSpan.FromMinutes(2);
            activity.RecordActivity(clock);
        }

        var entry = Assert.Single(registry.Snapshot(clock));
        Assert.True(EditDeployGatePolicy.HoldsGate(entry, cutoff - TimeSpan.FromSeconds(1), Defaults));
        Assert.False(EditDeployGatePolicy.HoldsGate(entry, cutoff, Defaults));
        Assert.Equal("cutoff", EditDeployGatePolicy.Remaining(entry, cutoff, Defaults).Clock);
    }

    /// <summary>The window is configuration, not a constant.</summary>
    [Fact]
    public void ThePresenceWindowIsConfigurable()
    {
        var shortWindow = new EditPresenceOptions { PresenceWindow = TimeSpan.FromMinutes(1) };
        var entry = new EditActivityEntry(EditKind.WallEdit, Wall, null, Now, Now, Now);

        Assert.Equal(TimeSpan.FromMinutes(30), Defaults.PresenceWindow);
        Assert.True(EditDeployGatePolicy.HoldsGate(entry, Now + TimeSpan.FromMinutes(5), Defaults));
        Assert.False(EditDeployGatePolicy.HoldsGate(entry, Now + TimeSpan.FromMinutes(5), shortWindow));
    }

    /// <summary>Background work has no user to be present, so it holds the gate for its whole (capped) life.</summary>
    [Fact]
    public void BackgroundWorkIsNotSubjectToPresence()
    {
        var entry = new EditActivityEntry(EditKind.Maintenance, null, null, Now, Now, Now);
        Assert.True(EditDeployGatePolicy.HoldsGate(entry, Now + TimeSpan.FromHours(5), Defaults));
    }

    /// <summary>End to end through the health check autodeploy polls: idle with an absent user, busy with a present one.</summary>
    [Fact]
    public async Task TheHealthCheckOnlyReportsBusyForAPresentUser()
    {
        var registry = new EditActivityRegistry();
        var started = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(20);
        var options = WithCutoffFarFrom(started);
        options.PresenceWindow = TimeSpan.FromMinutes(10);
        var check = new BusyHealthCheck(registry, Options.Create(options));
        var activity = new CircuitEditActivity(registry);
        activity.BeginWallEdit(Wall, userId: null, started);
        activity.Touch(DateTimeOffset.UtcNow);

        var absent = await check.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);
        Assert.Equal(HealthStatus.Healthy, absent.Status);
        Assert.False((bool)absent.Data["busy"]);

        activity.RecordActivity(DateTimeOffset.UtcNow);
        var present = await check.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);
        Assert.Equal(HealthStatus.Degraded, present.Status);
        Assert.Equal(1, (int)present.Data["count"]);
        Assert.Equal((int)options.PresenceWindow.TotalSeconds, (int)present.Data["presenceWindowSeconds"]);
    }

    /// <summary>
    /// Options whose cutoff lies about a day after <paramref name="started"/>, for tests that run on the real clock:
    /// with the Berlin-midnight default they would fail when run just after midnight.
    /// </summary>
    internal static EditPresenceOptions WithCutoffFarFrom(DateTimeOffset started) => new()
    {
        CutoffTimeZone = "UTC",
        CutoffTimeOfDay = TimeSpan.FromMinutes(Math.Floor((started.UtcDateTime.TimeOfDay - TimeSpan.FromMinutes(1)).TotalMinutes + 1440) % 1440),
    };
}
