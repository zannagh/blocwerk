// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Jobs;

/// <summary>
/// The read model of every long-running job (captures, GPU training, finishing, follow-up steps, texture re-renders,
/// re-solves, imports), derived from the rows and files that already track them. No authorization: callers decide the
/// scope (<see cref="IJobProgressService"/> for people, the telemetry collector for the gauges, the runner overview).
/// </summary>
public interface IJobProgressReader
{
    /// <summary>The jobs of the walls in <paramref name="scope"/>: running ones and those that ended within its window.</summary>
    Task<JobProgressSnapshot> ReadAsync(JobProgressScope scope, CancellationToken ct);
}
