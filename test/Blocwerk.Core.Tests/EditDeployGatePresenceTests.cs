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

    /// <summary>A hidden page lets go after the grace; showing it again is not input, but input clears the mark.</summary>
    [Fact]
    public void AHiddenEditorLetsGoAfterTheGrace()
    {
        var registry = new EditActivityRegistry();
        var activity = new CircuitEditActivity(registry);
        activity.BeginWallEdit(Wall, userId: null, Now);
        Assert.Equal(1, activity.RecordVisibility(visible: false, Now));

        var entry = Assert.Single(registry.Snapshot(Now));
        Assert.True(EditDeployGatePolicy.HoldsGate(entry, Now + Defaults.HiddenGrace - TimeSpan.FromSeconds(1), Defaults));
        Assert.False(EditDeployGatePolicy.HoldsGate(entry, Now + Defaults.HiddenGrace, Defaults));
        Assert.Equal("hidden", EditDeployGatePolicy.Remaining(entry, Now + Defaults.HiddenGrace, Defaults).Clock);

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

    /// <summary>A second hidden report does not restart the grace.</summary>
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
