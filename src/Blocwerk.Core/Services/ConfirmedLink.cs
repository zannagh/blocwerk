namespace Blocwerk.Core.Services;

/// <summary>
/// A user-confirmed overlap correspondence to persist as a <see cref="Blocwerk.Core.Entities.HoldLink"/>
/// when a staged panel is promoted.
/// </summary>
/// <param name="NeighborHoldId">The live neighbour panel's hold (link end A).</param>
/// <param name="NewHoldId">The newly staged panel's hold (link end B).</param>
/// <param name="Moved">True to record the link as <see cref="Blocwerk.Core.Enums.HoldLinkKind.Moved"/>.</param>
public record ConfirmedLink(Guid NeighborHoldId, Guid NewHoldId, bool Moved);
