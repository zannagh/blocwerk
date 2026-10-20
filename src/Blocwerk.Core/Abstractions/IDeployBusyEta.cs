// <copyright file="IDeployBusyEta.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Abstractions;

/// <summary>
/// Core's seam to the web host's busy ETA (<c>/health/busy-eta</c>): long background work that holds the deploy gate says
/// when it expects to be done, so the autodeploy hook can wait for it beyond its usual deferral cap instead of cutting
/// it off. Optional where it is injected.
/// </summary>
public interface IDeployBusyEta
{
    /// <summary>Records (or, with null, clears) when the work under <paramref name="key"/> expects to finish.</summary>
    /// <param name="key">The work (e.g. an enhancement run).</param>
    /// <param name="until">The expected end, or null when it is done.</param>
    void Set(Guid key, DateTimeOffset? until);
}
