// <copyright file="HoldDetectedBox.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;

namespace Blocwerk.Core.Entities;

/// <summary>The detector's box around a hold, centred on the hold's position.</summary>
/// <param name="Width">Box width as a fraction of the image width.</param>
/// <param name="Height">Box height as a fraction of the image height.</param>
public sealed record HoldDetectedBox(double Width, double Height)
{
    /// <summary>The box of a detection, or null when the detector gave none.</summary>
    /// <param name="detected">The detection.</param>
    /// <returns>The box.</returns>
    public static HoldDetectedBox? Of(DetectedHold detected)
    {
        ArgumentNullException.ThrowIfNull(detected);
        return detected is { BoxWidth: > 0, BoxHeight: > 0 } ? new HoldDetectedBox(detected.BoxWidth.Value, detected.BoxHeight.Value) : null;
    }
}
