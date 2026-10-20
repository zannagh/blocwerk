// <copyright file="DeployBusyEtaBoard.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Collections.Concurrent;
using Blocwerk.Core.Abstractions;

namespace Blocwerk.Web.State;

/// <summary>
/// <see cref="IDeployBusyEta"/> for the web host: the expected ends of long background work, read by
/// <c>/health/busy-eta</c> (seconds until the latest one, 0 when none). An ETA is capped at
/// <see cref="EditActivityPolicy.MaintenanceMaxLifetime"/>, so a stuck reporter can never hold deploys longer than the
/// busy lease itself would.
/// </summary>
public sealed class DeployBusyEtaBoard : IDeployBusyEta
{
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> entries = new();

    /// <inheritdoc/>
    public void Set(Guid key, DateTimeOffset? until)
    {
        if (until is { } end)
        {
            var cap = DateTimeOffset.UtcNow + EditActivityPolicy.MaintenanceMaxLifetime;
            entries[key] = end < cap ? end : cap;
        }
        else
        {
            entries.TryRemove(key, out _);
        }
    }

    /// <summary>Seconds until the latest expected end still ahead, or 0.</summary>
    /// <param name="nowOverride">The clock, for tests.</param>
    /// <returns>The seconds.</returns>
    public int SecondsLeft(DateTimeOffset? nowOverride = null)
    {
        var now = nowOverride ?? DateTimeOffset.UtcNow;
        var latest = entries.Values.Where(e => e > now).DefaultIfEmpty(now).Max();
        return (int)Math.Ceiling((latest - now).TotalSeconds);
    }
}
