// <copyright file="HoldOutlierFix.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Detection.Enrichment;
using Blocwerk.Core.Entities;

namespace Blocwerk.Core.Geometry.TextureRegistration;

/// <summary>A placed hold whose 3D position the rest of the wall contradicts, and where it belongs instead.</summary>
/// <param name="Hold">The hold, as read.</param>
/// <param name="FacetId">The facet it belongs on.</param>
/// <param name="PlaneAMm">Its centre along the facet's a axis, mm.</param>
/// <param name="PlaneBMm">Its centre along the facet's b axis, mm.</param>
/// <param name="Size">Its size at the new position, or null when it cannot be measured there.</param>
/// <param name="Twin">The linked copy on a more consistent photo it was taken from; null when its neighbours placed it.</param>
public sealed record HoldOutlierFix(Hold Hold, string FacetId, double PlaneAMm, double PlaneBMm, HoldMetric? Size, Hold? Twin);
