// <copyright file="UpdateReviewFacts.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;

namespace Blocwerk.Core.Refresh.Review;

/// <summary>Everything the one review is worked out from, read from the update session in one go.</summary>
/// <param name="Panels">The staged panels, centre first.</param>
/// <param name="CentrePanelId">The staged centre panel.</param>
/// <param name="Cards">The confirm screen's cards.</param>
/// <param name="Carry">The carry verdict per old hold.</param>
/// <param name="NewCentre">The keep/discard verdict per staged centre detection (true: discarded).</param>
/// <param name="NeighbourRemoved">The neighbour detections left out.</param>
/// <param name="NeighbourLinked">The neighbour detections linked to a centre hold (the same hold on two photos).</param>
/// <param name="Staged">The staged detections.</param>
/// <param name="OldHolds">The old holds the carry verdicts are about.</param>
/// <param name="Boulders">The names of the (not historic) boulders per old hold.</param>
/// <param name="Answers">The people's review answers.</param>
/// <param name="Relocations">The "this hold may have moved" suggestions.</param>
/// <param name="PhotoSizes">Each staged panel's photo size in pixels.</param>
/// <param name="UserNames">Display names of the people who answered.</param>
/// <param name="Spots">Where the matcher puts an old hold on the new photo, when it is known.</param>
/// <param name="Moves">The summary's measured moves: the suspected ones are asked about, the confirmed ones shown answered.</param>
internal sealed record UpdateReviewFacts(
    IReadOnlyList<ReviewPanel> Panels,
    Guid CentrePanelId,
    IReadOnlyList<WallUpdateException> Cards,
    IReadOnlyDictionary<Guid, CarryFact> Carry,
    IReadOnlyDictionary<Guid, bool> NewCentre,
    IReadOnlySet<Guid> NeighbourRemoved,
    IReadOnlySet<Guid> NeighbourLinked,
    IReadOnlyList<StagedFact> Staged,
    IReadOnlyDictionary<Guid, OldHoldFact> OldHolds,
    IReadOnlyDictionary<Guid, IReadOnlyList<string>> Boulders,
    IReadOnlyDictionary<(Guid HoldId, ReviewSubject Subject), WallUpdateReviewAnswer> Answers,
    IReadOnlyList<RelocationSuggestion> Relocations,
    IReadOnlyDictionary<Guid, (int Width, int Height)> PhotoSizes,
    IReadOnlyDictionary<Guid, string> UserNames,
    IReadOnlyDictionary<Guid, HoldPositionNorm>? Spots = null,
    IReadOnlyList<MovedHoldLine>? Moves = null);

/// <summary>One old hold's carry verdict.</summary>
internal sealed record CarryFact(CarryKind Kind, Guid? PairedHoldId, bool Confirmed, Guid? ConfirmedByUserId);

/// <summary>One staged detection.</summary>
internal sealed record StagedFact(
    Guid Id, Guid PanelId, double X, double Y, double Radius, double? Confidence, string? DiscardReason,
    IReadOnlyList<ShapePoint>? ShapePoints, bool IsVirtual);

/// <summary>One old hold: its name, and the staged panel at its position (null when that position was not re-shot).</summary>
internal sealed record OldHoldFact(Guid Id, string? Name, Guid? StagedPanelId, bool OnCentre);
