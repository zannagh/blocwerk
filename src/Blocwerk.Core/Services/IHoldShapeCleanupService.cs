namespace Blocwerk.Core.Services;

/// <summary>
/// The wall-admin action that re-runs the shape clean-up over a wall's EXISTING automatically traced hold
/// outlines: smooths jagged ones (or turns them back into circles) and removes overlaps between holds on the
/// same photo. Hand-drawn shapes and manually placed holds are never touched and always win. Owner / wall
/// admin only, never from a kiosk tablet. Every apply is ONE change-journal batch, so it is exactly revertable.
/// </summary>
public interface IHoldShapeCleanupService
{
    /// <summary>The wall's latest clean-up that can still be undone (its journal batch is not reverted).</summary>
    /// <param name="wallId">The wall.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The status.</returns>
    Task<HoldShapeCleanupStatus> GetStatusAsync(Guid wallId, CancellationToken ct = default);

    /// <summary>Computes what <see cref="ApplyAsync"/> would change, writing nothing.</summary>
    /// <param name="wallId">The wall.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The counts (no batch id).</returns>
    Task<HoldShapeCleanupSummary> PreviewAsync(Guid wallId, CancellationToken ct = default);

    /// <summary>Writes the clean-up as one journal batch. Idempotent: a second run finds nothing left to change.</summary>
    /// <param name="wallId">The wall.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The counts and the journal batch id (null when nothing changed).</returns>
    Task<HoldShapeCleanupSummary> ApplyAsync(Guid wallId, CancellationToken ct = default);

    /// <summary>Reverts a clean-up batch exactly; refuses (and changes nothing) when a hold was edited since.</summary>
    /// <param name="wallId">The wall.</param>
    /// <param name="batchId">The batch id <see cref="ApplyAsync"/> returned.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The journal's revert result.</returns>
    Task<ChangeJournalRevertResult> RevertAsync(Guid wallId, Guid batchId, CancellationToken ct = default);
}

/// <summary>What a clean-up found or did.</summary>
/// <param name="BatchId">The journal batch (revert with it), or null for a preview / no change.</param>
/// <param name="Photos">Photos looked at.</param>
/// <param name="AutoShapes">Automatically traced outlines in scope.</param>
/// <param name="Smoothed">Outlines smoothed in place.</param>
/// <param name="Clipped">Outlines clipped or shrunk to clear a neighbour.</param>
/// <param name="BackToCircle">Outlines replaced by the plain circle (jagged, or no room).</param>
/// <param name="ShrunkCircle">Of those, circles whose radius was reduced.</param>
/// <param name="StillOverlapping">Minimum-size circles that still overlap a locked hold.</param>
/// <param name="LockedHolds">Manual / hand-drawn / virtual holds that were left alone (they only act as obstacles).</param>
public sealed record HoldShapeCleanupSummary(
    Guid? BatchId,
    int Photos,
    int AutoShapes,
    int Smoothed,
    int Clipped,
    int BackToCircle,
    int ShrunkCircle,
    int StillOverlapping,
    int LockedHolds);

/// <summary>Whether a clean-up can be undone.</summary>
/// <param name="RevertableBatchId">The latest not-yet-reverted clean-up batch, or null.</param>
/// <param name="CreatedAt">When it ran.</param>
public sealed record HoldShapeCleanupStatus(Guid? RevertableBatchId, DateTimeOffset? CreatedAt);
