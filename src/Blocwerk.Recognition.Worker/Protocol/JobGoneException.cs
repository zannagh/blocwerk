// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Recognition.Worker.Protocol;

/// <summary>A job-scoped call answered <c>404</c> or <c>410</c>: the job is no longer this runner's, so it stops.</summary>
public sealed class JobGoneException(Guid jobId, int status) : Exception($"Job {jobId} is gone ({status}).")
{
    public int Status { get; } = status;
}
