// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Geometry.View3D;

/// <summary>
/// What turns a wall's view3d document (<see cref="View3DDocument"/>) back into one boulder's <see cref="Wall3DView"/>:
/// sent over JS interop beside the document's URL, and applied by <c>wall3d-doc.js</c> before the scene is built.
/// </summary>
/// <param name="BoulderId">The highlighted boulder, or null for the plain wall.</param>
/// <param name="BoulderName">Its name.</param>
/// <param name="Roles">The role of every document hold that is part of the boulder, by hold id.</param>
/// <param name="Holds">The holds drawn only for this boulder (<see cref="Wall3DHold.BoulderOnly"/>), roles included.</param>
public sealed record Wall3DOverlay(
    Guid? BoulderId,
    string? BoulderName,
    IReadOnlyDictionary<Guid, Wall3DHoldRole> Roles,
    IReadOnlyList<Wall3DHold> Holds);
