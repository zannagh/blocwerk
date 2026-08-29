namespace Blocwerk.Core.Services;

/// <summary>
/// A pair of holds recorded as the same physical hold seen across two overlapping big-wall
/// panels (see <see cref="Blocwerk.Core.Entities.HoldLink"/>). Direction is irrelevant here:
/// both ids denote the one physical hold, so callers treat the pair as undirected.
/// </summary>
/// <param name="HoldAId">One hold in the linked pair.</param>
/// <param name="HoldBId">The other hold in the linked pair.</param>
public record HoldLinkPair(Guid HoldAId, Guid HoldBId);
