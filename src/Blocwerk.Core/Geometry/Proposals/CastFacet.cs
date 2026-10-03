// <copyright file="CastFacet.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Geometry.View3D;
using Blocwerk.Core.Geometry.Volumes;

namespace Blocwerk.Core.Geometry.Proposals;

/// <summary>A facet to cast onto: its frame, extent and volumes.</summary>
/// <param name="Id">The facet.</param>
/// <param name="Frame">Its frame.</param>
/// <param name="Extent">Its extent on the plane.</param>
/// <param name="Volumes">Its visible volumes.</param>
public sealed record CastFacet(string Id, FacetFrame Frame, PlaneRectMm Extent, IReadOnlyList<VolumeSurface> Volumes);
