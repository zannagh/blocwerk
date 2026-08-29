namespace Blocwerk.Core.Services;

/// <summary>
/// The outcome of staging a new big-wall panel: the created panel plus the overlap
/// proposals against every adjacent live neighbour, for the user to confirm.
/// </summary>
/// <param name="PanelId">The newly created (staged) panel's id.</param>
/// <param name="Proposals">Proposed hold correspondences across all adjacent live neighbours.</param>
public record StagePanelResult(Guid PanelId, List<OverlapProposalDto> Proposals);
