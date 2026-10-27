// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Geometry.Surface;

/// <summary>Where one view puts a hold's base: its facet, plane position, and how steeply the view saw the facet.</summary>
/// <param name="FacetId">The facet the surface hits drop onto.</param>
/// <param name="A">Plane position along u, mm.</param>
/// <param name="B">Plane position along v, mm.</param>
/// <param name="ThetaDeg">Angle between the centre ray and the facet normal (0 = head on).</param>
/// <param name="Hits">Rays that hit the surface on that facet.</param>
public sealed record SurfaceCast(string FacetId, double A, double B, double ThetaDeg, int Hits);
