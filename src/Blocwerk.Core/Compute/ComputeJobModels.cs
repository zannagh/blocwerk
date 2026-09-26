using System.Text.Json;
using System.Text.Json.Serialization;

namespace Blocwerk.Core.Compute;

/// <summary>Job states of protocol v1.</summary>
public static class ComputeJobStates
{
    public const string Queued = "queued";
    public const string Running = "running";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";

    public static bool IsTerminal(string? status) => status is Succeeded or Failed or Cancelled;
}

/// <summary>The body of <c>GET /v1/jobs/{id}</c>.</summary>
public sealed record ComputeJobStatus
{
    [JsonPropertyName("jobId")]
    public string? JobId { get; init; }

    [JsonPropertyName("status")]
    public string Status { get; init; } = ComputeJobStates.Queued;

    [JsonPropertyName("progress")]
    public double? Progress { get; init; }

    [JsonPropertyName("stage")]
    public string? Stage { get; init; }

    /// <summary>Optional finer detail within the stage, e.g. "step 1200/15000".</summary>
    [JsonPropertyName("stageDetail")]
    public string? StageDetail { get; init; }

    [JsonPropertyName("message")]
    public string? Message { get; init; }

    [JsonPropertyName("error")]
    public string? Error { get; init; }

    /// <summary>Per-kind result, kept raw so each caller parses what it needs.</summary>
    [JsonPropertyName("result")]
    public JsonElement? Result { get; init; }
}

/// <summary>The body of <c>GET /health</c>.</summary>
public sealed record ComputeHealth
{
    [JsonPropertyName("status")]
    public string? Status { get; init; }

    [JsonPropertyName("service")]
    public string? Service { get; init; }

    [JsonPropertyName("protocol")]
    public string? Protocol { get; init; }

    [JsonPropertyName("version")]
    public string? Version { get; init; }

    [JsonPropertyName("kinds")]
    public IReadOnlyList<string> Kinds { get; init; } = [];

    /// <summary>The highest quality profile the worker trains itself (<c>ultra</c> with gsplat on a 12 GB GPU), when it says.</summary>
    [JsonPropertyName("maxQuality")]
    public string? MaxQuality { get; init; }

    /// <summary>
    /// What the splat worker's <c>splat-prepare</c> returns and accepts beyond the bundle (<c>sparse.zip</c>, <c>anchors</c>):
    /// markerless captures need both. Empty on older workers and on wall-geometry.
    /// </summary>
    [JsonPropertyName("prepareOutputs")]
    public IReadOnlyList<string> PrepareOutputs { get; init; } = [];
}

/// <summary>
/// One part of a multipart job submission: a JSON field or a file. <c>SourcePath</c>, when set, is a file on disk the client streams instead of <c>Content</c>
/// (which is then empty).
/// </summary>
public sealed record ComputeJobPart(string Name, byte[] Content, string ContentType, string? FileName = null, string? SourcePath = null)
{
    public static ComputeJobPart Json(string name, string json) =>
        new(name, System.Text.Encoding.UTF8.GetBytes(json), "application/json", $"{name}.json");

    public static ComputeJobPart File(string name, string fileName, byte[] content, string contentType) =>
        new(name, content, contentType, fileName);

    /// <summary>A file part streamed from <paramref name="path"/> (never read into memory).</summary>
    public static ComputeJobPart FromDisk(string name, string fileName, string path, string contentType) =>
        new(name, [], contentType, fileName, path);
}

/// <summary>What went wrong talking to a worker, so callers can decide to retry.</summary>
public enum ComputeFailureKind
{
    /// <summary>No URL configured.</summary>
    NotConfigured,

    /// <summary>401/403: the API key is missing or wrong.</summary>
    Unauthorized,

    /// <summary>Unknown or expired job (worker restarted or result TTL elapsed).</summary>
    JobNotFound,

    /// <summary>400/404/413/422/501: the worker refused the input; retrying will not help.</summary>
    Rejected,

    /// <summary>429: the worker's queue is full.</summary>
    Busy,

    /// <summary>Network error, timeout or 5xx: worth retrying.</summary>
    Unavailable,

    /// <summary>The worker answered something that is not protocol v1.</summary>
    Protocol,
}

/// <summary>A compute worker failure. <see cref="Exception.Message"/> is safe to show an admin.</summary>
public sealed class ComputeJobException(ComputeFailureKind kind, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public ComputeFailureKind Kind { get; } = kind;

    /// <summary>Whether waiting and asking again can succeed.</summary>
    public bool IsTransient => Kind is ComputeFailureKind.Unavailable or ComputeFailureKind.Busy;

    /// <summary>A result file over the caller's size limit.</summary>
    public static ComputeJobException TooLarge(string name, long maxBytes) => new(
        ComputeFailureKind.Protocol,
        $"The 3D computation service sent a file ({name}) larger than {maxBytes / (1024 * 1024)} MB.");
}
