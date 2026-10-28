// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Entities;

namespace Blocwerk.Core.Runners;

/// <summary>
/// Starts a one-off job on this server for a queued <see cref="GpuJobKind.SplatPrepare"/> of a markerless capture when no 3D runner
/// that could take it is online (a markerless capture blocks its wall while it waits). The job reads the envelope and uploads the
/// result like a runner. Asked on every sweep while no runner is online, so an implementation must start at most one job per
/// queued job. The default (<see cref="NoOnDemandComputeLauncher"/>) starts nothing: the capture waits for a runner.
/// </summary>
public interface IOnDemandComputeLauncher
{
    /// <summary>True when a job was started (or already runs) for <paramref name="job"/>.</summary>
    Task<bool> TryLaunchAsync(GpuJob job, CancellationToken ct);
}
