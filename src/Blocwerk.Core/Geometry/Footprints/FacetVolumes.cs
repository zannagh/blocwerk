// <copyright file="FacetVolumes.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Geometry.View3D;
using Blocwerk.Core.Geometry.Volumes;

namespace Blocwerk.Core.Geometry.Footprints;

/// <summary>The volumes standing on one facet, for the footprint refinement's occlusion test.</summary>
/// <param name="Facet">The facet.</param>
/// <param name="Volumes">Its visible volumes.</param>
public sealed record FacetVolumes(FacetFrame Facet, IReadOnlyList<VolumeSurface> Volumes);
