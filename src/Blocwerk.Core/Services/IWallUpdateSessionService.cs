using Blocwerk.Core.Enums;

namespace Blocwerk.Core.Services;

/// <summary>
/// Persistence for the DECISIONS of an in-flight big-wall update, so the flow survives a lost browser
/// session. The staged photos and staged hold geometry were already durable; everything the user
/// decided about them — the carryover verdicts, the kept/discarded new centre holds, the confirmed
/// neighbour links and removals, and the step they were on — used to live only in the circuit.
/// <para>
/// Decisions are written INCREMENTALLY, as they are made; nothing waits for the Confirm step. Reading
/// them back yields a <see cref="BigUpdateConfirmation"/> ready to hand to
/// <see cref="IWallBigUpdateService.PromoteAsync"/> once the caller has topped it up with the matcher's
/// warp dictionaries (which are deterministic from the staged photos and deliberately not persisted).
/// </para>
/// <para>
/// A session is per WALL, not per user: every method is gated by <see cref="WallAdminGuard"/> exactly as
/// the big-update methods are, and any wall admin may read and continue another's session. Who started
/// it and who touched it last are recorded on the session.
/// </para>
/// </summary>
public interface IWallUpdateSessionService
{
    /// <summary>
    /// The wall's in-flight update session, or null when none is open. The cheap probe the wizard opens
    /// with: it says whether to offer a resume, where to resume, and who has it open.
    /// </summary>
    Task<WallUpdateSessionInfo?> GetOpenSessionAsync(Guid wallId);

    /// <summary>
    /// Moves the resume cursor. Call it on every step transition — it is what makes a reopened wizard
    /// land on the step the user left rather than at the beginning.
    /// </summary>
    /// <param name="wallId">The wall being updated.</param>
    /// <param name="phase">The step now being shown.</param>
    /// <param name="neighbourIndex">
    /// The position in the neighbour-overlap walk. Only meaningful at <see cref="WallUpdatePhase.Neighbours"/>;
    /// pass 0 elsewhere. Throws when no session is open.
    /// </param>
    Task<WallUpdateSessionInfo> SetPhaseAsync(Guid wallId, WallUpdatePhase phase, int neighbourIndex = 0);

    /// <summary>
    /// Everything decided so far, as a promote-ready payload. The warp dictionaries are null: re-run the
    /// matcher (<see cref="IWallBigUpdateService.ResumeAsync"/>) and copy them across before promoting.
    /// Returns an empty confirmation when no session is open.
    /// </summary>
    Task<BigUpdateConfirmation> GetDecisionsAsync(Guid wallId);

    /// <summary>Upserts the verdict for ONE old live hold. The as-you-go save of the carryover stepper.</summary>
    Task SaveCarryDecisionAsync(Guid wallId, CarryoverDecision decision);

    /// <summary>Upserts the keep/discard verdict for ONE staged centre hold with no old twin.</summary>
    Task SaveNewCentreHoldDecisionAsync(Guid wallId, Guid stagedHoldId, bool discarded);

    /// <summary>
    /// Replaces the whole carryover half of the session in one write: every carry verdict plus the
    /// kept/discarded new centre holds. The bulk save for leaving the carryover step; the per-hold
    /// upserts above cover the keystroke-by-keystroke case. Ids whose hold no longer exists (another
    /// admin deleted the staged row) are dropped rather than throwing.
    /// </summary>
    Task SaveCarryOutcomeAsync(
        Guid wallId,
        IReadOnlyList<CarryoverDecision> carryover,
        IReadOnlyList<Guid> acceptedNewCentreHoldIds,
        IReadOnlyList<Guid> removedNewCentreHoldIds);

    /// <summary>
    /// Replaces one staged panel's confirmed overlap outcome — its links and its removed holds. Called
    /// once per panel as the neighbour walk advances; re-confirming a panel overwrites its previous set.
    /// </summary>
    /// <remarks>
    /// This is also how a panel is CLEARED — confirm it with an empty set. There is deliberately no
    /// separate "clear this panel" call: the one the wizard used to make on Skip silently deleted rows
    /// the user had confirmed in an earlier session, and skipping now leaves a decided panel alone.
    /// </remarks>
    Task SaveNeighbourLinkSetAsync(Guid wallId, NeighbourLinkSet linkSet);

}
