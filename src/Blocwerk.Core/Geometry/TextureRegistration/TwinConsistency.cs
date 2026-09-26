// <copyright file="TwinConsistency.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Geometry.Registration;

namespace Blocwerk.Core.Geometry.TextureRegistration;

/// <summary>
/// Two panel photos that overlap see the same physical hold ("same hold" links); after both are placed, their two
/// world positions must agree. When they do not, the side with the weaker evidence is the wrong one: the registration's
/// quality (inliers × coverage over RMS) discounted by how far the hold lies from that registration's inliers.
/// Without a clear winner both are dropped. Pure.
/// </summary>
public static class TwinConsistency
{
    /// <summary>Smallest disagreement that counts, mm: fit error and hold parallax stay well below it.</summary>
    public const double MinDisagreementMm = 60;

    /// <summary>A disagreement also has to exceed this share of the hold's size (a big hold's centre is less exact).</summary>
    public const double SizeShare = 0.5;

    /// <summary>How much stronger one side's evidence must be to win a disagreement.</summary>
    public const double ClearWinnerRatio = 1.5;

    /// <summary>Distance scale of the locality discount, mm: a hold this far from any inlier halves its side's score.</summary>
    public const double LocalityScaleMm = 100;

    /// <summary>The world distance above which the two placements of a pair disagree, mm.</summary>
    /// <param name="first">One side.</param>
    /// <param name="second">The other.</param>
    /// <returns>The threshold.</returns>
    public static double Threshold(TwinSide first, TwinSide second) =>
        Math.Max(MinDisagreementMm, SizeShare * Math.Max(first.SizeMm, second.SizeMm));

    /// <summary>The distance between the two placements, mm.</summary>
    /// <param name="first">One side.</param>
    /// <param name="second">The other.</param>
    /// <returns>The distance.</returns>
    public static double Distance(TwinSide first, TwinSide second) => Vec3.Distance(first.World, second.World);

    /// <summary>Judges a linked pair.</summary>
    /// <param name="first">One side.</param>
    /// <param name="second">The other.</param>
    /// <returns>The verdict.</returns>
    public static TwinVerdict Judge(TwinSide first, TwinSide second)
    {
        if (Distance(first, second) <= Threshold(first, second))
        {
            return TwinVerdict.Agree;
        }

        var (s1, s2) = (Score(first), Score(second));
        if (s1 >= ClearWinnerRatio * s2)
        {
            return TwinVerdict.SecondWrong;
        }

        return s2 >= ClearWinnerRatio * s1 ? TwinVerdict.FirstWrong : TwinVerdict.BothWrong;
    }

    /// <summary>The strength of a side's evidence: its registration's quality, discounted by the hold's distance from its inliers.</summary>
    /// <param name="side">The side.</param>
    /// <returns>The score (higher is better).</returns>
    public static double Score(TwinSide side) =>
        Quality(side.Registration)
        / (1 + (side.NearestInlierMm / LocalityScaleMm))
        / (1 + (side.BeyondHullMm / LocalityScaleMm));

    /// <summary>A registration's quality: inliers × coverage over RMS (RMS floored at 1 mm).</summary>
    /// <param name="r">The registration.</param>
    /// <returns>The quality.</returns>
    public static double Quality(FacetRegistration r) => r.Inliers * r.Coverage / Math.Max(r.RmsMm ?? double.PositiveInfinity, 1);
}
