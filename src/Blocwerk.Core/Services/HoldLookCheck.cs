// <copyright file="HoldLookCheck.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.HoldMoves;

namespace Blocwerk.Core.Services;

/// <summary>Whether the detection an old hold was paired with still looks like that hold.</summary>
public enum HoldLook
{
    /// <summary>The new photo shows the old hold's look at the detection: the pair stands.</summary>
    Same = 0,

    /// <summary>Not clear, or not measurable: the pair stands and a person is asked.</summary>
    Unsure = 1,

    /// <summary>The new photo shows something else there: another hold took the old hold's place.</summary>
    Replaced = 2,
}

/// <summary>A carried pair whose look is not clearly the same.</summary>
/// <param name="OldHoldId">The old hold.</param>
/// <param name="NewHoldId">The staged detection the matcher paired it with.</param>
/// <param name="PanelId">The staged panel of the detection.</param>
/// <param name="Look">Unsure (still paired) or replaced (no longer paired).</param>
/// <param name="Score">The patch correlation, or null when it could not be measured.</param>
public sealed record HoldLookFinding(Guid OldHoldId, Guid NewHoldId, Guid PanelId, HoldLook Look, double? Score);

/// <summary>
/// The matcher pairs an old hold with a detection mostly by where it is, so a hold that was swapped for another one on
/// the same spot is still paired. This judges the pair by its look instead: the new photo's patch at the detection
/// against the old photo around the old hold (<see cref="IHoldPresenceProbe"/>). The same probe at an unpaired old
/// hold's predicted spot tells a hold the detector merely missed from one that is gone. Pure.
/// </summary>
public static class HoldLookCheck
{
    /// <summary>The verdict on a pair's correlation; an unmeasured pair is unsure, never replaced.</summary>
    /// <param name="score">The correlation, or null.</param>
    /// <param name="options">The thresholds.</param>
    /// <returns>The verdict.</returns>
    public static HoldLook Judge(double? score, HoldMoveOptions options)
    {
        if (score is not { } s || s >= options.SameLookMinScore)
        {
            return score is null ? HoldLook.Unsure : HoldLook.Same;
        }

        return s >= options.ReplacedBelowScore ? HoldLook.Unsure : HoldLook.Replaced;
    }

    /// <summary>Whether an old hold with no twin still shows at its predicted spot.</summary>
    /// <param name="score">The correlation at the spot.</param>
    /// <param name="options">The thresholds.</param>
    /// <returns>True when it is demonstrably still there.</returns>
    public static bool StillThere(double score, HoldMoveOptions options) => score >= options.StillThereMinScore;

    /// <summary>
    /// The probe query for a spot on the new photo against a spot on the old photo (RAW pixels), or null when the spot is
    /// outside the new photo or the local scale between the photos is unknown there.
    /// </summary>
    /// <param name="newToOld">The panel's matched pairs, new photo to old photo.</param>
    /// <param name="newSpot">The spot on the new photo.</param>
    /// <param name="oldSpot">The old hold's centre on the old photo.</param>
    /// <param name="newSize">The new photo's size.</param>
    /// <returns>The query or null.</returns>
    public static PresenceQuery? Query(
        IReadOnlyList<PointPair> newToOld, (double X, double Y) newSpot, (double X, double Y) oldSpot, (int Width, int Height) newSize)
    {
        if (newSpot.X < 0 || newSpot.Y < 0 || newSpot.X >= newSize.Width || newSpot.Y >= newSize.Height)
        {
            return null;
        }

        return LocalAffine.Scale(newToOld, newSpot.X, newSpot.Y) is { } scale and > 0
            ? new PresenceQuery(newSpot.X, newSpot.Y, oldSpot.X, oldSpot.Y, scale)
            : null;
    }
}

/// <summary>One re-photographed panel as the look check reads it.</summary>
/// <param name="PanelId">The staged panel.</param>
/// <param name="OldHolds">The old holds carried on this panel.</param>
/// <param name="Staged">The staged detections of the panel.</param>
/// <param name="OldPhoto">The photo the old holds are normalized to, or null when there is none.</param>
/// <param name="NewPhoto">The staged photo.</param>
internal sealed record LookPanel(Guid PanelId, IReadOnlyList<Entities.Hold> OldHolds, IReadOnlyList<Entities.Hold> Staged, byte[]? OldPhoto, byte[] NewPhoto);

/// <summary>What the look check of one session found, filled panel by panel.</summary>
internal sealed class LookOutcome
{
    /// <summary>Gets the pairs that are unsure or replaced.</summary>
    public List<HoldLookFinding> Findings { get; } = [];

    /// <summary>Gets the correlation at the predicted spot of each old hold without a twin that could be measured.</summary>
    public Dictionary<Guid, double> SpotScores { get; } = [];
}
