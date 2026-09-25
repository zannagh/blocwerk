namespace Blocwerk.Core.Services;

/// <summary>
/// "Place existing holds on the 3D model": registers every live panel photo of a wall onto the active model's
/// flattened facet textures (feature matching) and writes each hold's facet and plane position and its
/// millimetre size — for walls whose photos were taken before any printed marker was on the wall. Owner /
/// wall admin only, never from a kiosk tablet. Every run is recorded and can be reverted exactly.
/// </summary>
/// <remarks>
/// Hold X/Y/radius/outline, panels and boulders are never changed and nothing is deleted. Holds placed by
/// markers or by an edit are left alone; holds this action placed before are placed again.
/// </remarks>
public interface IHoldTexturePlacementService
{
    /// <summary>Whether the action is available on the wall, and its latest run.</summary>
    /// <param name="wallId">The wall.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The status.</returns>
    Task<HoldPlacementStatus> GetStatusAsync(Guid wallId, CancellationToken ct = default);

    /// <summary>
    /// Registers the photos, places the holds, records the run and queues the placed holds for footprint
    /// refinement. Takes a while (a few seconds per photo × facet).
    /// </summary>
    /// <param name="wallId">The wall.</param>
    /// <param name="trigger">What started it (<see cref="HoldPlacementTrigger.Admin"/> or <see cref="HoldPlacementTrigger.Api"/>).</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The counts, per panel, and the run id.</returns>
    Task<HoldPlacementResult> PlaceAsync(Guid wallId, string trigger = HoldPlacementTrigger.Admin, CancellationToken ct = default);

    /// <summary>
    /// Restores the holds a run placed — only those whose placement is still exactly what the run wrote.
    /// </summary>
    /// <param name="wallId">The wall.</param>
    /// <param name="runId">The run.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The counts.</returns>
    Task<HoldPlacementRevertResult> RevertAsync(Guid wallId, Guid runId, CancellationToken ct = default);

    /// <summary>
    /// The capture pipeline's automatic run after <paramref name="modelId"/> went live (the post-capture chain's
    /// first step): only when it is the wall's active model and has textures, and only for the live holds that are
    /// not on it yet (including those this action placed on an earlier model: a re-solve moves the planes). Holds placed by markers or by an edit are never touched, whatever model they sit on. No user
    /// check (the pipeline checked its admin), no refinement queued (the chain refines next). A failure throws
    /// (the chain records it and goes on).
    /// </summary>
    /// <param name="wallId">The wall.</param>
    /// <param name="modelId">The capture's model.</param>
    /// <param name="actingUserId">The admin who started the capture.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The result, or null when it did not run.</returns>
    Task<HoldPlacementResult?> PlaceFromPipelineAsync(Guid wallId, Guid modelId, Guid actingUserId, CancellationToken ct = default);
}
