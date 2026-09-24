// <copyright file="TexturePlaneFrame.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;

namespace Blocwerk.Core.Geometry.TextureRegistration;

/// <summary>
/// The pixel grid of a flattened facet texture (<see cref="WallGeometryTexture"/>) on its facet plane. The
/// worker renders pixel centres (<c>docker/wall-geometry/wallgeometry/textures.py</c>, <c>_plane_points</c>):
/// <c>a = aMin + (col + 0.5)·res</c>, <c>b = bMax − (row + 0.5)·res</c> — a to the right, b up, row 0 at the top.
/// </summary>
/// <param name="FacetId">The facet.</param>
/// <param name="AMin">Plane a of the left image edge, mm.</param>
/// <param name="AMax">Plane a of the right image edge, mm.</param>
/// <param name="BMin">Plane b of the bottom image edge, mm.</param>
/// <param name="BMax">Plane b of the top image edge, mm.</param>
/// <param name="WidthPx">Image width.</param>
/// <param name="HeightPx">Image height.</param>
public sealed record TexturePlaneFrame(string FacetId, double AMin, double AMax, double BMin, double BMax, int WidthPx, int HeightPx)
{
    /// <summary>Gets the millimetres per pixel along a.</summary>
    public double MmPerPxA => (AMax - AMin) / WidthPx;

    /// <summary>Gets the millimetres per pixel along b.</summary>
    public double MmPerPxB => (BMax - BMin) / HeightPx;

    /// <summary>Gets a value indicating whether the grid is usable (positive size and extent).</summary>
    public bool IsValid => WidthPx > 0 && HeightPx > 0 && AMax > AMin && BMax > BMin;

    /// <summary>The frame of a stored texture.</summary>
    /// <param name="texture">The texture row.</param>
    /// <returns>Its frame.</returns>
    public static TexturePlaneFrame Of(WallGeometryTexture texture) =>
        new(texture.FacetId, texture.AMin, texture.AMax, texture.BMin, texture.BMax, texture.WidthPx, texture.HeightPx);

    /// <summary>A texture pixel position (OpenCV convention: pixel centres at integers) on the plane.</summary>
    /// <param name="x">Column.</param>
    /// <param name="y">Row.</param>
    /// <returns>Plane (a, b) in mm.</returns>
    public (double A, double B) ToPlane(double x, double y) => (AMin + ((x + 0.5) * MmPerPxA), BMax - ((y + 0.5) * MmPerPxB));

    /// <summary>A plane point as a texture pixel position (the inverse of <see cref="ToPlane"/>).</summary>
    /// <param name="a">Plane a, mm.</param>
    /// <param name="b">Plane b, mm.</param>
    /// <returns>(column, row).</returns>
    public (double X, double Y) ToPixel(double a, double b) => (((a - AMin) / MmPerPxA) - 0.5, ((BMax - b) / MmPerPxB) - 0.5);

    /// <summary>The texture px → plane mm mapping as a homography (it is affine).</summary>
    /// <returns>The mapping.</returns>
    public PlaneHomography PixelToPlane() => PlaneHomography.FromCoefficients(
    [
        MmPerPxA, 0, AMin + (0.5 * MmPerPxA),
        0, -MmPerPxB, BMax - (0.5 * MmPerPxB),
        0, 0, 1,
    ]);
}
