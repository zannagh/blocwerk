// <copyright file="IWallVolumeService.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Abstractions;

/// <summary>
/// Volumes without markers: finds them on the wall's active model from its photo-real scene
/// (<see cref="Geometry.Volumes.VolumeDetector"/>), stores them (<see cref="Entities.WallVolume"/>) and places the
/// holds that sit on them (<see cref="Entities.Hold.VolumePlacementJson"/>). Needs a splat: a wall without one
/// (no GPU runner) simply has no volumes and everything behaves as before. Derived data only; panels, panel hold
/// positions and boulders are never touched.
/// </summary>
public interface IWallVolumeService
{
    /// <summary>Detects and places after a splat is installed; no user check, never throws.</summary>
    /// <param name="wallId">The wall.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The outcome, or null when the wall has no scene or the run failed.</returns>
    Task<WallVolumeRunResult?> DetectFromPipelineAsync(Guid wallId, CancellationToken ct = default);

    /// <summary>Detects and places on a wall admin's request.</summary>
    /// <param name="wallId">The wall.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The outcome.</returns>
    Task<WallVolumeRunResult> DetectAsync(Guid wallId, CancellationToken ct = default);

    /// <summary>The active model's volumes (hidden ones included) for the admin list; wall admins only.</summary>
    /// <param name="wallId">The wall.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The volumes.</returns>
    Task<IReadOnlyList<WallVolumeSummary>> ListAsync(Guid wallId, CancellationToken ct = default);

    /// <summary>Hides or shows a volume (wall admin) and re-places the holds.</summary>
    /// <param name="wallId">The wall.</param>
    /// <param name="volumeId">The volume.</param>
    /// <param name="hidden">Hide it.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The outcome of the re-placement.</returns>
    Task<WallVolumeRunResult> SetHiddenAsync(Guid wallId, Guid volumeId, bool hidden, CancellationToken ct = default);
}

/// <summary>The outcome of a volume run.</summary>
/// <param name="Volumes">Volumes stored (accepted).</param>
/// <param name="Rejected">Raised candidates rejected (holds, edges, steps).</param>
/// <param name="HoldsPlaced">Live holds now placed on a (visible) volume.</param>
/// <param name="HoldsChanged">Holds whose stored placement changed.</param>
public sealed record WallVolumeRunResult(int Volumes, int Rejected, int HoldsPlaced, int HoldsChanged);

/// <summary>One volume for the admin list.</summary>
/// <param name="Id">The volume.</param>
/// <param name="Index">Its number within the model.</param>
/// <param name="FacetId">Its facet.</param>
/// <param name="AreaM2">Area on the facet.</param>
/// <param name="HeightMm">Height above the wall.</param>
/// <param name="Confidence">0–1.</param>
/// <param name="HoldCount">Holds placed on it.</param>
/// <param name="IsHidden">Hidden by an admin.</param>
public sealed record WallVolumeSummary(Guid Id, int Index, string FacetId, double AreaM2, double HeightMm, double Confidence, int HoldCount, bool IsHidden);
