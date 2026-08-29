using Blocwerk.Core.Services;

namespace Blocwerk.Web.Components.Shared;

/// <summary>
/// The outcome of the overlap confirmation stepper when the user finishes: the confirmed
/// hold links to persist and the neighbour holds the user marked as physically removed from
/// the wall. Both are applied atomically by <see cref="IWallPanelService.ConfirmPanelAsync"/>;
/// discarding the panel applies neither.
/// </summary>
/// <param name="Links">The confirmed neighbour ↔ new-panel hold links.</param>
/// <param name="RemovedNeighborHoldIds">Ids of neighbour holds to delete from the wall.</param>
public record PanelConfirmation(
    IReadOnlyList<ConfirmedLink> Links,
    IReadOnlyList<Guid> RemovedNeighborHoldIds);
