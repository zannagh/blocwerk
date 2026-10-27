// <copyright file="HoldOutlierPlan.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Geometry.TextureRegistration;

/// <summary>What an outlier check found: the holds to re-place, and the counts per panel photo.</summary>
/// <param name="Fixes">The holds to re-place.</param>
/// <param name="Panels">Per panel photo, in the order they were checked.</param>
public sealed record HoldOutlierPlan(IReadOnlyList<HoldOutlierFix> Fixes, IReadOnlyList<HoldOutlierPanelCount> Panels);
