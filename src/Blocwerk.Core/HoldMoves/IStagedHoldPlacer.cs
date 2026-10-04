// <copyright file="IStagedHoldPlacer.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;

namespace Blocwerk.Core.HoldMoves;

/// <summary>A provisional wall position of a staged hold, on a facet of the active 3D model (mm).</summary>
/// <param name="FacetId">The facet.</param>
/// <param name="PlaneAMm">Across the facet.</param>
/// <param name="PlaneBMm">Up the facet.</param>
public sealed record StagedPlacement(string FacetId, double PlaneAMm, double PlaneBMm);

/// <summary>
/// Where the staged (not yet live) holds of a panel update would sit on the wall's active 3D model, worked out by
/// registering the staged panel photos onto the model's textures. Nothing is written: the positions only feed the moves plan.
/// </summary>
public interface IStagedHoldPlacer
{
    /// <summary>The provisional placement of each staged twin the photos could be registered for.</summary>
    /// <param name="pairs">Old holds with their staged successors.</param>
    /// <returns>The placement per staged hold id; holds the registration could not place are missing.</returns>
    Task<IReadOnlyDictionary<Guid, StagedPlacement>> PlaceAsync(IReadOnlyList<(Hold Old, Hold Twin)> pairs);
}
