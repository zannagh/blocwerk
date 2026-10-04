namespace Blocwerk.Core.Services;

/// <summary>
/// The full set of user decisions promoting an in-flight big-wall update to live.
/// </summary>
/// <param name="Carryover">One decision per old live hold (carried / moved / removed).</param>
/// <param name="AcceptedNewCenterHoldIds">Staged centre holds to keep as genuinely new live holds.</param>
/// <param name="RemovedNewCenterHoldIds">Staged centre holds to discard.</param>
/// <param name="Neighbours">Per non-centre panel, its confirmed links and removed holds.</param>
/// <param name="CarriedWarpPositions">
/// The matcher's warp-predicted new-image position (new-image normalized) for each old hold with no
/// proposal, carried straight from <see cref="BigUpdateSession.CarriedWarpPositions"/>. Promote clones
/// an unmatched carried hold at its warped position instead of its stale old coordinates. Null/empty
/// falls back to the old-position clone for every carried hold, exactly as before.
/// </param>
/// <param name="CarriedWarpShapes">
/// The matcher's warp-predicted new-image OUTLINE (new-image normalized, absolute vertices) for each old
/// hold that had a custom polygon, carried straight from <see cref="BigUpdateSession.CarriedWarpShapes"/>.
/// Promote sets the successor hold's <see cref="Blocwerk.Core.Entities.Hold.ShapePoints"/> to this warped
/// polygon (for matched twins and clones alike). Null/empty leaves the successor's shape as the clone
/// copied it (the old outline), exactly as before.
/// </param>
/// <param name="ReviewOldHoldIds">
/// Old holds whose successor is marked <see cref="Blocwerk.Core.Entities.Hold.NeedsReview"/>: the confirm screen asked about
/// them and nobody answered (an old hold possibly removed, an unsure match). Null/empty flags nothing.
/// </param>
/// <param name="HandPlacedMergeOldIds">
/// Old hand-placed or virtual holds merged onto a detection: the successor adopts the detection's outline instead of the
/// old hold's warped one. Null/empty changes nothing.
/// </param>
/// <param name="ExpectedMovesVersion">
/// The <see cref="HoldMoves.HoldMovePlan.Version"/> the user confirmed. When set, the promote refuses if the moves it
/// derives now differ, so it only ever does what was shown. Null skips the check (wizard, tests).
/// </param>
public record BigUpdateConfirmation(
    List<CarryoverDecision> Carryover,
    List<Guid> AcceptedNewCenterHoldIds,
    List<Guid> RemovedNewCenterHoldIds,
    List<NeighbourLinkSet> Neighbours,
    IReadOnlyDictionary<Guid, HoldPositionNorm>? CarriedWarpPositions = null,
    IReadOnlyDictionary<Guid, IReadOnlyList<HoldPositionNorm>>? CarriedWarpShapes = null,
    IReadOnlyCollection<Guid>? ReviewOldHoldIds = null,
    IReadOnlyCollection<Guid>? HandPlacedMergeOldIds = null,
    string? ExpectedMovesVersion = null);
