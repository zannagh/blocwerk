// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Entities;

namespace Blocwerk.Core.Runners;

/// <summary>No on-demand jobs on this server: work that needs a 3D runner waits for one.</summary>
public sealed class NoOnDemandComputeLauncher : IOnDemandComputeLauncher
{
    /// <inheritdoc />
    public Task<bool> TryLaunchAsync(GpuJob job, CancellationToken ct) => Task.FromResult(false);
}
