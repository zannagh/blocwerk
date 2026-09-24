// <copyright file="IHoldProtrusionService.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Abstractions;

/// <summary>
/// Measures how far each hold stands out of its facet from the wall's active photo-real scene and stores
/// it (<see cref="Entities.Hold.ProtrusionMm"/>), so the 3D view can draw the overlays ON the holds
/// instead of on the wall behind them. Runs after a splat is installed; never per page view.
/// </summary>
public interface IHoldProtrusionService
{
    /// <summary>Measures the wall's live holds; no user check, never throws.</summary>
    /// <param name="wallId">The wall.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>What was measured and stored, or null when the wall has no scene or the run failed.</returns>
    Task<HoldProtrusionRunResult?> MeasureFromPipelineAsync(Guid wallId, CancellationToken ct = default);

    /// <summary>The same measurement for a few holds only (after an edit); no user check, never throws.</summary>
    /// <param name="wallId">The wall.</param>
    /// <param name="holdIds">The holds to measure.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>What was measured and stored, or null when the wall has no scene or the run failed.</returns>
    Task<HoldProtrusionRunResult?> MeasureHoldsFromPipelineAsync(Guid wallId, IReadOnlyCollection<Guid> holdIds, CancellationToken ct = default);
}

/// <summary>The outcome of one protrusion run.</summary>
/// <param name="Measured">Holds measured from the scene.</param>
/// <param name="Estimated">Holds with too few scene points (size estimate).</param>
/// <param name="OnVolumes">Measured holds standing on a volume (auto-detected, unreviewed).</param>
/// <param name="Written">Holds whose stored value changed.</param>
public sealed record HoldProtrusionRunResult(int Measured, int Estimated, int OnVolumes, int Written);
