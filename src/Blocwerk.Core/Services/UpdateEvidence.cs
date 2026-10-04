// <copyright file="UpdateEvidence.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Enums;

namespace Blocwerk.Core.Services;

/// <summary>
/// An old hold not found again whose spot shows bare wall on the new photo AND on this visit's 3D texture
/// (<see cref="PossiblyRemovedJudge"/>). Evidence only: the hold stays carried unless a person removes it.
/// </summary>
/// <param name="OldHoldId">The old hold.</param>
/// <param name="PanelId">The staged panel its spot is on.</param>
/// <param name="X">The x of the warp-predicted spot on the staged photo, normalised.</param>
/// <param name="Y">The y of the warp-predicted spot on the staged photo, normalised.</param>
/// <param name="ModelId">The 3D model whose texture was checked.</param>
/// <param name="FacetId">The facet the spot lands on.</param>
/// <param name="A">Plane a of the spot, mm.</param>
/// <param name="B">Plane b of the spot, mm.</param>
/// <param name="PhotoScore">Old photo against the new photo.</param>
/// <param name="TextureScore">Old photo against the texture.</param>
public sealed record PossiblyRemovedHold(
    Guid OldHoldId, Guid PanelId, double X, double Y, Guid ModelId, string FacetId, double A, double B, double PhotoScore, double TextureScore);

/// <summary>
/// A new detection the photo check leaves out by default while this visit's 3D model sees a hold there: the two
/// sources disagree, so a person decides.
/// </summary>
/// <param name="StagedHoldId">The staged detection.</param>
/// <param name="PanelId">Its staged panel.</param>
/// <param name="Reason">Why the photo check leaves it out.</param>
/// <param name="ModelId">The 3D model that sees it.</param>
/// <param name="FacetId">The facet its spot lands on, when the registration supports it there.</param>
/// <param name="A">Plane a of the spot, mm.</param>
/// <param name="B">Plane b of the spot, mm.</param>
public sealed record ConflictingNewHold(
    Guid StagedHoldId, Guid PanelId, NewHoldDiscardReason Reason, Guid ModelId, string? FacetId, double? A, double? B);
