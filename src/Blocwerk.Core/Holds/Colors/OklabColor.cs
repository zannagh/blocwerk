// <copyright file="OklabColor.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Holds.Colors;

/// <summary>
/// One pixel in Oklab, scaled by 100: lightness 0..100 and the two colour axes around zero. Oklab rather than CIE Lab
/// because its hue angle stays put when a colour gets lighter, darker or duller; in CIE Lab a strong blue drifts into
/// the hues of purple.
/// </summary>
/// <param name="L">Lightness, 0 (black) to 100 (white).</param>
/// <param name="A">Green (negative) to red (positive).</param>
/// <param name="B">Blue (negative) to yellow (positive).</param>
public readonly record struct OklabColor(double L, double A, double B)
{
    /// <summary>How colourful the pixel is: 0 for a grey, about 20 for a saturated colour.</summary>
    public double Chroma => Math.Sqrt((A * A) + (B * B));

    /// <summary>The hue angle in degrees, 0..360 (about 0 pink, 25 red, 55 orange, 97 yellow, 145 green, 264 blue, 305 purple).</summary>
    public double Hue => ((Math.Atan2(B, A) * 180.0 / Math.PI) + 360.0) % 360.0;

    /// <summary>Converts an sRGB pixel.</summary>
    /// <param name="r">Red, 0..255.</param>
    /// <param name="g">Green, 0..255.</param>
    /// <param name="b">Blue, 0..255.</param>
    /// <returns>The pixel in Oklab.</returns>
    public static OklabColor FromRgb(byte r, byte g, byte b)
    {
        double lr = Linear(r), lg = Linear(g), lb = Linear(b);
        var l = Math.Cbrt((0.4122214708 * lr) + (0.5363325363 * lg) + (0.0514459929 * lb));
        var m = Math.Cbrt((0.2119034982 * lr) + (0.6806995451 * lg) + (0.1073969566 * lb));
        var s = Math.Cbrt((0.0883024619 * lr) + (0.2817188376 * lg) + (0.6299787005 * lb));
        return new OklabColor(
            100.0 * ((0.2104542553 * l) + (0.7936177850 * m) - (0.0040720468 * s)),
            100.0 * ((1.9779984951 * l) - (2.4285922050 * m) + (0.4505937099 * s)),
            100.0 * ((0.0259040371 * l) + (0.7827717662 * m) - (0.8086757660 * s)));
    }

    /// <summary>The distance to another colour.</summary>
    /// <param name="other">The other colour.</param>
    /// <returns>The distance; about 2 is just visible.</returns>
    public double DistanceTo(OklabColor other)
    {
        double dl = L - other.L, da = A - other.A, db = B - other.B;
        return Math.Sqrt((dl * dl) + (da * da) + (db * db));
    }

    private static double Linear(byte channel)
    {
        var c = channel / 255.0;
        return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    }
}
