// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.ComponentModel.DataAnnotations;

namespace Blocwerk.Core.Entities;

/// <summary>
/// One training of a <see cref="GpuJob"/> that failed on a runner (a retryable failure it reported, or a failure that
/// ended the job while that runner held it), with that runner's reason. The runner overview reads a runner's recent
/// failures from here by an index instead of searching the jobs' failed-runner lists. Kept for
/// <c>GpuJobQueue.FailureRetention</c>.
/// </summary>
public class GpuRunnerFailure
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid RunnerId { get; set; }

    public Guid JobId { get; set; }

    public Guid WallId { get; set; }

    public DateTimeOffset At { get; set; }

    /// <summary>The reason as recorded on the job (safe to show the wall's admins).</summary>
    [MaxLength(2048)]
    public string? Reason { get; set; }
}
