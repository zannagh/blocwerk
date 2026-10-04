namespace Blocwerk.Core.Services;

/// <summary>
/// The wall-admin action that re-runs the shape clean-up over a wall's EXISTING automatic holds: smooths jagged
/// outlines (or turns them back into circles) and removes overlaps between holds on the same photo, shrinking
/// plain auto circles just enough where needed. Hand-drawn shapes and manually placed holds are never touched and
/// always win. Owner / wall admin only, never from a kiosk tablet. Every apply is ONE change-journal batch, so it
/// is exactly revertable, and only one apply or undo runs per wall at a time.
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
    /// <param name="progress">Reports finished photos.</param>
    /// <returns>The counts (no batch id) and the plan version to pass to <see cref="ApplyAsync"/>.</returns>
    Task<HoldShapeCleanupSummary> PreviewAsync(
        Guid wallId, CancellationToken ct = default, IProgress<HoldShapeCleanupProgress>? progress = null);

    /// <summary>
    /// Writes the clean-up as one journal batch. Idempotent: a second run finds nothing left to change. With
    /// <paramref name="expectedVersion"/> (from the preview) it writes exactly the previewed plan, and refuses when
    /// the holds changed since.
    /// </summary>
    /// <param name="wallId">The wall.</param>
    /// <param name="ct">Cancellation.</param>
    /// <param name="expectedVersion">The preview's plan version, or null to apply whatever the plan is now.</param>
    /// <param name="progress">Reports finished photos.</param>
    /// <returns>The counts and the journal batch id (null when nothing changed).</returns>
    Task<HoldShapeCleanupSummary> ApplyAsync(
        Guid wallId,
        CancellationToken ct = default,
        string? expectedVersion = null,
        IProgress<HoldShapeCleanupProgress>? progress = null);

    /// <summary>Reverts a clean-up batch exactly; refuses (and changes nothing) when a hold was edited since.</summary>
    /// <param name="wallId">The wall.</param>
    /// <param name="batchId">The batch id <see cref="ApplyAsync"/> returned.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The journal's revert result.</returns>
    Task<ChangeJournalRevertResult> RevertAsync(Guid wallId, Guid batchId, CancellationToken ct = default);
}

/// <summary>Planning progress: photos finished of all live photos.</summary>
/// <param name="Done">Photos finished.</param>
/// <param name="Total">All live photos.</param>
public readonly record struct HoldShapeCleanupProgress(int Done, int Total);

/// <summary>Two overlapping holds the clean-up may not change (they need a hand edit).</summary>
/// <param name="HoldA">First hold.</param>
/// <param name="NameA">Its name, if any.</param>
/// <param name="HoldB">Second hold.</param>
/// <param name="NameB">Its name, if any.</param>
/// <param name="X">Centre X of the first hold (normalized, on its photo).</param>
/// <param name="Y">Centre Y of the first hold.</param>
/// <param name="PanelId">The panel photo they sit on (null for a legacy single photo).</param>
public sealed record HoldShapeOverlapPair(Guid HoldA, string? NameA, Guid HoldB, string? NameB, double X, double Y, Guid? PanelId);

/// <summary>What a clean-up found or did.</summary>
/// <param name="BatchId">The journal batch (revert with it), or null for a preview / no change.</param>
/// <param name="PlanVersion">Identifies the holds this plan was made from; pass it to apply.</param>
/// <param name="Photos">Photos looked at.</param>
/// <param name="AutoShapes">Automatically traced outlines in scope.</param>
/// <param name="AutoCircles">Automatic plain circles in scope (they only ever shrink).</param>
/// <param name="Smoothed">Outlines smoothed in place.</param>
/// <param name="Clipped">Outlines clipped or shrunk to clear a neighbour.</param>
/// <param name="BackToCircle">Outlines replaced by the plain circle at its original radius.</param>
/// <param name="ShrunkCircle">Circles (new or old) shrunk to the largest radius that clears.</param>
/// <param name="StillOverlapping">Automatic holds that cannot be cleared even at the smallest sane size (kept as they were).</param>
/// <param name="LockedHolds">Manual / hand-drawn / virtual holds left alone (they only act as obstacles).</param>
/// <param name="ManualOverlapCount">Overlapping pairs that involve only such holds (they need a hand edit).</param>
/// <param name="ManualOverlaps">The first of those pairs, for listing.</param>
public sealed record HoldShapeCleanupSummary(
    Guid? BatchId,
    string PlanVersion,
    int Photos,
    int AutoShapes,
    int AutoCircles,
    int Smoothed,
    int Clipped,
    int BackToCircle,
    int ShrunkCircle,
    int StillOverlapping,
    int LockedHolds,
    int ManualOverlapCount,
    IReadOnlyList<HoldShapeOverlapPair> ManualOverlaps)
{
    /// <summary>Gets the number of holds that change.</summary>
    public int Changed => Smoothed + Clipped + BackToCircle + ShrunkCircle;
}

/// <summary>Whether a clean-up can be undone.</summary>
/// <param name="RevertableBatchId">The latest not-yet-reverted clean-up batch, or null.</param>
/// <param name="CreatedAt">When it ran.</param>
public sealed record HoldShapeCleanupStatus(Guid? RevertableBatchId, DateTimeOffset? CreatedAt);
