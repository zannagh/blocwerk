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
public record BigUpdateSession(
    Guid WallId,
    Guid CenterPanelId,
    List<CarryoverProposal> Carryover,
    List<Guid> RemovedCandidateHoldIds,
    List<Guid> NewCenterHoldIds,
    List<NeighbourOverlap> Neighbours);
