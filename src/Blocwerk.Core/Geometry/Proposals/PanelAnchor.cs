// <copyright file="PanelAnchor.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Geometry.Proposals;

/// <summary>A placed hold of a panel photo: where it is on the photo and where it was mapped on its facet.</summary>
/// <param name="FacetId">Its facet.</param>
/// <param name="A">Flat position along u, mm.</param>
/// <param name="B">Flat position along v, mm.</param>
/// <param name="X">Normalised photo x.</param>
/// <param name="Y">Normalised photo y.</param>
public readonly record struct PanelAnchor(string FacetId, double A, double B, double X, double Y);
