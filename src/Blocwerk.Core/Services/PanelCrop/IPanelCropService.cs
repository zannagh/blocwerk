// <copyright file="IPanelCropService.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Services.PanelCrop;

/// <summary>
/// Crops a live panel photo IN PLACE (same panel row, same generation), keeping the original so the crop can be undone.
/// The holds stay where they are on the wall: their normalized position and outline are re-mapped to the cropped frame
/// in the same transaction as the photo, as are the panel's marker observations and pending hold proposals. Holds the
/// crop cuts off are removed by the ordinary hold-removal rules (boulders that used them turn historic). Afterwards the
/// 3D placement follows the panel through the edited-holds path. Every mutation needs a wall admin and is journalled.
/// </summary>
public interface IPanelCropService
{
    /// <summary>The panel's crop state; null when the panel is not a live panel of a wall the caller can see.</summary>
    /// <param name="wallId">The wall.</param>
    /// <param name="panelId">The panel.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The state.</returns>
    Task<PanelCropState?> GetStateAsync(Guid wallId, Guid panelId, CancellationToken ct = default);

    /// <summary>What cropping to <paramref name="rect"/> (relative to the photo as shown now) would remove. Changes nothing.</summary>
    /// <param name="wallId">The wall.</param>
    /// <param name="panelId">The panel.</param>
    /// <param name="rect">The crop.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The preview.</returns>
    Task<PanelCropPreview> PreviewAsync(Guid wallId, Guid panelId, PanelCropRect rect, CancellationToken ct = default);

    /// <summary>
    /// Crops the panel photo to <paramref name="rect"/> (relative to the photo as shown now). When the crop cuts off holds
    /// and <paramref name="confirmRemovals"/> is false nothing is written and the result carries the preview to confirm.
    /// </summary>
    /// <param name="wallId">The wall.</param>
    /// <param name="panelId">The panel.</param>
    /// <param name="rect">The crop.</param>
    /// <param name="confirmRemovals">The user confirmed removing the cut-off holds.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The result.</returns>
    Task<PanelCropResult> CropAsync(Guid wallId, Guid panelId, PanelCropRect rect, bool confirmRemovals, CancellationToken ct = default);

    /// <summary>Restores the original photo and maps the panel's holds back onto it. Throws when the panel is not cropped.</summary>
    /// <param name="wallId">The wall.</param>
    /// <param name="panelId">The panel.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The result.</returns>
    Task<PanelCropResult> UndoAsync(Guid wallId, Guid panelId, CancellationToken ct = default);
}
