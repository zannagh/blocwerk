namespace Blocwerk.Core.Services;

/// <summary>
/// The outcome of staging a new big-wall panel: the created panel plus the overlap
/// proposals against every adjacent live neighbour, for the user to confirm.
/// </summary>
/// <param name="PanelId">The newly created (staged) panel's id.</param>
/// <param name="Proposals">Proposed hold correspondences across all adjacent live neighbours.</param>
/// <param name="UnalignedNeighbourIds">
/// The adjacent live neighbours the matcher could not line the new photo up with, so no proposal exists for
/// them for that reason (rather than because nothing overlaps). Null or empty when every neighbour aligned.
/// </param>
public record StagePanelResult(
    Guid PanelId,
    List<OverlapProposalDto> Proposals,
    IReadOnlyList<Guid>? UnalignedNeighbourIds = null);
