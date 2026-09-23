// <copyright file="PhoneLens.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Globalization;

namespace Blocwerk.Core.MarkerPlanning;

/// <summary>
/// One lens — or one zoom preset cropped from a lens — of a phone, as the camera app offers it, with what
/// the marker sizing needs: the saved photo's horizontal field of view and long edge.
/// </summary>
/// <param name="Id">The zoom button's label, e.g. <c>0.5x</c>, <c>1x</c>, <c>1.2x</c>, <c>5x</c>.</param>
/// <param name="Name">Plain name ("Ultra-wide", "Main", "Main, 28 mm crop", "Telephoto").</param>
/// <param name="Equivalent35mm">35 mm-equivalent focal length (the usual diagonal convention).</param>
/// <param name="HorizontalFovDeg">Field of view across the saved photo's long edge.</param>
/// <param name="ImageLongEdgePx">Long edge of the photo the camera app saves by default.</param>
/// <param name="Source">Where the numbers come from, and whether a real photo confirmed them.</param>
public sealed record PhoneLens(
    string Id,
    string Name,
    double Equivalent35mm,
    double HorizontalFovDeg,
    int ImageLongEdgePx,
    string Source)
{
    /// <summary>Half the long side of a 4:3 frame with the 43.27 mm diagonal of 35 mm film.</summary>
    public const double HalfLongSideMm = 43.27 * 0.8 / 2;

    /// <summary>Megapixels of the default 4:3 photo.</summary>
    public double Megapixels => ImageLongEdgePx * (double)ImageLongEdgePx * 0.75 / 1e6;

    /// <summary>Focal length in pixels of the saved photo.</summary>
    public double FocalPx => ImageLongEdgePx / 2.0 / Math.Tan(HorizontalFovDeg * Math.PI / 360.0);

    /// <summary>What the lens select shows, e.g. "0.5× ultra-wide · 13 mm · 12 MP".</summary>
    public string Label => string.Create(
        CultureInfo.InvariantCulture,
        $"{Id.Replace('x', '×')} {Name.ToLowerInvariant()} · {Equivalent35mm:0} mm · {Megapixels:0} MP");

    /// <summary>Horizontal FOV of a 4:3 photo from its 35 mm-equivalent focal length.</summary>
    public static double FovFromEquivalent(double equivalent35mm) =>
        2 * Math.Atan(HalfLongSideMm / equivalent35mm) * 180 / Math.PI;

    /// <summary>Horizontal FOV of a 4:3 photo from a quoted diagonal FOV.</summary>
    public static double FovFromDiagonal(double diagonalDeg) =>
        2 * Math.Atan(0.8 * Math.Tan(diagonalDeg * Math.PI / 360.0)) * 180 / Math.PI;

    /// <summary>The 35 mm equivalent matching a horizontal FOV (inverse of <see cref="FovFromEquivalent"/>).</summary>
    public static double EquivalentFromFov(double horizontalFovDeg) =>
        HalfLongSideMm / Math.Tan(horizontalFovDeg * Math.PI / 360.0);

    /// <summary>A lens given by its 35 mm equivalent.</summary>
    public static PhoneLens FromEquivalent(string id, string name, double equivalent35mm, int longEdgePx, string source) =>
        new(id, name, equivalent35mm, Math.Round(FovFromEquivalent(equivalent35mm), 1), longEdgePx, source);

    /// <summary>A lens given by the maker's diagonal FOV.</summary>
    public static PhoneLens FromDiagonal(string id, string name, double diagonalDeg, int longEdgePx, string source)
    {
        var fov = Math.Round(FovFromDiagonal(diagonalDeg), 1);
        return new(id, name, Math.Round(EquivalentFromFov(fov)), fov, longEdgePx, source);
    }
}
