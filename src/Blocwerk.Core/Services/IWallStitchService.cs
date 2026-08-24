using Blocwerk.Core.Entities;
using Blocwerk.Core.Stitching;

namespace Blocwerk.Core.Services;

/// <summary>
/// Orchestration seam between the web layer and the stitch sidecar: starting a run, keeping the
/// persisted <see cref="WallStitchJob"/> in step with the sidecar, and handing back the finished
/// result. Jobs run for minutes, so nothing here waits for completion — the caller polls
/// <see cref="RefreshJobAsync"/>.
/// </summary>
public interface IWallStitchService
{
    /// <summary>
    /// Submits photos for a wall and persists a <see cref="WallStitchJob"/> for it. The acting user
    /// must be an admin of the wall; otherwise this throws <see cref="UnauthorizedAccessException"/>.
    /// </summary>
    /// <param name="wallId">The wall being re-photographed.</param>
    /// <param name="actingUserId">The user starting the run; must be a wall admin.</param>
    /// <param name="photos">2..12 handheld shots.</param>
    /// <param name="options">Wall angle, default projection, hold transfer.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<WallStitchJob> StartJobAsync(
        Guid wallId,
        Guid actingUserId,
        IReadOnlyList<StitchPhotoUpload> photos,
        WallStitchStartOptions options,
        CancellationToken ct = default);

    /// <summary>The persisted job, or null when it does not exist.</summary>
    Task<WallStitchJob?> GetJobAsync(Guid jobId, CancellationToken ct = default);

    /// <summary>Jobs for a wall, newest first.</summary>
    Task<IReadOnlyList<WallStitchJob>> GetJobsForWallAsync(Guid wallId, CancellationToken ct = default);

    /// <summary>
    /// Polls the sidecar once and writes status, progress, stage, error and diagnostics back onto
    /// the job row. A job that has already reached a terminal state is returned untouched.
    /// </summary>
    Task<WallStitchJob?> RefreshJobAsync(Guid jobId, CancellationToken ct = default);

    /// <summary>
    /// The sidecar result for a succeeded job (artifact names, dimensions, vertical scale,
    /// diagnostics and transferred holds), or null when the job has not succeeded.
    /// </summary>
    Task<StitchJobResult?> GetResultAsync(Guid jobId, CancellationToken ct = default);

    /// <summary>Marks a job cancelled locally and releases the sidecar's temp data.</summary>
    Task CancelJobAsync(Guid jobId, Guid actingUserId, CancellationToken ct = default);

    /// <summary>
    /// Downloads the two full-resolution masters into the master store and returns their stored
    /// names as <c>(ortho, angled)</c>. Streams throughout; nothing is buffered in memory.
    /// </summary>
    Task<(string OrthoMasterPath, string AngledMasterPath)> DownloadMastersAsync(
        Guid jobId,
        CancellationToken ct = default);

    /// <summary>
    /// Applies a succeeded job to the wall's staged slot: the display pair goes onto
    /// <c>StagedPhoto</c>/<c>StagedPhotoAlternate</c> in the job's requested default projection,
    /// the full-resolution masters onto the staged master paths, and the sidecar's transferred
    /// holds into generation N+1 with their classifications mapped onto <c>Hold.NeedsReview</c>,
    /// <c>Confidence</c> and <c>AlignmentSourceHoldId</c>. The staging mode becomes
    /// <c>WallStagingMode.Stitched</c>; confirming or discarding it is <c>WallService</c>'s job.
    /// <para>
    /// A hold the sidecar classified as <c>missing</c> is still created at its predicted position
    /// and flagged for review — dropping it would orphan its <c>BoulderHold</c> links and silently
    /// break the boulders using it. The same holds for a live hold the sidecar never reported.
    /// </para>
    /// The acting user must be a wall admin, and the job must have succeeded.
    /// </summary>
    Task ApplyResultToStagingAsync(Guid jobId, Guid actingUserId, CancellationToken ct = default);
}
