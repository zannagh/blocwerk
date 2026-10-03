// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Jobs;

/// <summary>
/// The long-running jobs the acting user may watch: every wall's for an administrator of the installation, the walls they
/// administer for anyone else (none: an empty list). Refused from a kiosk tablet. Read-only.
/// </summary>
public interface IJobProgressService
{
    /// <summary>
    /// Running jobs and those that ended within <paramref name="recent"/> (default <see cref="JobProgressService.DefaultRecent"/>,
    /// at most <see cref="JobProgressService.MaxRecent"/>), optionally of one wall only. A wall the user may not watch is
    /// refused (<see cref="UnauthorizedAccessException"/>).
    /// </summary>
    Task<JobProgressSnapshot> ListAsync(Guid? wallId, TimeSpan? recent, CancellationToken ct);

    /// <summary>Whether the acting user may watch any wall's jobs at all (to show or hide the panels).</summary>
    Task<bool> CanWatchAsync(Guid? wallId, CancellationToken ct);
}
