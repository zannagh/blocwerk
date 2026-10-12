// <copyright file="PresencePatch.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Abstractions;

/// <summary>
/// How much of the new photo a <see cref="PresenceQuery"/> compares and how far around the aligned spot of the old photo
/// it is searched. Without a hold radius both are the fixed sizes every caller has always had; with one they grow with
/// the hold, so a large hold or volume is compared by its outline and surroundings instead of a patch of its inside, and
/// is still found when its centre was detected a little elsewhere. Pure, so it can be checked without OpenCV.
/// </summary>
public static class PresencePatch
{
    /// <summary>Half the patch side (px of the new photo) without a hold radius, and the least with one.</summary>
    public const int DefaultRadius = 56;

    /// <summary>The search slack (px of the old photo) around the patch without a hold radius.</summary>
    public const int DefaultSlack = 70;

    /// <summary>The most half a patch side grows to.</summary>
    public const int MaxRadius = 200;

    /// <summary>The patch reaches this many hold radii from the centre.</summary>
    public const double RadiusFactor = 1.3;

    /// <summary>Half the patch side for a hold of <paramref name="holdRadiusPx"/> (0 or less: no radius given).</summary>
    /// <param name="holdRadiusPx">The hold's radius on the new photo, px.</param>
    /// <returns>Half the patch side, px.</returns>
    public static int Radius(double holdRadiusPx) =>
        holdRadiusPx > 0 ? (int)Math.Clamp(Math.Round(RadiusFactor * holdRadiusPx), DefaultRadius, MaxRadius) : DefaultRadius;

    /// <summary>The search slack for a hold of <paramref name="holdRadiusPx"/>: in proportion to the patch.</summary>
    /// <param name="holdRadiusPx">The hold's radius on the new photo, px.</param>
    /// <returns>The slack, px.</returns>
    public static int Slack(double holdRadiusPx) => DefaultSlack * Radius(holdRadiusPx) / DefaultRadius;
}
