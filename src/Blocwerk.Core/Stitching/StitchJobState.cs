namespace Blocwerk.Core.Stitching;

/// <summary>The body of <c>GET /jobs/{jobId}</c>. <see cref="Status"/> is the sidecar's wire spelling.</summary>
public sealed record StitchJobState(
    string JobId,
    string Status,
    double Progress,
    string? Stage,
    StitchJobError? Error,
    StitchJobResult? Result);
