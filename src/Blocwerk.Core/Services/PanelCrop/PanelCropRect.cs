// <copyright file="PanelCropRect.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Services.PanelCrop;

/// <summary>
/// A crop rectangle in NORMALIZED (0..1) coordinates of a photo frame — the frame hold X/Y live in. Always relative to
/// some frame: the user's request is relative to the photo as it is now, the stored crop
/// (<see cref="Entities.WallPanelCrop"/>) relative to the original.
/// </summary>
/// <param name="Left">Left edge (fraction of the frame width).</param>
/// <param name="Top">Top edge (fraction of the frame height).</param>
/// <param name="Width">Width (fraction of the frame width).</param>
/// <param name="Height">Height (fraction of the frame height).</param>
public readonly record struct PanelCropRect(double Left, double Top, double Width, double Height)
{
    /// <summary>The smallest side a crop may keep, as a fraction of the frame: below this the panel is useless.</summary>
    public const double MinSide = 0.1;

    /// <summary>The whole frame.</summary>
    public static PanelCropRect Full => new(0, 0, 1, 1);

    /// <summary>Right edge.</summary>
    public double Right => Left + Width;

    /// <summary>Bottom edge.</summary>
    public double Bottom => Top + Height;

    /// <summary>Whether the rectangle covers (nearly) the whole frame, i.e. would crop nothing.</summary>
    public bool IsFull => Left <= 1e-4 && Top <= 1e-4 && Right >= 1 - 1e-4 && Bottom >= 1 - 1e-4;

    /// <summary>
    /// Throws <see cref="ArgumentException"/> unless the rectangle is finite, inside the frame, at least
    /// <see cref="MinSide"/> on each side and actually crops something.
    /// </summary>
    public void Validate()
    {
        double[] values = [Left, Top, Width, Height];
        if (values.Any(v => !double.IsFinite(v)))
        {
            throw new ArgumentException("The crop rectangle is not a number.");
        }

        const double slack = 1e-9;
        if (Left < -slack || Top < -slack || Right > 1 + slack || Bottom > 1 + slack)
        {
            throw new ArgumentException("The crop rectangle must lie inside the photo.");
        }

        if (Width < MinSide || Height < MinSide)
        {
            throw new ArgumentException("The crop would keep too little of the photo.");
        }

        if (IsFull)
        {
            throw new ArgumentException("The crop rectangle covers the whole photo; nothing to crop.");
        }
    }

    /// <summary>
    /// This rectangle (relative to <paramref name="parent"/>'s inside) expressed in the frame <paramref name="parent"/> is
    /// relative to: a crop of a cropped photo, as a crop of the original.
    /// </summary>
    /// <param name="parent">The rectangle this one is relative to.</param>
    /// <returns>The composed rectangle.</returns>
    public PanelCropRect Within(PanelCropRect parent) => new(
        parent.Left + (Left * parent.Width),
        parent.Top + (Top * parent.Height),
        Width * parent.Width,
        Height * parent.Height);
}
