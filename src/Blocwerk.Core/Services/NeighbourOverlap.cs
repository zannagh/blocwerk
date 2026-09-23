namespace Blocwerk.Core.Services;

/// <summary>
/// The overlap proposals between one non-centre big-wall update panel and the staged centre panel.
/// Each proposal's <see cref="OverlapProposalDto.HoldAId"/> is a staged centre hold and its
/// <see cref="OverlapProposalDto.HoldBId"/> is a hold on this panel.
/// </summary>
/// <param name="PanelId">The staged non-centre panel.</param>
/// <param name="Col">The panel's grid column.</param>
/// <param name="Row">The panel's grid row.</param>
/// <param name="Proposals">Proposed centre-hold ↔ this-panel-hold correspondences.</param>
/// <param name="AlignmentFailed">
/// True when the matcher could not line this panel up with the staged centre, so <paramref name="Proposals"/>
/// is empty for that reason rather than because nothing overlaps. Sticky for the session.
/// </param>
public record NeighbourOverlap(
    Guid PanelId,
    int Col,
    int Row,
    List<OverlapProposalDto> Proposals,
    bool AlignmentFailed = false);
