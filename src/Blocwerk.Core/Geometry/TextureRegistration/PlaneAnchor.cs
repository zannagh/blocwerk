// <copyright file="PlaneAnchor.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Geometry.TextureRegistration;

/// <summary>
/// A point of a photo whose place on a facet of the model is already known without matching this photo's
/// textures: a hold of the photo with its previous placement carried into the model's frame
/// (<see cref="PlacementCarrier"/>), or a hold linked to one another panel's photo placed in the same run.
/// Anchors only seed the guided matching (<see cref="AnchorSeed"/>); they never place a hold themselves.
/// </summary>
/// <param name="X">Normalised photo x (0..1).</param>
/// <param name="Y">Normalised photo y (0..1).</param>
/// <param name="FacetId">The facet.</param>
/// <param name="A">Plane a, mm.</param>
/// <param name="B">Plane b, mm.</param>
public sealed record PlaneAnchor(double X, double Y, string FacetId, double A, double B);
