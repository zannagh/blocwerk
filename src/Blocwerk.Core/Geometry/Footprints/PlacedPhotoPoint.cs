// <copyright file="PlacedPhotoPoint.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Geometry.Footprints;

/// <summary>A placed hold as a photo ↔ facet-plane pair for resecting the photo's camera.</summary>
/// <param name="FacetId">The facet the hold is placed on.</param>
/// <param name="A">Plane a, mm.</param>
/// <param name="B">Plane b, mm.</param>
/// <param name="X">Normalised photo x (0..1 of the width).</param>
/// <param name="Y">Normalised photo y (0..1 of the height).</param>
public readonly record struct PlacedPhotoPoint(string FacetId, double A, double B, double X, double Y);
