// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Geometry.Surface;

/// <summary>The plan, and for linked holds both views hit how far apart their own casts were (3D, mm).</summary>
public sealed record SurfacePlan(IReadOnlyList<SurfaceFix> Fixes, IReadOnlyList<SurfacePanelCount> Panels, IReadOnlyList<double> PairGapsMm);
