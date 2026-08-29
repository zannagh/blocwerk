namespace Blocwerk.Core.Services;

/// <summary>
/// The "big wall update" lifecycle: replacing a wall's photo with a fresh multi-image capture while
/// carrying the old, curated holds over onto the new centre photo so their boulders survive, and
/// linking the overlaps between the new panels. Start → (Resume) → Promote / Discard. Every mutation
/// is gated by <see cref="WallAdminGuard"/>.
/// </summary>
public interface IWallBigUpdateService
{
    /// <summary>
    /// Begins an update: discards any prior in-flight update for this wall (idempotent restart),
    /// stages a centre panel plus one panel per neighbour photo, detects holds on each, matches the
    /// old live holds onto the staged centre (carryover) and every neighbour onto the centre (overlap),
    /// and returns the reviewable session. Requires exactly one (0,0) centre photo.
    /// </summary>
    Task<BigUpdateSession> StartAsync(Guid wallId, IReadOnlyList<BigUpdatePhoto> photos);

    /// <summary>
    /// Rebuilds the session from the already-persisted staged panels and holds (no new detection):
    /// re-runs the old-vs-centre carryover and the neighbour overlaps. Throws when no update is staged.
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
