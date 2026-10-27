// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Detection.Enrichment;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Geometry.View3D;

namespace Blocwerk.Core.Geometry.Surface;

/// <summary>Where a hold goes and what it gets there. <paramref name="Size"/> and <paramref name="Footprint"/> may be null.</summary>
/// <param name="Hold">The hold as read.</param>
/// <param name="FacetId">Its facet.</param>
/// <param name="A">Plane position along u, mm.</param>
/// <param name="B">Plane position along v, mm.</param>
/// <param name="Size">Its size measured from its own view at the new position.</param>
/// <param name="Footprint">Its single-view footprint from its own view, around the new position.</param>
/// <param name="FromOutlierFallback">True when the surface missed it and the outlier check placed it.</param>
public sealed record SurfaceFix(Hold Hold, string FacetId, double A, double B, HoldMetric? Size, HoldFootprint? Footprint, bool FromOutlierFallback);
