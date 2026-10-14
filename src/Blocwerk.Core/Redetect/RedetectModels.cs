// <copyright file="RedetectModels.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;

namespace Blocwerk.Core.Redetect;

/// <summary>One fresh detection on the panel photo: a hold that is not stored, with its outline when one was traced.</summary>
/// <param name="Shape">Position, radius, outline and measurements as the detector and the outliner produced them.</param>
/// <param name="Color">The colour read off the photo for it, or null when it could not be told.</param>
public sealed record RedetectDetection(Hold Shape, string? Color);

/// <summary>One stored hold of the panel with what the decision needs to know about it.</summary>
/// <param name="Hold">The hold.</param>
/// <param name="Boulders">Every boulder that uses it, archived and historic ones included.</param>
/// <param name="ActiveBoulders">How many current boulders use it.</param>
/// <param name="Linked">True when it is linked to a hold on another panel.</param>
public sealed record RedetectExisting(Hold Hold, IReadOnlySet<Guid> Boulders, int ActiveBoulders, bool Linked);

/// <summary>What a person said earlier about a spot where the detector now finds a hold.</summary>
public enum RedetectEarlierKind
{
    /// <summary>The detection there was answered "Not a hold".</summary>
    NotAHold = 0,

    /// <summary>An update set the detection there aside, and nobody answered it.</summary>
    SetAside = 1,

    /// <summary>A person deleted a hold there.</summary>
    Deleted = 2,
}

/// <summary>An earlier answer about a spot. It is shown on the card; it is not taken as the truth.</summary>
/// <param name="Kind">What was said.</param>
/// <param name="When">When, where it is known.</param>
public sealed record RedetectEarlier(RedetectEarlierKind Kind, DateTimeOffset? When);

/// <summary>A spot of the photo with an earlier answer about it.</summary>
/// <param name="Spot">Where, as a hold that is never stored.</param>
/// <param name="Earlier">What was said there.</param>
public sealed record RedetectTurnedDown(Hold Spot, RedetectEarlier Earlier);

/// <summary>What a matched hold takes from its detection.</summary>
public enum RedetectShapeChange
{
    /// <summary>The detection's position, radius and outline replace the stored ones.</summary>
    Replace = 0,

    /// <summary>A hand-placed hold without an outline keeps its position and radius and gets the detection's outline.</summary>
    FillOutline = 1,
}

/// <summary>A stored hold that takes its shape from a detection.</summary>
/// <param name="HoldId">The stored hold; its identity stays.</param>
/// <param name="Detection">Index of the detection.</param>
/// <param name="Change">What it takes.</param>
public sealed record RedetectShapeUpdate(Guid HoldId, int Detection, RedetectShapeChange Change);

/// <summary>Two stored holds that are the same hold and become one.</summary>
/// <param name="KeptId">The hold that stays.</param>
/// <param name="RemovedId">The hold that goes; its boulders move to the kept one.</param>
/// <param name="Score">How sure, 0..1.</param>
public sealed record RedetectMerge(Guid KeptId, Guid RemovedId, double Score);

/// <summary>One stored hold where the detector sees several holds.</summary>
/// <param name="HoldId">The stored hold.</param>
/// <param name="Detections">Indexes of the detections inside it, the one covering most of it first.</param>
public sealed record RedetectSplit(Guid HoldId, IReadOnlyList<int> Detections);

/// <summary>Everything one re-detect decided, before anything is written.</summary>
/// <param name="Shapes">Stored holds that take a shape from their detection.</param>
/// <param name="NewDetections">Indexes of the detections that become new holds.</param>
/// <param name="Merges">Stored pairs merged without asking.</param>
/// <param name="ForReview">Stored pairs that look alike but are only left for the duplicate review.</param>
/// <param name="Removed">Automatic holds that were not found again and that nothing depends on.</param>
/// <param name="HandShapesKept">Matched holds whose hand-made shape stays as it is.</param>
/// <param name="SkippedDetections">Detections on a spot that is taken, which are not added.</param>
public sealed record RedetectDecisions(
    IReadOnlyList<RedetectShapeUpdate> Shapes,
    IReadOnlyList<int> NewDetections,
    IReadOnlyList<RedetectMerge> Merges,
    IReadOnlyList<(Guid A, Guid B)> ForReview,
    IReadOnlyList<Guid> Removed,
    int HandShapesKept,
    int SkippedDetections)
{
    /// <summary>Gets the hand-made outlines the detector offers another shape for; nothing changes unless a person takes one.</summary>
    public IReadOnlyList<RedetectShapeUpdate> HandOffers { get; init; } = [];

    /// <summary>
    /// Gets the stored pairs that share one detection and are likely the same hold recorded twice, without being sure
    /// enough to merge unasked: only ever a proposal.
    /// </summary>
    public IReadOnlyList<RedetectMerge> LikelyMerges { get; init; } = [];

    /// <summary>Gets the stored holds the detector sees as several holds; their detections are in none of the other lists.</summary>
    public IReadOnlyList<RedetectSplit> Splits { get; init; } = [];

    /// <summary>
    /// Gets the earlier answers about the spots of new detections, by detection: "Not a hold", set aside by an update, or
    /// a deleted hold. Such a detection is still proposed; its card says what was answered there before.
    /// </summary>
    public IReadOnlyDictionary<int, RedetectEarlier> Earlier { get; init; } = new Dictionary<int, RedetectEarlier>();

    /// <summary>Gets the number of new detections on a spot with an earlier answer (<see cref="Earlier"/>).</summary>
    public int TurnedDown { get; init; }

    /// <summary>
    /// Gets the number of detections on an empty spot that were left out because nothing could be traced there: only a
    /// circle or a rough shape. A crash mat, bare wood or a shadow gives such a detection; a hold gives an outline.
    /// </summary>
    public int Untraced { get; init; }

    /// <summary>Gets a value indicating whether anything would be written.</summary>
    public bool Any => Shapes.Count + NewDetections.Count + Merges.Count + Removed.Count > 0;
}

/// <summary>What the planner works on.</summary>
/// <param name="Existing">The panel's stored holds.</param>
/// <param name="Detections">The fresh detections.</param>
/// <param name="Dismissed">Pairs a person said are not duplicates (<see cref="Detection.Outlines.HoldMergeRules.PairKey"/>).</param>
/// <param name="CanMerge">False when the person may not merge holds: sure pairs are then only left for the review.</param>
public sealed record RedetectInput(
    IReadOnlyList<RedetectExisting> Existing,
    IReadOnlyList<RedetectDetection> Detections,
    IReadOnlySet<(Guid, Guid)> Dismissed,
    bool CanMerge)
{
    /// <summary>
    /// Gets the spots on this photo with an earlier answer: a detection answered "Not a hold" or still waiting in the
    /// look-over, and holds a person deleted. Those answers were often wrong, so a detection there is proposed like
    /// any other; its card says what was answered.
    /// </summary>
    public IReadOnlyList<RedetectTurnedDown> TurnedDown { get; init; } = [];

    /// <summary>
    /// Gets the holds a person moved or resized by hand without that being flagged on the hold: they count as hand-made.
    /// </summary>
    public IReadOnlySet<Guid> HandEdited { get; init; } = new HashSet<Guid>();

    /// <summary>Gets the printed markers on the photo, corners in photo fractions: a detection on one is never a hold.</summary>
    public IReadOnlyList<IReadOnlyList<(double X, double Y)>> Markers { get; init; } = [];
}
