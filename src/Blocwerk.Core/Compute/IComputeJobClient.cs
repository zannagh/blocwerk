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

    /// <summary>
    /// What one job of this worker takes (<c>GET /v1/info</c>, <c>limits.maxPhotos</c> and <c>maxRequestMb</c>), or null when
    /// the worker does not say (a client that is no HTTP worker). Throws <see cref="ComputeJobException"/> like every call.
    /// </summary>
    Task<ComputeLimits?> GetLimitsAsync(CancellationToken ct) => Task.FromResult<ComputeLimits?>(null);

    /// <summary><c>POST /v1/jobs/{kind}</c> with a JSON body. Returns the job id.</summary>
    Task<string> SubmitJsonAsync(string kind, string json, CancellationToken ct);

    /// <summary><c>POST /v1/jobs/{kind}</c> as multipart/form-data. Returns the job id.</summary>
    Task<string> SubmitMultipartAsync(string kind, IReadOnlyList<ComputeJobPart> parts, CancellationToken ct);

    /// <summary>
    /// <see cref="SubmitMultipartAsync(string, IReadOnlyList{ComputeJobPart}, CancellationToken)"/> for <paramref name="owner"/>'s
    /// capture. An HTTP worker does not need to know whose job it is; a client that queues for a 3D runner does.
    /// </summary>
    Task<string> SubmitMultipartAsync(string kind, IReadOnlyList<ComputeJobPart> parts, ComputeJobOwner owner, CancellationToken ct) =>
        SubmitMultipartAsync(kind, parts, ct);

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

    /// <summary>
    /// <c>GET /v1/jobs/{id}/files/{name}</c> as a stream handed to <paramref name="read"/> (a multi-GB file never becomes
    /// one array). The default buffers the whole file (test clients).
    /// </summary>
    async Task<T> ReadFileAsync<T>(string jobId, string name, Func<Stream, CancellationToken, Task<T>> read, CancellationToken ct)
    {
        using var buffer = new MemoryStream(await DownloadFileAsync(jobId, name, ct), writable: false);
        return await read(buffer, ct);
    }

    /// <summary><c>DELETE /v1/jobs/{id}</c>. Best effort: failures are logged, never thrown.</summary>
    Task CancelAsync(string jobId, CancellationToken ct);

    /// <summary>
    /// The caller read what it needs of the job (or gave up on it) and will not wait for it again: a client that keeps results
    /// itself may drop the job's input now, and its result too unless <paramref name="keepResult"/> (the caller reads it again
    /// later). An HTTP worker forgets its jobs on its own (nothing happens).
    /// </summary>
    Task DoneWithAsync(string jobId, bool keepResult, CancellationToken ct) => Task.CompletedTask;
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

    /// <summary>
    /// splat-worker: kinds <c>splat</c>, <c>splat-prepare</c>, <c>splat-finish</c>. With <c>SPLATSERVICE__ONRUNNERS</c> prepare and
    /// finish run on 3D runners instead (<c>RunnerSplatClient</c>).
    /// </summary>
    Splat,
}
