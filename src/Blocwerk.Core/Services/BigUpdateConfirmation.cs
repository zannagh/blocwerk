namespace Blocwerk.Core.Services;

/// <summary>
/// The full set of user decisions promoting an in-flight big-wall update to live.
/// </summary>
/// <param name="Carryover">One decision per old live hold (carried / moved / removed).</param>
/// <param name="AcceptedNewCenterHoldIds">Staged centre holds to keep as genuinely new live holds.</param>
/// <param name="RemovedNewCenterHoldIds">Staged centre holds to discard.</param>
/// <param name="Neighbours">Per non-centre panel, its confirmed links and removed holds.</param>
public record BigUpdateConfirmation(
    List<CarryoverDecision> Carryover,
    List<Guid> AcceptedNewCenterHoldIds,
    List<Guid> RemovedNewCenterHoldIds,
    List<NeighbourLinkSet> Neighbours);
