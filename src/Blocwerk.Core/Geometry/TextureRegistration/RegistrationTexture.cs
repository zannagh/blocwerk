// <copyright file="RegistrationTexture.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Geometry.View3D;

namespace Blocwerk.Core.Geometry.TextureRegistration;

/// <summary>One facet texture of the active model, loaded for registering photos onto it.</summary>
/// <param name="Frame">Its pixel grid on the plane.</param>
/// <param name="Extent">The facet extent holds must fall into (the model's, else the texture's own bounds).</param>
/// <param name="Image">The encoded texture.</param>
/// <param name="Mask">The encoded coverage mask, or null.</param>
/// <param name="Facet">The facet's 3D frame, when the model has one (needed to predict its view from a neighbour).</param>
public sealed record RegistrationTexture(TexturePlaneFrame Frame, PlaneRectMm Extent, byte[] Image, byte[]? Mask, FacetFrame? Facet = null);
