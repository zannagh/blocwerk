using Blocwerk.Core.Enums;

namespace Blocwerk.Core.Services;

/// <summary>
/// The user's decision for one old live hold when a big-wall update is promoted.
/// </summary>
/// <param name="OldHoldId">The old live hold this decision is about.</param>
/// <param name="Kind">Whether the hold is carried, carried-but-moved, or removed.</param>
/// <param name="NewHoldId">
/// For <see cref="CarryKind.Carried"/>/<see cref="CarryKind.Moved"/>, the staged new-centre hold whose
/// position the old hold takes over (then consumed). Null for <see cref="CarryKind.Removed"/>.
/// </param>
public record CarryoverDecision(Guid OldHoldId, CarryKind Kind, Guid? NewHoldId);
