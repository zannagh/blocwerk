// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Geometry.View3D;

/// <summary>
/// An enclosed recess beside the main wall (The Attic's slot between "closing up space" and "closing up end"): two
/// triangular facets facing each other, perpendicular to the main wall, whose hypotenuses lie on the main wall's plane.
/// The triangles lie behind the main wall's plane; the plane between their hypotenuses is the attic's roof surface,
/// a wall surface that never carries holds (<see cref="Wall3DRecesses"/>). Viewer-only: the renderer closes it with
/// solid body pieces and hides the capture's splats inside it.
/// </summary>
/// <param name="MainId">The main facet whose plane the hypotenuses lie on.</param>
/// <param name="LeftId">The closing triangle nearer the main wall's centre.</param>
/// <param name="ClosingId">The outer closing triangle (the one the recess is closed by); its back is solid.</param>
/// <param name="Roof">The roof surface's corners in world mm, on the main plane, between the two hypotenuses.</param>
/// <param name="Planes">
/// The recess volume's convex hull as outward half-spaces <c>[nx, ny, nz, d]</c>: a point is inside when
/// <c>n·p ≤ d</c> for all of them (the two triangle planes, the roof plane and the planes through the triangles' legs).
/// </param>
public sealed record Wall3DRecess(
    string MainId,
    string LeftId,
    string ClosingId,
    IReadOnlyList<double[]> Roof,
    IReadOnlyList<double[]> Planes);
