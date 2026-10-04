// <copyright file="PanelCropPlan.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;

namespace Blocwerk.Core.Services.PanelCrop;

/// <summary>The panel being cropped (tracked) and its kept original, if it is already cropped.</summary>
/// <param name="Panel">The live panel row.</param>
/// <param name="Crop">Its kept original; null when not cropped yet.</param>
internal sealed record CropTarget(WallPanel Panel, WallPanelCrop? Crop)
{
    /// <summary>Gets the current crop relative to the original; the whole frame when not cropped.</summary>
    public PanelCropRect Current => Crop is { } c ? new PanelCropRect(c.Left, c.Top, c.Width, c.Height) : PanelCropRect.Full;
}

/// <summary>A crop worked out but not written yet.</summary>
/// <param name="OriginalRect">The new crop relative to the ORIGINAL photo, snapped to its pixels.</param>
/// <param name="Map">Current frame to the new frame.</param>
/// <param name="Preview">What it does to the live holds.</param>
internal sealed record PanelCropPlan(PanelCropRect OriginalRect, PanelFrameMap Map, PanelCropPreview Preview);
