// <copyright file="HoldColorSignature.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Holds.Colors;

/// <summary>
/// The colour of a hold's coloured pixels, measured against the bare wall around it: the wall is the same wood all
/// over, so subtracting it takes out most of what the light does to a colour. Used to compare a hold with the holds of
/// known colour on the same photo (<see cref="HoldColorPalette"/>).
/// </summary>
/// <param name="A">Green (negative) to red (positive), minus the wall's.</param>
/// <param name="B">Blue (negative) to yellow (positive), minus the wall's.</param>
/// <param name="L">Lightness minus the wall's.</param>
/// <param name="Hue">The hue of the coloured pixels, degrees, not relative to the wall.</param>
/// <param name="Chroma">The median chroma of the coloured pixels, not relative to the wall.</param>
internal sealed record HoldColorSignature(double A, double B, double L, double Hue, double Chroma)
{
    /// <summary>Fewer pixels than this say nothing.</summary>
    private const int MinPixels = 12;

    /// <summary>Chroma below which a pixel counts as grey.</summary>
    private const double GreyChroma = 3.5;

    /// <summary>A hold pixel this close to the wall's median colour counts as wall.</summary>
    private const double WallDistance = 4.0;

    /// <summary>The share of coloured pixels a hold needs to have a signature.</summary>
    private const double ColoredShare = 0.40;

    /// <summary>The signature of a hold, or null when too little of it is coloured.</summary>
    /// <param name="hold">The pixels inside the hold's outline.</param>
    /// <param name="surroundings">Pixels in a ring around it; empty when unknown.</param>
    /// <returns>The signature.</returns>
    public static HoldColorSignature? Of(IReadOnlyList<OklabColor> hold, IReadOnlyList<OklabColor> surroundings)
    {
        OklabColor? wall = surroundings.Count < MinPixels ? null : HoldColorEstimator.MedianOf(surroundings);
        var own = wall is { } w ? hold.Where(p => p.DistanceTo(w) > WallDistance).ToList() : [.. hold];
        if (own.Count < MinPixels)
        {
            own = [.. hold];
        }

        var colored = own.Where(p => p.Chroma >= GreyChroma).ToList();
        if (colored.Count == 0 || colored.Count < ColoredShare * own.Count)
        {
            return null;
        }

        var hue = new OklabColor(0, colored.Sum(p => p.A), colored.Sum(p => p.B)).Hue;
        var chroma = HoldColorEstimator.Median(colored.Select(p => p.Chroma));
        var lightness = HoldColorEstimator.Median(colored.Select(p => p.L));
        var a = chroma * Math.Cos(hue * Math.PI / 180.0);
        var b = chroma * Math.Sin(hue * Math.PI / 180.0);
        return wall is { } x ? new(a - x.A, b - x.B, lightness - x.L, hue, chroma) : new(a, b, lightness, hue, chroma);
    }
}
