// <copyright file="EditDeployGatePolicy.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Web.State;

/// <summary>
/// Whether a live <see cref="EditActivityEntry"/> holds the deploy gate, and which clock lets go of it first.
/// </summary>
/// <remarks>
/// <para><b>Why the gate is narrower than the lease.</b> The heartbeat proves the socket is up, and a socket can be
/// up with nobody there: on 2026-10-21 a MacBook asleep with the wall editor open reconnected its circuit on every
/// dark wake, and the circuit traffic of each reconnect was counted as user activity, so the gate stayed closed for
/// as long as the laptop slept. The gate now asks the question it actually cares about: did a person touch the editor
/// recently, and is the page still in front of them.</para>
/// <para>Background work (<see cref="EditActivityPolicy.IsBackgroundWork"/>) has no person and no page, so it holds
/// the gate for as long as its lease lives (bounded by <see cref="EditActivityPolicy.MaintenanceMaxLifetime"/>).</para>
/// </remarks>
public static class EditDeployGatePolicy
{
    /// <summary>The clock that lets go of the gate first: heartbeat, presence, hidden or cap.</summary>
    public static (TimeSpan Remaining, string Clock) Remaining(EditActivityEntry entry, DateTimeOffset now, EditPresenceOptions options)
    {
        if (EditActivityPolicy.IsBackgroundWork(entry.EditKind))
        {
            return (EditActivityPolicy.MaintenanceRemaining(entry, now), "maintenanceCap");
        }

        var best = (Remaining: EditActivityPolicy.HeartbeatRemaining(entry, now), Clock: "heartbeat");
        best = Earlier(best, options.PresenceWindow - (now - entry.LastActivityUtc), "presence");
        best = Earlier(best, options.MaxHold - (now - entry.StartedUtc), "cap");
        if (entry.HiddenSinceUtc is { } hidden)
        {
            best = Earlier(best, options.HiddenGrace - (now - hidden), "hidden");
        }

        return best;
    }

    /// <summary>Whether <paramref name="entry"/> (already known to be unexpired) holds the deploy gate.</summary>
    public static bool HoldsGate(EditActivityEntry entry, DateTimeOffset now, EditPresenceOptions options)
    {
        return Remaining(entry, now, options).Remaining > TimeSpan.Zero;
    }

    private static (TimeSpan Remaining, string Clock) Earlier((TimeSpan Remaining, string Clock) best, TimeSpan candidate, string clock)
    {
        var clamped = candidate < TimeSpan.Zero ? TimeSpan.Zero : candidate;
        return clamped < best.Remaining ? (clamped, clock) : best;
    }
}
