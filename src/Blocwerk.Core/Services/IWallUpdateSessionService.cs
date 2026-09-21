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

    /// <summary>
    /// Upserts the verdict for ONE old live hold. The as-you-go save of the carryover stepper.
    /// <para>
    /// <see cref="CarryoverDecision.Confirmed"/> is the human-confirmation intent: true records that a
    /// person signed this verdict off (who and when), re-confirming an unchanged verdict is a no-op, and
    /// false leaves an existing confirmation alone unless this write changes the verdict — see
    /// <see cref="CarryConfirmationPolicy"/> for the full rule.
    /// </para>
    /// </summary>
    Task SaveCarryDecisionAsync(Guid wallId, CarryoverDecision decision);

    /// <summary>
    /// Who has confirmed which old-hold carry verdicts on the wall's open session, for the review to show
    /// "reviewed by X" and for two admins working the same session not to duplicate each other. Only
    /// CONFIRMED verdicts appear; a seeded or matcher-default one is simply absent. Empty when no session
    /// is open. The boolean alone is already on every <see cref="CarryoverDecision"/> from
    /// <see cref="GetDecisionsAsync"/> — call this only when the attribution is actually displayed.
    /// </summary>
    Task<IReadOnlyList<CarryConfirmation>> GetCarryConfirmationsAsync(Guid wallId);

    /// <summary>
    /// Drops the human confirmation from ONE old hold's verdict, leaving the verdict itself untouched.
    /// The "un-review this" action: the only way to clear a confirmation without changing the verdict,
    /// since a write that re-states the same verdict deliberately preserves it.
    /// </summary>
    Task ClearCarryConfirmationAsync(Guid wallId, Guid oldHoldId);

    /// <summary>Upserts the keep/discard verdict for ONE staged centre hold with no old twin.</summary>
    Task SaveNewCentreHoldDecisionAsync(Guid wallId, Guid stagedHoldId, bool discarded);

    /// <summary>
    /// Replaces the whole carryover half of the session in one write: every carry verdict plus the
    /// kept/discarded new centre holds. The bulk save for leaving the carryover step; the per-hold
    /// upserts above cover the keystroke-by-keystroke case. Ids whose hold no longer exists (another
    /// admin deleted the staged row) are dropped rather than throwing.
    /// <para>
    /// Rewriting the half does NOT wipe the review: a verdict that comes back unchanged keeps whatever
    /// human confirmation it had, and one whose verdict has CHANGED loses it (the sign-off was about the
    /// verdict that went away).
    /// </para>
    /// <para>
    /// This save never CONFIRMS. <see cref="CarryoverDecision.Confirmed"/> on this path is a stale echo
    /// of a read, not an intent — the caller ships back the whole in-memory carryover, which it seeded
    /// from <see cref="GetDecisionsAsync"/> — so an incoming true is ignored and the stored confirmation
    /// (or its absence) stands. Deliberate confirmations go through
    /// <see cref="SaveCarryDecisionAsync"/>, one hold at a time, as they are made.
    /// </para>
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
