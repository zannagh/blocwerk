// <copyright file="SurfaceAngle.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Globalization;

namespace Blocwerk.Core.MarkerPlanning;

/// <summary>
/// The one way a surface's signed tilt from vertical is put into words: "12° slab", "vertical" or
/// "45° overhang" — never "-12° overhang". The sign convention is the plan's, the solve request's and
/// the solver's: 0 = vertical, positive = overhang (normal points down), negative = slab.
/// </summary>
public static class SurfaceAngle
{
    /// <summary>
    /// Tilts within this many degrees of plumb read as vertical — and a planned surface that reads as
    /// vertical is a gravity reference for the solve. Matches the 3D view's <c>VERTICAL_TOLERANCE_DEG</c>.
    /// </summary>
    public const double VerticalToleranceDeg = 2.0;

    /// <summary>Which way <paramref name="overhangDeg"/> leans (|angle| below the tolerance is vertical).</summary>
    public static SurfaceLean LeanOf(double overhangDeg) =>
        Math.Abs(overhangDeg) < VerticalToleranceDeg ? SurfaceLean.Vertical
        : overhangDeg < 0 ? SurfaceLean.Slab
        : SurfaceLean.Overhang;

    /// <summary>True when the tilt reads as plumb.</summary>
    public static bool IsVertical(double overhangDeg) => LeanOf(overhangDeg) == SurfaceLean.Vertical;

    /// <summary>"12° slab", "vertical" or "45° overhang" (one decimal at most, invariant culture).</summary>
    public static string Describe(double overhangDeg)
    {
        if (!double.IsFinite(overhangDeg))
        {
            return "unknown angle";
        }

        return LeanOf(overhangDeg) switch
        {
            SurfaceLean.Vertical => "vertical",
            SurfaceLean.Slab => $"{Degrees(-overhangDeg)}° slab",
            _ => $"{Degrees(overhangDeg)}° overhang",
        };
    }

    /// <summary>The signed overhang angle for a lean and a (positive) size in degrees; vertical is 0.</summary>
    public static double Signed(SurfaceLean lean, double magnitudeDeg) => lean switch
    {
        SurfaceLean.Vertical => 0,
        SurfaceLean.Slab => -Math.Abs(magnitudeDeg),
        _ => Math.Abs(magnitudeDeg),
    };

    private static string Degrees(double value) => value.ToString("0.#", CultureInfo.InvariantCulture);
}
