namespace Blocwerk.Core.Services;

/// <summary>
/// The "big wall update" lifecycle: replacing a wall's photo with a fresh multi-image capture while
/// carrying the old, curated holds over onto the new centre photo so their boulders survive, and
/// linking the overlaps between the new panels. Stage → Resume (match) → Promote / Discard. Every mutation
/// is gated by <see cref="WallAdminGuard"/>.
/// </summary>
public interface IWallBigUpdateService
{
    /// <summary>
    /// Begins an update: discards any prior in-flight update for this wall (idempotent restart), stages
    /// a centre panel plus one panel per neighbour photo and detects holds on each — and stops there.
    /// NO matching runs yet: the returned session is the pre-match staged state (centre + neighbour
    /// panel ids, no proposals), so the user can correct the detection first; <see cref="ResumeAsync"/>
    /// then runs the matcher over the corrected rows. The staged set must be closed toward the centre
    /// (0,0) — a non-centre panel may only be re-photographed together with the panel one step toward
    /// the centre (center-first, decision D-D) — which also means a valid update always includes the centre.
    /// </summary>
    Task<BigUpdateSession> StageAsync(Guid wallId, IReadOnlyList<BigUpdatePhoto> photos);

    /// <summary>
    /// The pre-match staged state of an in-flight update, read back from the DB without detecting or
    /// matching anything: the staged centre plus every staged neighbour panel, with no proposals.
    /// Throws when no update is staged, so it doubles as the cheap "is an update in flight?" probe.
    /// </summary>
    Task<BigUpdateSession> GetStagedAsync(Guid wallId);

    /// <summary>
    /// Runs the matching over the already-persisted staged panels and holds (no new detection): the
    /// old-vs-centre carryover and the neighbour overlaps. Called once after staging to move from the
    /// pre-match review into the carryover review, and again to pick up an interrupted update — both
    /// see whatever hold corrections have been made since. Throws when no update is staged.
    /// </summary>
    Task<BigUpdateSession> ResumeAsync(Guid wallId);

    /// <summary>
    /// Promotes the staged update to live in one transaction: archives the outgoing photo, applies the
    /// carryover decisions in place on the old hold rows (preserving their identity and boulders),
    /// keeps/deletes the new-centre holds, brings every panel live, and persists the neighbour hold
    /// links (remapping any link that referenced a consumed centre hold onto the old hold that absorbed it).
    /// </summary>
    Task PromoteAsync(Guid wallId, BigUpdateConfirmation confirmation);

    /// <summary>
    /// Abandons the in-flight update: deletes every update-staged panel and its staged holds, leaving
    /// the live wall untouched.
    /// </summary>
    Task DiscardAsync(Guid wallId);
}
