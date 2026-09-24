// <copyright file="IHoldRefinementQueue.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Abstractions;

/// <summary>
/// Regenerates the slow 3D measurements of edited holds off the request: after a debounce, their
/// placement and size are re-checked, then their contact footprint (multi-view when the wall's capture
/// photos are linked, single-view otherwise) and their protrusion are refined for those holds only.
/// Until then the 3D view projects the edited outline (the stored footprint's outline key no longer matches).
/// </summary>
public interface IHoldRefinementQueue
{
    /// <summary>Queues the holds; repeated edits within the debounce are folded into one run per wall.</summary>
    /// <param name="wallId">The wall.</param>
    /// <param name="holdIds">The edited holds.</param>
    void Enqueue(Guid wallId, IEnumerable<Guid> holdIds);
}
