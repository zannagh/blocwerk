using Blocwerk.Core.Stitching;

namespace Blocwerk.Core.Abstractions;

/// <summary>
/// Talks to the external Python stitching sidecar. Every call is a single short HTTP round trip:
/// jobs are asynchronous and can take minutes, so callers poll <see cref="GetJobAsync"/> rather
/// than blocking a request thread.
/// </summary>
public interface IWallStitchClient
{
    /// <summary>Whether a sidecar base URL is configured at all.</summary>
    bool IsConfigured { get; }

    /// <summary>Submits 2..12 photos and returns the sidecar's job id.</summary>
    /// <param name="photos">The handheld shots to stitch.</param>
    /// <param name="options">Wall angle, projection, and the holds to transfer.</param>
    /// <param name="oldPhoto">The wall's current photo; required when transferring holds.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<StitchJobCreationResult> CreateJobAsync(
        IReadOnlyList<StitchPhotoUpload> photos,
        StitchJobOptions options,
        StitchPhotoUpload? oldPhoto,
        CancellationToken ct = default);

    /// <summary>Reads the current state (and, once succeeded, the result) of a sidecar job.</summary>
    Task<StitchJobState> GetJobAsync(string sidecarJobId, CancellationToken ct = default);

    /// <summary>
    /// Streams an artifact into <paramref name="destination"/>. Implementations must never buffer
    /// the body: a full-resolution master is tens of megabytes.
    /// </summary>
    Task DownloadArtifactAsync(
        string sidecarJobId,
        string artifactName,
        Stream destination,
        CancellationToken ct = default);

    /// <summary>Releases the sidecar's temp data for a job. Safe to call on an unknown job.</summary>
    Task DeleteJobAsync(string sidecarJobId, CancellationToken ct = default);
}
