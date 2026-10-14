// <copyright file="LookOverFindingKind.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Enums;

/// <summary>
/// Why an applied panel update left a hold for the wall look-over (<see cref="Entities.WallLookOverFinding"/>). The
/// first six are the confirm screen's cards nobody answered (<see cref="UpdateExceptionKind"/>, same numbers); the
/// rest only exist after the update went live.
/// </summary>
public enum LookOverFindingKind
{
    /// <summary>An old hold not found again where the new photo and the 3D model show bare wall; it was kept in place.</summary>
    PossiblyRemoved = 0,

    /// <summary>An old hold carried onto a detection the matcher or the look check was unsure about.</summary>
    UnsureMatch = 1,

    /// <summary>A detection that was left out while the 3D model sees a hold there.</summary>
    ConflictingNew = 2,

    /// <summary>A hand-placed old hold that was merged onto the one detection at its spot.</summary>
    MatchedToHandPlaced = 3,

    /// <summary>A hand-placed old hold several detections compete for; nothing was merged.</summary>
    HandPlacedAmbiguous = 4,

    /// <summary>Another hold sits where the old one was; the old hold was kept in place, the other one added.</summary>
    ReplacedByOther = 5,

    /// <summary>An old hold on a boulder that was not found again and no longer shows at its spot; kept in place.</summary>
    NotFound = 6,

    /// <summary>An old hold kept in place with a new hold on the same spot, which no other finding is about.</summary>
    SameSpot = 7,

    /// <summary>
    /// A detection the update's triage discarded, kept so it can still be added as a hold. It is about no hold until
    /// somebody adds it, and it only counts as open while it is likely a missed hold (<see cref="LookOver.LookOverRules.LikelyMissed"/>).
    /// </summary>
    DiscardedDetection = 8,

    /// <summary>
    /// A full revision linked a neighbour photo's hold (<see cref="Entities.WallLookOverFinding.HoldId"/>) to the centre
    /// hold the overlap matcher paired it with (<see cref="Entities.WallLookOverFinding.OtherHoldId"/>) without being sure:
    /// is it the same hold on both photos? "Yes" keeps the link, "No, two holds" takes it apart and both holds stay.
    /// </summary>
    UnsureLink = 9,
}
