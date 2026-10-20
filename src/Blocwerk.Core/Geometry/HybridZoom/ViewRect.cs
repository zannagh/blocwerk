// <copyright file="ViewRect.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Globalization;

namespace Blocwerk.Core.Geometry.HybridZoom;

/// <summary>
/// A rectangle in a panel photo's normalised space (x right, y down, 0..1), the zoomed viewport a close-up is
/// requested for. Always clamped into the photo and never degenerate once built through <see cref="Create"/>.
/// </summary>
/// <param name="X">Left edge.</param>
/// <param name="Y">Top edge.</param>
/// <param name="W">Width.</param>
/// <param name="H">Height.</param>
public readonly record struct ViewRect(double X, double Y, double W, double H)
{
    /// <summary>Narrowest side accepted (a 1/400 of the photo: past 12× zoom on a phone).</summary>
    public const double MinSide = 0.0025;

    /// <summary>Grid the rectangle is snapped to, so nearby requests share one cached render.</summary>
    public const int Grid = 256;

    /// <summary>Centre x.</summary>
    public double CentreX => X + (W / 2);

    /// <summary>Centre y.</summary>
    public double CentreY => Y + (H / 2);

    /// <summary>A rectangle clamped into the photo, or null when it is not finite or too small.</summary>
    /// <param name="x">Left edge.</param>
    /// <param name="y">Top edge.</param>
    /// <param name="w">Width.</param>
    /// <param name="h">Height.</param>
    /// <returns>The rectangle, or null.</returns>
    public static ViewRect? Create(double x, double y, double w, double h)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(w) || !double.IsFinite(h))
        {
            return null;
        }

        var x0 = Math.Clamp(x, 0, 1);
        var y0 = Math.Clamp(y, 0, 1);
        var x1 = Math.Clamp(x + w, 0, 1);
        var y1 = Math.Clamp(y + h, 0, 1);
        return x1 - x0 < MinSide || y1 - y0 < MinSide ? null : new ViewRect(x0, y0, x1 - x0, y1 - y0);
    }

    /// <summary>The rectangle snapped outward to the <see cref="Grid"/> (a stable cache key).</summary>
    /// <returns>The snapped rectangle.</returns>
    public ViewRect Snapped()
    {
        var x0 = Math.Floor(X * Grid) / Grid;
        var y0 = Math.Floor(Y * Grid) / Grid;
        var x1 = Math.Min(1, Math.Ceiling((X + W) * Grid) / Grid);
        var y1 = Math.Min(1, Math.Ceiling((Y + H) * Grid) / Grid);
        return new ViewRect(x0, y0, Math.Max(x1 - x0, 1.0 / Grid), Math.Max(y1 - y0, 1.0 / Grid));
    }

    /// <summary>The rectangle scaled about its centre by <paramref name="factor"/>, clamped into the photo.</summary>
    /// <param name="factor">Scale (below 1 shrinks).</param>
    /// <returns>The scaled rectangle (this one when the result would be degenerate).</returns>
    public ViewRect Scaled(double factor)
    {
        var w = W * factor;
        var h = H * factor;
        return Create(CentreX - (w / 2), CentreY - (h / 2), w, h) ?? this;
    }

    /// <summary>The point at (u, v) of the rectangle (0..1 each) in photo-normalised coordinates.</summary>
    /// <param name="u">Across, 0..1.</param>
    /// <param name="v">Down, 0..1.</param>
    /// <returns>The photo point.</returns>
    public (double X, double Y) At(double u, double v) => (X + (u * W), Y + (v * H));

    /// <summary>"x,y,w,h" with invariant formatting, for a response header.</summary>
    /// <returns>The text.</returns>
    public string ToHeader() => string.Join(
        ',',
        new[] { X, Y, W, H }.Select(d => d.ToString("0.######", CultureInfo.InvariantCulture)));
}
