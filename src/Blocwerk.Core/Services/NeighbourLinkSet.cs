namespace Blocwerk.Core.Services;

/// <summary>
/// The confirmed overlap outcome for one non-centre big-wall update panel.
/// </summary>
/// <param name="PanelId">The staged non-centre panel.</param>
/// <param name="Links">
/// Confirmed centre ↔ this-panel correspondences to persist as <see cref="Blocwerk.Core.Entities.HoldLink"/>s.
/// Each link's <c>NeighborHoldId</c> is a staged centre hold and its <c>NewHoldId</c> is a hold on this panel.
/// </param>
/// <param name="RemovedNeighbourHoldIds">This panel's holds the user marked as physically absent.</param>
public record NeighbourLinkSet(
    Guid PanelId,
    List<ConfirmedLink> Links,
    List<Guid> RemovedNeighbourHoldIds);
