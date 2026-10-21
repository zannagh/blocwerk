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
/// recently, and has the day's cutoff (<see cref="EditDeployCutoff"/>) passed.</para>
/// <para>Background work (<see cref="EditActivityPolicy.IsBackgroundWork"/>) has no person and no page, so it holds
/// the gate for as long as its lease lives (bounded by <see cref="EditActivityPolicy.MaintenanceMaxLifetime"/>).</para>
/// </remarks>
public static class EditDeployGatePolicy
{
    /// <summary>The clock that lets go of the gate first: heartbeat, presence or cutoff.</summary>
    public static (TimeSpan Remaining, string Clock) Remaining(EditActivityEntry entry, DateTimeOffset now, EditPresenceOptions options)
    {
        return Remaining(entry, now, options, EditDeployCutoff.Zone(options));
    }

    /// <summary>As <see cref="Remaining(EditActivityEntry, DateTimeOffset, EditPresenceOptions)"/>, with the zone resolved once.</summary>
    public static (TimeSpan Remaining, string Clock) Remaining(
        EditActivityEntry entry, DateTimeOffset now, EditPresenceOptions options, TimeZoneInfo zone)
    {
        if (EditActivityPolicy.IsBackgroundWork(entry.EditKind))
        {
            return (EditActivityPolicy.MaintenanceRemaining(entry, now), "maintenanceCap");
        }

        var best = (Remaining: EditActivityPolicy.HeartbeatRemaining(entry, now), Clock: "heartbeat");
        best = Earlier(best, options.PresenceWindow - (now - entry.LastActivityUtc), "presence");
        best = Earlier(best, EditDeployCutoff.NextAfter(entry.StartedUtc, zone, options.CutoffTimeOfDay) - now, "cutoff");

        // Deliberately no hidden clock: a locked phone between two attempts is the normal case, not an absence.
        return best;
    }

    /// <summary>Whether <paramref name="entry"/> (already known to be unexpired) holds the deploy gate.</summary>
    public static bool HoldsGate(EditActivityEntry entry, DateTimeOffset now, EditPresenceOptions options)
    {
        return Remaining(entry, now, options).Remaining > TimeSpan.Zero;
    }

    /// <summary>
    /// Seconds until the latest cutoff among the editing leases that hold the gate right now, or 0 when none does:
    /// the longest an edit can still hold a deploy back. Reported through <c>/health/busy-eta</c>, so the deploy script
    /// keeps waiting for a present editor instead of giving up after its short busy budget.
    /// </summary>
    public static int SecondsUntilEditCutoff(IEnumerable<EditActivityEntry> entries, DateTimeOffset now, EditPresenceOptions options)
    {
        var zone = EditDeployCutoff.Zone(options);
        var latest = entries
            .Where(e => !EditActivityPolicy.IsBackgroundWork(e.EditKind))
            .Where(e => Remaining(e, now, options, zone).Remaining > TimeSpan.Zero)
            .Select(e => EditDeployCutoff.NextAfter(e.StartedUtc, zone, options.CutoffTimeOfDay))
            .DefaultIfEmpty(now)
            .Max();
        return (int)Math.Ceiling((latest - now).TotalSeconds);
    }

    private static (TimeSpan Remaining, string Clock) Earlier((TimeSpan Remaining, string Clock) best, TimeSpan candidate, string clock)
    {
        var clamped = candidate < TimeSpan.Zero ? TimeSpan.Zero : candidate;
        return clamped < best.Remaining ? (clamped, clock) : best;
    }
}
