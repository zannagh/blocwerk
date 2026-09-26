// <copyright file="IWallVolumeService.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Abstractions;

/// <summary>
/// Volumes without markers: finds them on the wall's active model from its photo-real scene
/// (<see cref="Geometry.Volumes.VolumeDetector"/>), stores them (<see cref="Entities.WallVolume"/>) and places the
/// holds that sit on them (<see cref="Entities.Hold.VolumePlacementJson"/>). Prefers the splat; without one it searches the sparse points of
/// the model's capture (coarser); a wall with neither simply has no volumes and everything behaves as before. Derived data only; panels, panel hold
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

    /// <summary>
    /// Removes a falsely detected volume (wall admin): not drawn, holds back on the facet, not re-created by a later
    /// detection on the model; or restores it (<paramref name="removed"/> false, the "Undo").
    /// </summary>
    /// <param name="wallId">The wall.</param>
    /// <param name="volumeId">The volume.</param>
    /// <param name="removed">Remove (true) or restore (false).</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The outcome of the re-placement.</returns>
    Task<WallVolumeRunResult> SetRemovedAsync(Guid wallId, Guid volumeId, bool removed, CancellationToken ct = default);

    /// <summary>Switches one volume to flat faces or back to its measured height field (wall admin) and re-places its holds.</summary>
    /// <param name="wallId">The wall.</param>
    /// <param name="volumeId">The volume.</param>
    /// <param name="value">Flat sides on.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The outcome.</returns>
    Task<WallVolumeShapeResult> SetFlatSidesAsync(Guid wallId, Guid volumeId, bool value, CancellationToken ct = default);

    /// <summary>Whether newly detected volumes of the wall get flat sides (wall admin).</summary>
    /// <param name="wallId">The wall.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The wall setting.</returns>
    Task<bool> GetWallFlatSidesAsync(Guid wallId, CancellationToken ct = default);

    /// <summary>
    /// Sets "Volumes on this wall have flat sides" (wall admin); with <paramref name="applyToAll"/> also switches every volume
    /// of the active model (on: only where the flat faces fit well; off: all back to their height field).
    /// </summary>
    /// <param name="wallId">The wall.</param>
    /// <param name="value">The setting.</param>
    /// <param name="applyToAll">Also apply it to the existing volumes.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The outcome.</returns>
    Task<WallVolumeShapeResult> SetWallFlatSidesAsync(Guid wallId, bool value, bool applyToAll, CancellationToken ct = default);
}

/// <summary>The outcome of switching volumes between flat sides and the height field.</summary>
/// <param name="FlatSided">Volumes of the model with flat sides now.</param>
/// <param name="KeptHeightField">Volumes asked for flat sides that stayed on the height field (a poor or impossible fit).</param>
/// <param name="HoldsPlaced">Live holds now placed on a (visible) volume.</param>
/// <param name="HoldsChanged">Holds whose stored placement changed.</param>
public sealed record WallVolumeShapeResult(int FlatSided, int KeptHeightField, int HoldsPlaced, int HoldsChanged);

/// <summary>The outcome of a volume run.</summary>
/// <param name="Volumes">Volumes stored (accepted).</param>
/// <param name="Rejected">Raised candidates rejected (holds, edges, steps).</param>
/// <param name="HoldsPlaced">Live holds now placed on a (visible) volume.</param>
/// <param name="HoldsChanged">Holds whose stored placement changed.</param>
/// <param name="FromSparsePoints">Found in the capture's sparse points (no photo-real view): a coarser grid.</param>
public sealed record WallVolumeRunResult(int Volumes, int Rejected, int HoldsPlaced, int HoldsChanged, bool FromSparsePoints = false);

/// <summary>One volume for the admin list.</summary>
/// <param name="Id">The volume.</param>
/// <param name="Index">Its number within the model.</param>
/// <param name="FacetId">Its facet.</param>
/// <param name="AreaM2">Area on the facet.</param>
/// <param name="HeightMm">Height above the wall.</param>
/// <param name="Confidence">0–1.</param>
/// <param name="HoldCount">Holds placed on it.</param>
/// <param name="IsHidden">Hidden by an admin.</param>
/// <param name="IsRemoved">Removed as falsely detected (can be restored).</param>
/// <param name="HasFlatSides">Drawn and used as flat faces.</param>
/// <param name="Shape">With flat sides: "pyramid" or "roof".</param>
/// <param name="Faces">With flat sides: the number of flat sides.</param>
/// <param name="FitRmsMm">With flat sides: how far the faces are from the measurement (trimmed RMS), mm.</param>
/// <param name="CentreA">Footprint centre on the facet along u, mm.</param>
/// <param name="CentreB">Footprint centre on the facet along v, mm.</param>
public sealed record WallVolumeSummary(
    Guid Id,
    int Index,
    string FacetId,
    double AreaM2,
    double HeightMm,
    double Confidence,
    int HoldCount,
    bool IsHidden,
    bool IsRemoved = false,
    bool HasFlatSides = false,
    string? Shape = null,
    int Faces = 0,
    double? FitRmsMm = null,
    double CentreA = 0,
    double CentreB = 0);
