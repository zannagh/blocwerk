namespace Blocwerk.Core.Compute;

/// <summary>
/// A client for ONE compute worker speaking the Blocwerk compute job protocol v1
/// (<c>docker/compute-jobs-protocol.md</c>). Every failure surfaces as a
/// <see cref="ComputeJobException"/> whose message is safe to show an admin; the API key is never
/// part of a message or a log line.
/// </summary>
public interface IComputeJobClient
{
    /// <summary>Which configured worker this client talks to.</summary>
    ComputeServiceKind Service { get; }

    /// <summary>False when the worker has no URL configured; every call then throws.</summary>
    bool IsConfigured { get; }

    /// <summary><c>GET /health</c>. Never authenticated.</summary>
    Task<ComputeHealth> GetHealthAsync(CancellationToken ct);

    /// <summary><c>POST /v1/jobs/{kind}</c> with a JSON body. Returns the job id.</summary>
    Task<string> SubmitJsonAsync(string kind, string json, CancellationToken ct);

    /// <summary><c>POST /v1/jobs/{kind}</c> as multipart/form-data. Returns the job id.</summary>
    Task<string> SubmitMultipartAsync(string kind, IReadOnlyList<ComputeJobPart> parts, CancellationToken ct);

    /// <summary><c>GET /v1/jobs/{id}</c>.</summary>
    Task<ComputeJobStatus> GetStatusAsync(string jobId, CancellationToken ct);

    /// <summary><c>GET /v1/jobs/{id}/files/{name}</c>.</summary>
    Task<byte[]> DownloadFileAsync(string jobId, string name, CancellationToken ct);

    /// <summary>
    /// <c>GET /v1/jobs/{id}/files/{name}</c>, refusing (<see cref="ComputeFailureKind.Protocol"/>) a file
    /// larger than <paramref name="maxBytes"/>. The default checks after the download; the HTTP client
    /// stops reading at the limit.
    /// </summary>
    async Task<byte[]> DownloadFileAsync(string jobId, string name, long maxBytes, CancellationToken ct)
    {
        var bytes = await DownloadFileAsync(jobId, name, ct);
        return bytes.LongLength <= maxBytes ? bytes : throw ComputeJobException.TooLarge(name, maxBytes);
    }

    /// <summary><c>DELETE /v1/jobs/{id}</c>. Best effort: failures are logged, never thrown.</summary>
    Task CancelAsync(string jobId, CancellationToken ct);
}

/// <summary>Resolves the client of a configured worker. A new worker is a new enum value + settings.</summary>
public interface IComputeJobClientFactory
{
    IComputeJobClient Get(ComputeServiceKind service);
}

/// <summary>The compute workers the app knows about.</summary>
public enum ComputeServiceKind
{
    /// <summary>wall-geometry: kinds <c>solve</c> and <c>textures</c>.</summary>
    Geometry,

    /// <summary>splat-worker (future): kind <c>splat</c>.</summary>
    Splat,
}
