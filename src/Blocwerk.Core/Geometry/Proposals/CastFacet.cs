// <copyright file="CastFacet.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Geometry.View3D;
using Blocwerk.Core.Geometry.Volumes;

namespace Blocwerk.Core.Geometry.Proposals;

/// <summary>A facet to cast onto: its frame, extent, outline and volumes.</summary>
/// <param name="Id">The facet.</param>
/// <param name="Frame">Its frame.</param>
/// <param name="Extent">Its extent on the plane.</param>
/// <param name="Volumes">Its visible volumes.</param>
/// <param name="Outline">Its outline as the 3D view draws it (<see cref="FacetShapes"/>), or null for the whole extent.</param>
public sealed record CastFacet(
    string Id, FacetFrame Frame, PlaneRectMm Extent, IReadOnlyList<VolumeSurface> Volumes, IReadOnlyList<double[]>? Outline = null)
{
    /// <summary>Whether (a, b) lies on the facet's real shape, or within <paramref name="marginMm"/> of it.</summary>
    /// <param name="a">Along u, mm.</param>
    /// <param name="b">Along v, mm.</param>
    /// <param name="marginMm">How far outside still counts, mm.</param>
    /// <returns>True on the facet.</returns>
    public bool Covers(double a, double b, double marginMm = 0) => FacetShapes.Covers(Outline, Extent, a, b, marginMm);
}
