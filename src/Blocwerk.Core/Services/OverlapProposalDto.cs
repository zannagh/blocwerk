namespace Blocwerk.Core.Services;

/// <summary>
/// A proposed correspondence between a hold on an existing (live) neighbour panel and a hold
/// on the newly staged panel, carrying real <see cref="Blocwerk.Core.Entities.Hold"/> ids so the
/// UI can confirm it into a <see cref="Blocwerk.Core.Entities.HoldLink"/>.
/// </summary>
/// <param name="NeighborPanelId">The live neighbour panel the match was computed against.</param>
/// <param name="HoldAId">The neighbour panel's hold (link end A).</param>
/// <param name="HoldBId">The newly staged panel's hold (link end B).</param>
/// <param name="Confidence">Matcher confidence 0..1.</param>
/// <param name="Moved">True when the geometric residual suggests the hold physically moved.</param>
/// <param name="ResidualPx">Warp-field prediction error, in the new panel's pixels.</param>
public record OverlapProposalDto(
    Guid NeighborPanelId,
    Guid HoldAId,
    Guid HoldBId,
    double Confidence,
    bool Moved,
    double ResidualPx);
