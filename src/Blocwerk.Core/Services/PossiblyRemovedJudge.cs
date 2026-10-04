// <copyright file="PossiblyRemovedJudge.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Services;

/// <summary>What the evidence says about an old hold the matcher did not find again on the new photo.</summary>
public enum RemovalVerdict
{
    /// <summary>Not enough evidence either way (no usable 3D model, no registration there, unsure scores): nothing is shown.</summary>
    Unknown = 0,

    /// <summary>Something is at the spot (a detection, a hold the 3D model sees, or the old hold's look): not removed.</summary>
    HoldPresent = 1,

    /// <summary>Both the new photo and this visit's 3D texture show bare wall where the hold was: possibly removed.</summary>
    BareWall = 2,
}

/// <summary>The evidence about one old hold at its predicted spot on the new photo.</summary>
/// <param name="Registered">
/// The new photo registered onto this visit's 3D model at the spot: an accepted facet registration, the spot inside the
/// facet and inside the registration's inlier hull (the texture there is real, matched content).
/// </param>
/// <param name="TextureFromThisVisit">The model was built after the new photo was staged, so its texture shows today's wall.</param>
/// <param name="DetectionNearby">A new detection lies at the spot (a replaced or slightly moved hold).</param>
/// <param name="SeenIn3DNearby">This visit's 3D model sees a hold at the spot.</param>
/// <param name="PhotoScore">Old photo at the old spot against the new photo at the predicted spot; null when not measurable.</param>
/// <param name="TextureScore">Old photo at the old spot against the 3D texture at the spot; null when not measurable.</param>
public sealed record RemovalEvidence(
    bool Registered,
    bool TextureFromThisVisit,
    bool DetectionNearby,
    bool SeenIn3DNearby,
    double? PhotoScore,
    double? TextureScore);

/// <summary>
/// Judges an old hold not found again (<see cref="RemovalEvidence"/>). Conservative on purpose: "bare wall" needs a
/// successful registration on a model from this visit, nothing at the spot, AND both presence scores confidently low;
/// any sign of a hold wins, and anything else stays <see cref="RemovalVerdict.Unknown"/>. A verdict never removes a
/// hold: it only puts it on the confirm screen for a person to decide.
/// </summary>
public static class PossiblyRemovedJudge
{
    /// <summary>At or below this correlation the old hold's look is not at the spot.</summary>
    public const double BareWallMaxScore = 0.3;

    /// <summary>At or above this correlation the old hold's look is still there (the triage's "same as old" bar).</summary>
    public const double HoldPresentMinScore = NewHoldTriage.SameAsOldScore;

    /// <summary>The verdict for one old hold.</summary>
    /// <param name="e">The evidence.</param>
    /// <returns>The verdict.</returns>
    public static RemovalVerdict Judge(RemovalEvidence e)
    {
        if (!e.Registered || !e.TextureFromThisVisit)
        {
            return RemovalVerdict.Unknown;
        }

        if (e.DetectionNearby || e.SeenIn3DNearby || e.PhotoScore >= HoldPresentMinScore || e.TextureScore >= HoldPresentMinScore)
        {
            return RemovalVerdict.HoldPresent;
        }

        return e is { PhotoScore: { } photo, TextureScore: { } texture } && photo <= BareWallMaxScore && texture <= BareWallMaxScore
            ? RemovalVerdict.BareWall
            : RemovalVerdict.Unknown;
    }
}
