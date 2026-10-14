// <copyright file="HoldColorGreys.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Holds.Colors;

/// <summary>
/// The part of <see cref="HoldColorEstimator"/> for holds with almost no colour of their own: black, white, and a dark
/// blue that reads nearly black. What decides is how dark or light most of the hold is and which way its slight colour
/// cast points. The lamps cast warm light, so a black hold leans warm or green; a dark blue hold leans blue, which no
/// black hold on the measured wall did. A white hold may lean the way the wall does, but less: it is lit by the same
/// lamps, while a wooden hold is as coloured as the wall or more.
/// </summary>
internal static class HoldColorGreys
{
    private const double BlackBelowL = 34.0;
    private const double WhiteAboveL = 66.0;

    /// <summary>The share of the pixels that must be dark (black) or light (white).</summary>
    private const double LightnessShare = 0.75;

    /// <summary>The colour cast a black hold may have in any direction.</summary>
    private const double BlackCast = 0.7;

    /// <summary>The colour cast a black hold may have when it leans warm or green, as the lamps make it.</summary>
    private const double WarmBlackCast = 2.3;

    /// <summary>The share of dark pixels from which a hold leaning blue is a dark blue one.</summary>
    private const double DarkBlueShare = 0.6;

    /// <summary>The blue cast a dark blue hold needs at the least.</summary>
    private const double DarkBlueCast = 1.0;

    /// <summary>The colour cast a white hold may have in any direction.</summary>
    private const double WhiteCast = 1.5;

    /// <summary>The cast a white hold may have when it leans the way the wall does (the lamps' warm light).</summary>
    private const double LitWhiteCast = 2.5;

    /// <summary>A lit white hold's cast is at most this share of the wall's.</summary>
    private const double LitWhiteOfWall = 0.7;

    /// <summary>A lit white hold is at least this much lighter than the wall.</summary>
    private const double LitWhiteAboveWall = 8.0;

    /// <summary>How far (degrees) a lit white hold's cast may point from the wall's.</summary>
    private const double LitWhiteHueToWall = 25.0;

    /// <summary>Black, white, dark blue, or why none of them.</summary>
    /// <param name="own">The hold's pixels without its surroundings.</param>
    /// <param name="wall">The surroundings' median colour, or null when unknown.</param>
    /// <returns>The colour or null, and the verdict.</returns>
    public static (string? Color, HoldColorVerdict Verdict) Judge(List<OklabColor> own, OklabColor? wall)
    {
        var cast = Cast(own);
        var dark = own.Count(p => p.L < BlackBelowL) / (double)own.Count;
        if (dark >= LightnessShare && (cast.Chroma < BlackCast || (cast.Chroma < WarmBlackCast && IsIn(cast.Hue, 20, 180))))
        {
            return ("black", HoldColorVerdict.Proposed);
        }

        if (dark >= DarkBlueShare && cast.Chroma >= DarkBlueCast && IsIn(cast.Hue, 200, 290))
        {
            return ("blue", HoldColorVerdict.Proposed);
        }

        if (dark >= LightnessShare)
        {
            return (null, HoldColorVerdict.TintedGrey);
        }

        if (!IsLight(own))
        {
            return (null, HoldColorVerdict.UnclearGrey);
        }

        return IsWhite(own, cast, wall) ? ("white", HoldColorVerdict.Proposed) : (null, HoldColorVerdict.TintedGrey);
    }

    /// <summary>Whether a hold with some colour under chalk is a white one: light and white like <see cref="Judge"/> wants.</summary>
    /// <param name="own">The hold's pixels without its surroundings.</param>
    /// <param name="wall">The surroundings' median colour, or null when unknown.</param>
    /// <returns>Whether it is white.</returns>
    public static bool IsChalkedWhite(List<OklabColor> own, OklabColor? wall) => IsLight(own) && IsWhite(own, Cast(own), wall);

    private static bool IsLight(List<OklabColor> own) => own.Count(p => p.L > WhiteAboveL) >= LightnessShare * own.Count;

    private static bool IsWhite(List<OklabColor> own, OklabColor cast, OklabColor? wall)
    {
        if (cast.Chroma < WhiteCast)
        {
            return true;
        }

        if (wall is not { } w || cast.Chroma >= LitWhiteCast || cast.Chroma >= LitWhiteOfWall * w.Chroma)
        {
            return false;
        }

        var lightness = HoldColorEstimator.Median(own.Select(p => p.L));
        var turn = Math.Abs(((cast.Hue - w.Hue + 540.0) % 360.0) - 180.0);
        return lightness > w.L + LitWhiteAboveWall && turn < LitWhiteHueToWall;
    }

    private static OklabColor Cast(List<OklabColor> own) => new(0, own.Average(p => p.A), own.Average(p => p.B));

    private static bool IsIn(double hue, double from, double to) => hue >= from && hue < to;
}
