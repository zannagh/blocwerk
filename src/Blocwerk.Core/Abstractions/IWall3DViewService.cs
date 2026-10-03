// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Geometry.View3D;

namespace Blocwerk.Core.Abstractions;

/// <summary>Builds the opt-in 3D wall view from the wall's active glyph geometry model.</summary>
public interface IWall3DViewService
{
    /// <summary>
    /// The 3D view of <paramref name="wallId"/>, under exactly the wall-detail page's visibility rules:
    /// members (and a registered kiosk on its own wall) by id, anyone holding the share token by
    /// <paramref name="shareToken"/>, and nobody but the updating admin while the wall is in update mode.
    /// </summary>
    /// <param name="wallId">The wall.</param>
    /// <param name="boulderId">Optional boulder whose holds get their roles filled in.</param>
    /// <param name="shareToken">The share token when the viewer came through a share link.</param>
    /// <param name="ct">Cancellation.</param>
    /// <exception cref="UnauthorizedAccessException">
    /// Nobody is signed in and the session may not view the wall anonymously (no share token, not a
    /// kiosk on this wall) — the caller sends the visitor to sign in, as the wall page does.
    /// </exception>
    Task<Wall3DViewResult> BuildAsync(Guid wallId, Guid? boulderId, string? shareToken = null, CancellationToken ct = default);

    /// <summary>
    /// True when the wall has an active geometry model. Answers only yes/no and is meant for pages
    /// that already loaded (and so were allowed to see) the wall, to decide whether to offer the 3D view.
    /// </summary>
    Task<bool> HasActiveGeometryAsync(Guid wallId, CancellationToken ct = default);
}
