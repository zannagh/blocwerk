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
    public static HoldMoveOutcome Classify(HoldMoveMeasure measure, HoldMoveOptions options)
    {
        if (measure.DistanceMm > options.CutoffMm)
        {
            return HoldMoveOutcome.Removed;
        }

        var noise = measure.Source == HoldMoveSource.ThreeD ? options.NoiseMm : options.NoiseMm2D;
        if (measure.DistanceMm >= noise || measure.RotationDeg >= options.MinRotationDeg)
        {
            return HoldMoveOutcome.Kept;
        }

        return HoldMoveOutcome.Stayed;
    }

    /// <summary>The plain-language line for a move, as the boulder and the confirm screen show it.</summary>
    /// <param name="distanceMm">The distance, mm.</param>
    /// <param name="rotationDeg">The turn, degrees, when known.</param>
    /// <param name="outcome">The verdict.</param>
    /// <returns>For example "moved 6 cm, kept" or "moved 34 cm, removed from this boulder".</returns>
    public static string Describe(double distanceMm, double? rotationDeg, HoldMoveOutcome outcome)
    {
        var cm = Math.Max(1, (int)Math.Round(distanceMm / 10));
        var turn = rotationDeg is { } r && r >= 45 ? (int)Math.Round(r) : 0;
        var how = (distanceMm, turn) switch
        {
            ( < 30, > 0) => $"turned {turn}°",
            (_, > 0) => $"moved {cm} cm and turned {turn}°",
            _ => $"moved {cm} cm",
        };
        return outcome == HoldMoveOutcome.Removed ? $"{how}, removed from this boulder" : $"{how}, kept";
    }

    /// <summary>The boulder's review reason for a move.</summary>
    /// <param name="distanceMm">The distance, mm.</param>
    /// <param name="outcome">The verdict.</param>
    /// <returns>For example "hold moved 6 cm" or "hold moved 34 cm, removed".</returns>
    public static string Reason(double distanceMm, HoldMoveOutcome outcome)
    {
        var cm = Math.Max(1, (int)Math.Round(distanceMm / 10));
        return outcome == HoldMoveOutcome.Removed ? $"hold moved {cm} cm, removed" : $"hold moved {cm} cm";
    }
}
