// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.MarkerPlanning;

namespace Blocwerk.Core.Geometry.View3D;

/// <summary>A plan triangle segment as the 3D view cuts it (<see cref="Wall3DFacetOutlines"/>).</summary>
/// <param name="ParentIndex">The segment its hypotenuse is attached to in the plan.</param>
/// <param name="RightAngle">Its right-angle corner in its own plan frame, which is the solved facet's (a, b).</param>
public sealed record PlanTriangle(int ParentIndex, TriangleCorner RightAngle);
