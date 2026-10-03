namespace Blocwerk.Core.Services;

/// <summary>
/// The wall-admin action that upgrades a wall's existing circle holds to real outlines on its current
/// photos (every wall, markers not needed). Dry run, apply, and an exact revert of a run. Owner / wall admin
/// only, never from a kiosk tablet; respects the <c>HoldDetection:Outlines:Enabled</c> kill switch.
/// </summary>
/// <remarks>
/// Hold positions and radii are never changed and boulders are never flagged: a better drawing of the same
/// physical hold is not a change to the wall.
/// </remarks>
public interface IHoldOutlineUpgradeService
{
    /// <summary>Whether the action is available here, and the wall's latest run (for "revert").</summary>
    /// <param name="wallId">The wall.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The status.</returns>
    Task<HoldOutlineUpgradeStatus> GetStatusAsync(Guid wallId, CancellationToken ct = default);

    /// <summary>Outlines the wall's circle holds without writing anything and returns what would happen.</summary>
    /// <param name="wallId">The wall.</param>
    /// <param name="options">Scope.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The counts.</returns>
    Task<HoldOutlineUpgradePreview> PreviewAsync(Guid wallId, HoldOutlineUpgradeOptions options, CancellationToken ct = default);

    /// <summary>
    /// Writes the outlines (and missing fingerprints) and records the run. Idempotent: holds that already
    /// carry an outline are never touched, so running it again only picks up what is still a circle.
    /// </summary>
    /// <param name="wallId">The wall.</param>
    /// <param name="options">Scope.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The counts and the run id.</returns>
    Task<HoldOutlineUpgradeResult> ApplyAsync(Guid wallId, HoldOutlineUpgradeOptions options, CancellationToken ct = default);

    /// <summary>
    /// Restores the holds a run changed — only those whose outline is still exactly what the run wrote; a
    /// hold edited since is left alone and reported.
    /// </summary>
    /// <param name="wallId">The wall.</param>
    /// <param name="runId">The run to revert.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The counts.</returns>
    Task<HoldOutlineRevertResult> RevertAsync(Guid wallId, Guid runId, CancellationToken ct = default);
}
