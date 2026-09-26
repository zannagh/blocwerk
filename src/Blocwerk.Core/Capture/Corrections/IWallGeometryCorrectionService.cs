// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Capture.Corrections;

/// <summary>
/// Corrections of a wall's active 3D model, each saved as a NEW active model version derived from it
/// (<see cref="Entities.WallGeometryModel.DerivedFromModelId"/>); the old one stays in the history and can be activated
/// again. The new version reuses the old one's texture and photo-real files (their transforms are mapped, see
/// <see cref="Geometry.Corrections.WallGeometryModelTransformer"/>), and the post-capture chain runs again on it. Gated like
/// every wall-admin action: owner or admin of the wall, never from a kiosk tablet. Refusals throw
/// <see cref="Services.UserFacingException"/> with a message for the admin.
/// </summary>
public interface IWallGeometryCorrectionService
{
    /// <summary>The active model's sources and what can be corrected, or null when the wall has no model. Admin only.</summary>
    /// <param name="wallId">The wall.</param>
    /// <returns>The state.</returns>
    Task<GeometryCorrectionState?> GetStateAsync(Guid wallId);

    /// <summary>"Make sizes exact": two points on a photo of the model's capture and the millimetres between them.</summary>
    /// <param name="wallId">The wall.</param>
    /// <param name="reference">The photo, the two pixel points (stored photo) and the distance.</param>
    /// <returns>What was done.</returns>
    Task<GeometryCorrectionResult> MakeSizesExactAsync(Guid wallId, CaptureScaleReference reference);

    /// <summary>"This surface is vertical": "up" is re-derived so that surface is plumb (the whole model turns).</summary>
    /// <param name="wallId">The wall.</param>
    /// <param name="facetId">The surface.</param>
    /// <returns>What was done.</returns>
    Task<GeometryCorrectionResult> SetVerticalSurfaceAsync(Guid wallId, string facetId);

    /// <summary>"Not part of the wall": the surface is dropped (e.g. a rafter plane accepted by mistake).</summary>
    /// <param name="wallId">The wall.</param>
    /// <param name="facetId">The surface.</param>
    /// <returns>What was done.</returns>
    Task<GeometryCorrectionResult> DropSurfaceAsync(Guid wallId, string facetId);
}
