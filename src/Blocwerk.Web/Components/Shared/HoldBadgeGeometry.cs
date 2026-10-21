// <copyright file="HoldBadgeGeometry.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Web.Components.Shared;

/// <summary>
/// Where the wall editor puts a small status badge (the "?" of a suggested colour or of missing
/// information) next to a hold, and how big it is.
/// <para>
/// Two rules, both learned the hard way. The badge thins with the zoom like the editing handles
/// (<c>WallPhotoEditor.HandleScale</c>): drawn in fixed SVG units it balloons with the photo, and at
/// high zoom a "?" was wider than the hold being traced. And it sits OUTSIDE the hold's circle,
/// tangent to it on a diagonal, rather than straddling its edge: the edge is exactly what the shape
/// tool edits, so a badge on it hid the part of the outline the owner was working on.
/// </para>
/// </summary>
public static class HoldBadgeGeometry
{
    /// <summary>The badge radius at zoom 1, in the overlay's 0..100 units.</summary>
    public const double BaseRadius = 1.0;

    /// <summary>
    /// The badge radius for a handle scale (1 / zoom). Never larger than at zoom 1, so zooming out
    /// below 1 does not grow it either.
    /// </summary>
    /// <param name="scale">The editor's handle scale, 1 / zoom.</param>
    /// <returns>The radius in overlay units.</returns>
    public static double Radius(double scale) => BaseRadius * Math.Clamp(scale, 1.0 / 12.0, 1.0);

    /// <summary>
    /// The badge centre's offset from the hold centre: on the diagonal given by
    /// <paramref name="signX"/> / <paramref name="signY"/>, far enough out that the badge only
    /// touches the hold's circle.
    /// </summary>
    /// <param name="holdRadius">The hold's drawn radius, in overlay units.</param>
    /// <param name="badgeRadius">The badge radius from <see cref="Radius"/>.</param>
    /// <param name="signX">+1 for right of the hold, -1 for left.</param>
    /// <param name="signY">+1 for below the hold, -1 for above.</param>
    /// <returns>The offset in overlay units.</returns>
    public static (double Dx, double Dy) Offset(double holdRadius, double badgeRadius, int signX, int signY)
    {
        var d = (holdRadius + badgeRadius) * Math.Sqrt(0.5);
        return (signX * d, signY * d);
    }
}
