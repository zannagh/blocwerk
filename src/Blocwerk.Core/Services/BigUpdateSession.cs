using Blocwerk.Core.Enums;

namespace Blocwerk.Core.Services;

/// <summary>
/// The reviewable state of an in-flight big-wall update: the old→new-centre carryover proposals,
/// the old holds with no match (removal candidates), the staged centre holds with no old match
/// (genuinely new holds), and the overlap proposals for every non-centre panel. All ids are real
/// <see cref="Blocwerk.Core.Entities.Hold"/> / <see cref="Blocwerk.Core.Entities.WallPanel"/> ids.
/// </summary>
/// <param name="WallId">The wall being updated.</param>
/// <param name="CenterPanelId">The staged centre panel (0,0).</param>
/// <param name="Carryover">Old live hold → staged new-centre hold match proposals.</param>
/// <param name="RemovedCandidateHoldIds">Old live holds the matcher found no new-centre twin for.</param>
/// <param name="NewCenterHoldIds">Staged centre holds the matcher found no old twin for.</param>
/// <param name="Neighbours">Per non-centre panel, its overlap proposals against the centre.</param>
/// <param name="AutoMatchStatus">
/// Whether the optional OpenCV carryover auto-matching pass ran, could not load, or failed. The
/// session is fully populated regardless (carry-all needs no matcher); this drives the review banner.
/// </param>
/// <param name="AutoMatchMessage">Optional human-readable detail for a non-<see cref="Enums.AutoMatchStatus.Ok"/> status.</param>
/// <param name="CarriedWarpPositions">
/// The matcher's warp-predicted new-image position (new-image normalized) for each old hold that got
/// NO carryover proposal. Promote clones those unmatched carried holds at the warped position instead
/// of their stale old coordinates, so a hand-placed hold YOLO never detects still lands on its real
/// hold and its boulder does not need revision. Empty when the matcher did not run.
/// </param>
/// <param name="CarriedWarpShapes">
/// The matcher's warp-predicted new-image OUTLINE (new-image normalized, absolute vertices) for each old
/// hold that HAD a custom polygon and the field could warp — for MATCHED holds too, since a detection
/// never carries the hand-drawn outline. Promote sets the successor's <see cref="Blocwerk.Core.Entities.Hold.ShapePoints"/>
/// to this warped polygon so a custom shape (e.g. a triangular volume) lands correctly on the new photo.
/// Empty when the matcher did not run or no hold had a custom shape.
/// </param>
public record BigUpdateSession(
    Guid WallId,
    Guid CenterPanelId,
    List<CarryoverProposal> Carryover,
    List<Guid> RemovedCandidateHoldIds,
    List<Guid> NewCenterHoldIds,
    List<NeighbourOverlap> Neighbours,
    AutoMatchStatus AutoMatchStatus = AutoMatchStatus.Ok,
    string? AutoMatchMessage = null,
    IReadOnlyDictionary<Guid, HoldPositionNorm>? CarriedWarpPositions = null,
    IReadOnlyDictionary<Guid, IReadOnlyList<HoldPositionNorm>>? CarriedWarpShapes = null);
