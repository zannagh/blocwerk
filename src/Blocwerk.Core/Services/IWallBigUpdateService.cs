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
    /// Begins an update: stages
    /// a centre panel plus one panel per neighbour photo and detects holds on each — and stops there.
    /// NO matching runs yet: the returned session is the pre-match staged state (centre + neighbour
    /// panel ids, no proposals), so the user can correct the detection first; <see cref="ResumeAsync"/>
    /// then runs the matcher over the corrected rows. The staged set must be closed toward the centre
    /// (0,0) — a non-centre panel may only be re-photographed together with the panel one step toward
    /// the centre (center-first, decision D-D) — which also means a valid update always includes the centre.
    /// <para>
    /// REFUSES when the wall already has an OPEN <see cref="Entities.WallUpdateSession"/>, throwing a
    /// <see cref="WallUpdateSessionConflictException"/> that names who started it and when — the caller
    /// is expected to offer resume-or-discard. Pass <paramref name="takeOverExisting"/> only once the
    /// user has explicitly chosen to throw that work away; the superseded session is then marked
    /// discarded and its change-journal batch sealed. Staged residue with NO session row (an update from
    /// before sessions existed) is still cleared unconditionally, as the old idempotent restart did.
    /// </para>
    /// </summary>
    /// <param name="wallId">The wall to update.</param>
    /// <param name="photos">The fresh capture, one photo per grid position.</param>
    /// <param name="takeOverExisting">
    /// True to abandon another admin's in-flight update and start fresh. Default false: refuse.
    /// </param>
    Task<BigUpdateSession> StageAsync(
        Guid wallId, IReadOnlyList<BigUpdatePhoto> photos, bool takeOverExisting = false);

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
    /// <para>
    /// A RESUMED run therefore matches against staged geometry the user has since touched up, where the
    /// first run matched the raw detections. That is deliberate and it is the MORE correct pass: touch-up
    /// only makes the new-image hold set truer (holds the detector missed, false ones deleted, positions
    /// corrected), and the warp field is estimated from those correspondences, so a later pass fits a
    /// better one. The resume is not "re-deriving stale state" — if anything the uninterrupted run is the
    /// one working from a coarser fit. What must NOT depend on which pass ran is the carryover payload,
    /// which is why <c>CollectCarryover</c> records a warp position for every predictable old hold rather
    /// than only for the ones this pass left unmatched (see the note there).
    /// </para>
    /// </summary>
    Task<BigUpdateSession> ResumeAsync(Guid wallId);

    /// <summary>
    /// Promotes the staged update to live in one transaction: archives the outgoing photo, applies the
    /// carryover decisions in place on the old hold rows (preserving their identity and boulders),
    /// keeps/deletes the new-centre holds, brings every panel live, and persists the neighbour hold
    /// links (remapping any link that referenced a consumed centre hold onto the old hold that absorbed it).
    /// </summary>
    /// <param name="wallId">The wall to promote.</param>
    /// <param name="confirmation">What the user decided, plus the matcher's warp dictionaries.</param>
    /// <param name="expectedSessionId">
    /// The <see cref="Entities.WallUpdateSession"/> the caller believes it is finishing. REFUSES with a
    /// <see cref="WallUpdateSessionSupersededException"/> when that is not the wall's currently-open
    /// session, so a stale circuit cannot promote a LATER admin's staged photos under its own decisions.
    /// Null only for a legacy staged update that has no session row (and for service-level tests).
    /// </param>
    Task PromoteAsync(Guid wallId, BigUpdateConfirmation confirmation, Guid? expectedSessionId = null);

    /// <summary>
    /// Abandons the in-flight update: deletes every update-staged panel and its staged holds, leaving
    /// the live wall untouched. Takes the same <paramref name="expectedSessionId"/> guard as
    /// <see cref="PromoteAsync"/> — a stale Discard must not destroy the update that replaced it.
    /// </summary>
    Task DiscardAsync(Guid wallId, Guid? expectedSessionId = null);
}
