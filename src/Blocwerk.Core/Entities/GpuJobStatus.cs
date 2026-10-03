// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Entities;

/// <summary>Where a <see cref="GpuJob"/> stands.</summary>
public enum GpuJobStatus
{
    /// <summary>Prepared by the server; waiting for an eligible runner. Never times out.</summary>
    Queued = 0,

    /// <summary>A runner claimed it (holds the lease) and is downloading the bundle.</summary>
    Claimed = 1,

    /// <summary>The runner reported training progress.</summary>
    Running = 2,

    /// <summary>The trained splat was uploaded; the server finishes (crop, export, LOD) and installs it.</summary>
    Succeeded = 3,

    /// <summary>Gave up (failed attempts exhausted, or a non-retryable failure).</summary>
    Failed = 4,

    /// <summary>Cancelled by an admin (or superseded by a retrain).</summary>
    Cancelled = 5,
}
