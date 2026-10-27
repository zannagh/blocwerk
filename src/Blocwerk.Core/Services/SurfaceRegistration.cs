// <copyright file="SurfaceRegistration.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;

namespace Blocwerk.Core.Services;

/// <summary>The wall's latest panel registration on a 3D runner (for the active model).</summary>
/// <param name="State">none, queued, running, done or failed.</param>
/// <param name="Stage">The job's stage text.</param>
/// <param name="Error">Why it failed.</param>
/// <param name="CompletedAt">When it ended.</param>
/// <param name="Registered">Panel photos with a usable pose.</param>
public sealed record SurfaceRegistration(string State, string? Stage, string? Error, DateTimeOffset? CompletedAt, int Registered)
{
    /// <summary>Gets no registration.</summary>
    public static SurfaceRegistration None { get; } = new("none", null, null, null, 0);

    /// <summary>The registration of <paramref name="job"/>.</summary>
    public static SurfaceRegistration Of(GpuJob? job, int registered) => job is null
        ? None
        : new(
            job.Status switch
            {
                GpuJobStatus.Queued => "queued",
                GpuJobStatus.Claimed or GpuJobStatus.Running => "running",
                GpuJobStatus.Succeeded => "done",
                _ => "failed",
            },
            job.Stage,
            job.Error,
            job.CompletedAt,
            registered);
}
