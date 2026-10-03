// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Runners;

/// <summary>
/// The runner overview: every runner the viewer may see with its state (online, paused, offline, revoked), the walls it
/// serves, what it is doing now (from <see cref="Jobs.IJobProgressReader"/>) and its recent failures. A site admin sees every
/// runner; a wall admin the runners that serve their walls (and their own), with another wall's job only as "busy"; anyone
/// else none. Refused from a kiosk. Revoking and sharing go through <see cref="IGpuRunnerService"/>.
/// </summary>
public interface IGpuRunnerOverviewService
{
    /// <summary>The overview for the acting user.</summary>
    Task<GpuRunnerOverview> GetAsync(CancellationToken ct);
}
