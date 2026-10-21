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
        activity.RecordActivity(Now + TimeSpan.FromSeconds(7));

        var clock = Now + TimeSpan.FromSeconds(7);
        for (var tick = 0; tick < 50; tick++)
        {
            clock += EditActivityPolicy.HeartbeatInterval;
            activity.Touch(clock);
            if (tick % 6 == 0)
            {
                activity.Resume(clock);
                activity.RecordVisibility(visible: true, clock);
            }
        }

        var entry = Assert.Single(registry.Snapshot(clock));
        Assert.Equal(Now + TimeSpan.FromSeconds(7), entry.LastActivityUtc);
        Assert.False(EditDeployGatePolicy.HoldsGate(entry, clock, Defaults));
        Assert.Equal("presence", EditDeployGatePolicy.Remaining(entry, clock, Defaults).Clock);

        // The lease itself survives, so background hold writers still stay off the wall until it expires.
        Assert.True(registry.IsBusy(clock));
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

        var entry = Assert.Single(registry.Snapshot(input));
        Assert.True(EditDeployGatePolicy.HoldsGate(entry, input + Defaults.PresenceWindow - TimeSpan.FromSeconds(1), Defaults));
        Assert.False(EditDeployGatePolicy.HoldsGate(entry, input + Defaults.PresenceWindow, Defaults));
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

    /// <summary>However busy the user is, one lease cannot hold deploys back past the cap.</summary>
    [Fact]
    public void OneLeaseCannotHoldTheGatePastTheCap()
    {
        var registry = new EditActivityRegistry();
        var activity = new CircuitEditActivity(registry);
        activity.BeginWallEdit(Wall, userId: null, Now);

        var clock = Now;
        while (clock < Now + Defaults.MaxHold)
        {
            clock += TimeSpan.FromMinutes(2);
            activity.RecordActivity(clock);
        }

        var entry = Assert.Single(registry.Snapshot(clock));
        Assert.False(EditDeployGatePolicy.HoldsGate(entry, clock, Defaults));
        Assert.Equal("cap", EditDeployGatePolicy.Remaining(entry, clock, Defaults).Clock);
    }

    /// <summary>The window is configuration, not a constant.</summary>
    [Fact]
    public void ThePresenceWindowIsConfigurable()
    {
        var shortWindow = new EditPresenceOptions { PresenceWindow = TimeSpan.FromMinutes(1) };
        var entry = new EditActivityEntry(EditKind.WallEdit, Wall, null, Now, Now, Now);

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
        var check = new BusyHealthCheck(registry, Options.Create(new EditPresenceOptions()));
        var activity = new CircuitEditActivity(registry);
        activity.BeginWallEdit(Wall, userId: null, DateTimeOffset.UtcNow - TimeSpan.FromMinutes(20));
        activity.Touch(DateTimeOffset.UtcNow);

        var absent = await check.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);
        Assert.Equal(HealthStatus.Healthy, absent.Status);
        Assert.False((bool)absent.Data["busy"]);

        activity.RecordActivity(DateTimeOffset.UtcNow);
        var present = await check.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);
        Assert.Equal(HealthStatus.Degraded, present.Status);
        Assert.Equal(1, (int)present.Data["count"]);
        Assert.Equal((int)Defaults.PresenceWindow.TotalSeconds, (int)present.Data["presenceWindowSeconds"]);
    }
}
