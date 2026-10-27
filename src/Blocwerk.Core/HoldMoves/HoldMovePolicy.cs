// <copyright file="HoldMovePolicy.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Enums;

namespace Blocwerk.Core.HoldMoves;

/// <summary>The cutoff rules: what a measured move does to the boulders using the hold. Pure.</summary>
public static class HoldMovePolicy
{
    /// <summary>Classifies <paramref name="measure"/>.</summary>
    /// <param name="measure">The measurement.</param>
    /// <param name="options">The thresholds.</param>
    /// <returns>Stayed (noise), Kept (moved or turned within the cutoff) or Removed (beyond it).</returns>
    /// <param name="confirmedMove">Whether a person confirmed that this hold moved (an accepted "this hold moved").</param>
    public static HoldMoveOutcome Classify(HoldMoveMeasure measure, HoldMoveOptions options, bool confirmedMove = false)
    {
        if (measure.DistanceMm > options.CutoffMm)
        {
            // Taking a hold off a boulder needs everything to agree: a person said it moved, the measure is trustworthy and
            // the distance is well past the cutoff. Anything less is "possibly moved": kept, for a person to check.
            var backed = confirmedMove || (options.RemoveUnconfirmedBeyondCutoff && measure.Corroborated);
            var certain = backed && measure.Confident && measure.DistanceMm >= options.CutoffMm * options.RemovalMargin;
            return certain ? HoldMoveOutcome.Removed : HoldMoveOutcome.Possible;
        }

        if (!measure.Confident && measure.Source == HoldMoveSource.ThreeD)
        {
            return HoldMoveOutcome.Stayed;
        }

        var noise = measure.Source == HoldMoveSource.ThreeD ? Math.Max(options.NoiseMm, options.SpreadFactor * (measure.SpreadMm ?? 0)) : options.NoiseMm2D;
        if (measure.DistanceMm >= noise || measure.RotationDeg >= options.MinRotationDeg)
        {
            return HoldMoveOutcome.Kept;
        }

        return HoldMoveOutcome.Stayed;
    }

    /// <summary>
    /// What a measured verdict does when <paramref name="confirmedMove"/> is false: a measurement alone never acts on boulders,
    /// so anything but "stayed" becomes <see cref="HoldMoveOutcome.Suspected"/>, for a person to confirm in the review.
    /// </summary>
    /// <param name="outcome">The measured verdict (<see cref="Classify"/>).</param>
    /// <param name="confirmedMove">Whether a person confirmed that this hold moved.</param>
    /// <returns>The verdict the promote acts on.</returns>
    public static HoldMoveOutcome Unconfirmed(HoldMoveOutcome outcome, bool confirmedMove) =>
        confirmedMove || outcome is HoldMoveOutcome.Stayed or HoldMoveOutcome.NotFound or HoldMoveOutcome.Replaced
            ? outcome
            : HoldMoveOutcome.Suspected;

    /// <summary>Whether <paramref name="outcome"/> acts on the boulders using the hold (marks them, or takes the hold off).</summary>
    /// <param name="outcome">The verdict.</param>
    /// <returns>False for a hold that stayed or a move nobody confirmed.</returns>
    public static bool Flags(HoldMoveOutcome outcome) => outcome is not (HoldMoveOutcome.Stayed or HoldMoveOutcome.Suspected);

    /// <summary>The plain-language line for a move, as the boulder and the confirm screen show it.</summary>
    /// <param name="distanceMm">The distance, mm.</param>
    /// <param name="rotationDeg">The turn, degrees, when known.</param>
    /// <param name="outcome">The verdict.</param>
    /// <returns>For example "moved 6 cm, kept" or "moved 34 cm, removed from this boulder".</returns>
    public static string Describe(double distanceMm, double? rotationDeg, HoldMoveOutcome outcome)
    {
        if (NotFoundText(outcome) is { } notFound)
        {
            return $"{notFound}, check this hold";
        }

        var cm = Math.Max(1, (int)Math.Round(distanceMm / 10));
        var turn = rotationDeg is { } r && r >= 45 ? (int)Math.Round(r) : 0;
        var how = (distanceMm, turn) switch
        {
            ( < 30, > 0) => $"turned {turn}°",
            (_, > 0) => $"moved {cm} cm and turned {turn}°",
            _ => $"moved {cm} cm",
        };
        return outcome switch
        {
            HoldMoveOutcome.Suspected => $"may have {how}, confirm it in the review",
            HoldMoveOutcome.Removed => $"{how}, removed from this boulder",
            HoldMoveOutcome.Possible => $"possibly moved about {cm} cm, check this hold",
            _ => $"{how}, kept",
        };
    }

    /// <summary>The boulder's review reason for a move.</summary>
    /// <param name="distanceMm">The distance, mm.</param>
    /// <param name="outcome">The verdict.</param>
    /// <returns>For example "hold moved 6 cm" or "hold moved 34 cm, removed".</returns>
    public static string Reason(double distanceMm, HoldMoveOutcome outcome)
    {
        if (NotFoundText(outcome) is { } notFound)
        {
            return $"hold {notFound}";
        }

        var cm = Math.Max(1, (int)Math.Round(distanceMm / 10));
        return outcome switch
        {
            HoldMoveOutcome.Removed => $"hold moved {cm} cm, removed",
            HoldMoveOutcome.Possible => $"hold possibly moved about {cm} cm, check",
            _ => $"hold moved {cm} cm",
        };
    }

    /// <summary>The plain-language reason of a hold that did not move but was not found again, or null for a move.</summary>
    /// <param name="outcome">The verdict.</param>
    /// <returns>"not found again on the new photo", "another hold is here now", or null.</returns>
    public static string? NotFoundText(HoldMoveOutcome outcome) => outcome switch
    {
        HoldMoveOutcome.NotFound => "not found again on the new photo",
        HoldMoveOutcome.Replaced => "another hold is here now",
        _ => null,
    };
}
