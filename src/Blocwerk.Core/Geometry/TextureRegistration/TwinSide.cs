// <copyright file="TwinSide.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Geometry.TextureRegistration;

/// <summary>One side of a linked pair: where its photo placed the hold, and the evidence behind it.</summary>
/// <param name="World">The placement in world mm.</param>
/// <param name="SizeMm">The hold's larger dimension, mm (0 when unknown).</param>
/// <param name="Registration">The photo × facet registration that placed it.</param>
/// <param name="NearestInlierMm">Distance from the placement to the registration's nearest inlier, mm.</param>
/// <param name="BeyondHullMm">How far the placement lies beyond the registration's inlier hull, mm.</param>
public sealed record TwinSide(double[] World, double SizeMm, FacetRegistration Registration, double NearestInlierMm, double BeyondHullMm);
