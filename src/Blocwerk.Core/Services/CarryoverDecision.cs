using Blocwerk.Core.Enums;

namespace Blocwerk.Core.Services;

/// <summary>
/// The user's decision for one old live hold when a big-wall update is promoted.
/// </summary>
/// <param name="OldHoldId">The old live hold this decision is about.</param>
/// <param name="Kind">Whether the hold is carried, carried-but-changed, or removed.</param>
/// <param name="NewHoldId">
/// For <see cref="CarryKind.Carried"/>/<see cref="CarryKind.Changed"/>, the staged new-centre hold whose
/// position the old hold takes over (then consumed). Null for <see cref="CarryKind.Removed"/>.
/// </param>
/// <param name="Confirmed">
/// Whether a human deliberately signed this verdict off, as opposed to it being the matcher default the
/// review seeds for every old hold. Defaults to FALSE, so every seed, every matcher suggestion and every
/// construction that does not mean "a person just decided this" reads as unreviewed.
/// <para>
/// On the way IN to <see cref="IWallUpdateSessionService.SaveCarryDecisionAsync"/> it is an intent: true
/// confirms, false leaves an existing confirmation alone unless the verdict itself changes (see
/// <see cref="CarryConfirmationPolicy"/>). On the way OUT of
/// <see cref="IWallUpdateSessionService.GetDecisionsAsync"/> it is the persisted fact — which is why
/// <see cref="IWallUpdateSessionService.SaveCarryOutcomeAsync"/>, whose payload is a round trip of that
/// read, ignores an incoming true rather than re-confirming somebody else's cleared sign-off. The
/// promote ignores it entirely — it is review metadata, never an input to what happens to the hold.
/// </para>
/// </param>
public record CarryoverDecision(Guid OldHoldId, CarryKind Kind, Guid? NewHoldId, bool Confirmed = false);
