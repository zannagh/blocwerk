// <copyright file="BusyEta.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Web.State;
using Microsoft.Extensions.Options;

namespace Blocwerk.Web.HealthChecks;

/// <summary>
/// The body of <c>/health/busy-eta</c>: seconds until whatever holds the deploy gate expects to let go, 0 when nothing
/// reports one. The later of long background work's own ETA (<see cref="DeployBusyEtaBoard"/>) and the cutoff of the
/// editing leases that hold the gate right now (<see cref="EditDeployGatePolicy.SecondsUntilEditCutoff"/>).
/// </summary>
/// <remarks>
/// The deploy script keeps deferring while the gate is busy AND this is above zero, up to its own long safety cap. So
/// a present editor is waited for until the day's cutoff rather than for the script's short busy budget, while
/// background work without an ETA keeps exactly its old treatment.
/// </remarks>
public static class BusyEta
{
    /// <summary>The seconds, never negative.</summary>
    public static int SecondsLeft(
        DeployBusyEtaBoard board, EditActivityRegistry registry, IOptions<EditPresenceOptions> options, DateTimeOffset? nowOverride = null)
    {
        var now = nowOverride ?? DateTimeOffset.UtcNow;
        var edits = EditDeployGatePolicy.SecondsUntilEditCutoff(registry.Snapshot(now), now, options.Value);
        return Math.Max(board.SecondsLeft(now), edits);
    }
}
