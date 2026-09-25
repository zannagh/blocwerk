// <copyright file="ICaptureCoverageService.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Capture.Coverage;

/// <summary>
/// The capture coverage report (<see cref="CaptureCoverageAnalyzer"/>): computed by the post-capture chain once a
/// capture is done, read by wall admins in the capture history and over the capture API.
/// </summary>
public interface ICaptureCoverageService
{
    /// <summary>
    /// Computes and stores the capture's report from its model, cameras, volumes and video (pipeline: no user
    /// check). Null when the capture has no model.
    /// </summary>
    /// <param name="captureId">The capture.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The stored report, or null.</returns>
    Task<CaptureCoverageReport?> ComputeFromPipelineAsync(Guid captureId, CancellationToken ct = default);

    /// <summary>The stored report of a capture of the wall, for a wall admin (kiosk sessions are refused).</summary>
    /// <param name="wallId">The wall.</param>
    /// <param name="captureId">The capture (must belong to the wall).</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>Whether the capture exists on the wall, and its report.</returns>
    /// <exception cref="UnauthorizedAccessException">The user is not an admin of the wall.</exception>
    Task<CaptureCoverageLookup> GetAsync(Guid wallId, Guid captureId, CancellationToken ct = default);
}
