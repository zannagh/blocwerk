// <copyright file="IHoldFootprintService.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Abstractions;

/// <summary>
/// Batch refinement of the 3D hold shapes: stores each traced hold's contact footprint on its facet
/// (<see cref="Entities.Hold.FootprintMm"/>) from the capture photos that see it. Never runs per page view.
/// </summary>
public interface IHoldFootprintService
{
    /// <summary>"Refine 3D hold shapes" for a wall admin.</summary>
    /// <param name="wallId">The wall.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>What was stored.</returns>
    Task<HoldFootprintRunResult> RefineAsync(Guid wallId, CancellationToken ct = default);

    /// <summary>The same step from a server pipeline (capture solve, outline upgrade): no user check, never throws.</summary>
    /// <param name="wallId">The wall.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>What was stored, or null when it could not run.</returns>
    Task<HoldFootprintRunResult?> RefineFromPipelineAsync(Guid wallId, CancellationToken ct = default);

    /// <summary>
    /// The same step for a few holds only (after an edit made their footprints stale): the wall's other
    /// holds still feed the mappings and cameras, but only these are traced and written. Never throws.
    /// </summary>
    /// <param name="wallId">The wall.</param>
    /// <param name="holdIds">The holds to refine.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>What was stored, or null when it could not run.</returns>
    Task<HoldFootprintRunResult?> RefineHoldsFromPipelineAsync(Guid wallId, IReadOnlyCollection<Guid> holdIds, CancellationToken ct = default);
}

/// <summary>The outcome of one footprint refinement.</summary>
/// <param name="MultiView">Holds whose footprint was intersected from ≥ 2 views.</param>
/// <param name="SingleView">Holds that got the approximate single-view correction.</param>
/// <param name="Skipped">Traced holds that could not be refined (no mapping or panel camera).</param>
/// <param name="CapturePhotos">Capture photos with solved cameras that were available.</param>
public sealed record HoldFootprintRunResult(int MultiView, int SingleView, int Skipped, int CapturePhotos);
