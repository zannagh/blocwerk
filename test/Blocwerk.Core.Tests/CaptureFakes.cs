using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Compute;

namespace Blocwerk.Core.Tests;

/// <summary>
/// A scripted compute worker: records every submission, answers each job with "running" once and
/// then the scripted terminal status. Solve jobs succeed with <see cref="GeometryJson"/>, texture
/// jobs with one facet file per facet of it — unless a failure is scripted. As the splat worker it
/// serves <see cref="FrameJson"/> and <see cref="Spz"/> for any finished job.
/// </summary>
internal sealed class FakeComputeJobClient : IComputeJobClient
{
    private readonly Dictionary<string, string> kinds = [];
    private readonly Dictionary<string, int> polls = [];
    private int next;

    public ComputeServiceKind Service { get; init; } = ComputeServiceKind.Geometry;

    public bool IsConfigured { get; set; } = true;

    public string GeometryJson { get; set; } = CaptureScenario.GeometryWithCameras("p01", "p02");

    /// <summary>The <c>frame.json</c> a splat job returns: aligned, 1 unit = 1 m.</summary>
    public string FrameJson { get; set; } = SplatFrame(aligned: true);

    /// <summary>The <c>wall.spz</c> a splat job returns (a gzip stream, as a real one is).</summary>
    public byte[] Spz { get; set; } = Gzip(new byte[4096]);

    public List<string> Downloads { get; } = [];

    public List<(string Kind, string Json)> JsonSubmissions { get; } = [];

    public List<(string Kind, IReadOnlyList<ComputeJobPart> Parts)> MultipartSubmissions { get; } = [];

    /// <summary>Thrown from every submit (e.g. a 401 mapped by the real client).</summary>
    public ComputeJobException? SubmitError { get; set; }

    /// <summary>When set, the job of that kind ends with this status instead of succeeding.</summary>
    public Dictionary<string, ComputeJobStatus> Terminal { get; } = [];

    /// <summary>Kinds whose jobs never finish (to exercise the timeout).</summary>
    public HashSet<string> NeverFinish { get; } = [];

    /// <summary>Job ids the worker "forgot" (restarted): polling them answers 404.</summary>
    public HashSet<string> Forgotten { get; } = [];

    public List<string> Cancelled { get; } = [];

    public Task<ComputeHealth> GetHealthAsync(CancellationToken ct) => Task.FromResult(new ComputeHealth { Status = "ok" });

    public Task<string> SubmitJsonAsync(string kind, string json, CancellationToken ct)
    {
        JsonSubmissions.Add((kind, json));
        return Submit(kind);
    }

    public Task<string> SubmitMultipartAsync(string kind, IReadOnlyList<ComputeJobPart> parts, CancellationToken ct)
    {
        MultipartSubmissions.Add((kind, parts));
        return Submit(kind);
    }

    public Task<ComputeJobStatus> GetStatusAsync(string jobId, CancellationToken ct)
    {
        if (Forgotten.Contains(jobId) || !kinds.TryGetValue(jobId, out var kind))
        {
            throw new ComputeJobException(ComputeFailureKind.JobNotFound, "gone");
        }

        polls[jobId] = polls.GetValueOrDefault(jobId) + 1;
        if (polls[jobId] == 1 || NeverFinish.Contains(kind))
        {
            return Task.FromResult(new ComputeJobStatus { JobId = jobId, Status = ComputeJobStates.Running, Progress = 0.5, Stage = "working" });
        }

        return Task.FromResult(Terminal.TryGetValue(kind, out var scripted) ? scripted : Succeeded(jobId, kind));
    }

    /// <summary>
    /// Optional override for a result-file download, by file name. When null, downloads answer per file:
    /// <c>frame.json</c> → <see cref="FrameJson"/>, <c>wall.spz</c> → <see cref="Spz"/>, anything else a tiny JPEG.
    /// </summary>
    public Func<string, byte[]>? Download { get; set; }

    public Task<byte[]> DownloadFileAsync(string jobId, string name, CancellationToken ct)
    {
        Downloads.Add(name);
        if (Download is not null)
        {
            return Task.FromResult(Download(name));
        }

        return Task.FromResult(name switch
        {
            "frame.json" => Encoding.UTF8.GetBytes(FrameJson),
            "wall.spz" => Spz,
            _ => CaptureScenario.TinyJpeg(),
        });
    }

    public static string SplatFrame(bool aligned) => new JsonObject
    {
        ["version"] = 1,
        ["aligned"] = aligned,
        ["units"] = "m",
        ["toWorldMm"] = new JsonArray(
            new JsonArray(1000.0, 0.0, 0.0, 10.0),
            new JsonArray(0.0, 1000.0, 0.0, 20.0),
            new JsonArray(0.0, 0.0, 1000.0, 30.0),
            new JsonArray(0.0, 0.0, 0.0, 1.0)),
        ["alignment"] = new JsonObject { ["residualMmMedian"] = 12.5 },
    }.ToJsonString();

    public static byte[] Gzip(byte[] content)
    {
        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionLevel.Fastest))
        {
            gzip.Write(content);
        }

        return buffer.ToArray();
    }

    public Task CancelAsync(string jobId, CancellationToken ct)
    {
        Cancelled.Add(jobId);
        return Task.CompletedTask;
    }

    /// <summary>Registers a job as if a previous app process had submitted it.</summary>
    public string Adopt(string kind)
    {
        var id = $"job-{++next}";
        kinds[id] = kind;
        return id;
    }

    private Task<string> Submit(string kind)
    {
        if (SubmitError is not null)
        {
            throw SubmitError;
        }

        return Task.FromResult(Adopt(kind));
    }

    private ComputeJobStatus Succeeded(string jobId, string kind)
    {
        var result = kind == "solve"
            ? new JsonObject { ["geometry"] = JsonNode.Parse(GeometryJson) }
            : new JsonObject
            {
                ["facets"] = new JsonArray(
                    new JsonObject
                    {
                        ["facet"] = "0", ["file"] = "facet_0.jpg", ["widthPx"] = 1550, ["heightPx"] = 1300,
                        ["bounds"] = new JsonObject { ["aMin"] = -100.0, ["aMax"] = 3000.0, ["bMin"] = -100.0, ["bMax"] = 2500.0 },
                    },
                    new JsonObject { ["facetId"] = "5a", ["file"] = "facet_5a.jpg", ["widthPx"] = 450, ["heightPx"] = 1000, ["aMin"] = 0.0, ["aMax"] = 900.0, ["bMin"] = 0.0, ["bMax"] = 2000.0 }),
            };
        return new ComputeJobStatus
        {
            JobId = jobId, Status = ComputeJobStates.Succeeded, Progress = 1,
            Result = JsonDocument.Parse(result.ToJsonString()).RootElement.Clone(),
        };
    }
}

/// <summary>Hands out the fake geometry client, and the splat client (not configured unless given).</summary>
internal sealed class FakeComputeJobClientFactory(FakeComputeJobClient client, FakeComputeJobClient? splat = null)
    : IComputeJobClientFactory
{
    private readonly FakeComputeJobClient splatClient =
        splat ?? new FakeComputeJobClient { Service = ComputeServiceKind.Splat, IsConfigured = false };

    public IComputeJobClient Get(ComputeServiceKind service) =>
        service == ComputeServiceKind.Splat ? splatClient : client;
}

/// <summary>
/// A marker detector that "finds" a fixed set of markers in every image — the ones the options allow,
/// as the real validator does — and records the options of every call.
/// </summary>
internal sealed class FakeMarkerDetectionService(params int[] ids) : IMarkerDetectionService
{
    public List<MarkerDetectionOptions?> Calls { get; } = [];

    public Task<MarkerDetectionResult> DetectAsync(byte[] image, MarkerDetectionOptions? options, CancellationToken ct)
    {
        Calls.Add(options);
        var allowed = (options ?? MarkerDetectionOptions.Default).AllowedIds;
        var markers = ids.Where(allowed.Contains).Select(id => new DetectedMarker
        {
            Id = id,
            CornersPx = [new(10 + id, 10), new(60 + id, 10), new(60 + id, 60), new(10 + id, 60)],
            CornersNormalized = [new(0.1, 0.1), new(0.6, 0.1), new(0.6, 0.6), new(0.1, 0.6)],
            SidePx = 50,
            EdgeRatio = 1,
        }).ToList();
        return Task.FromResult(new MarkerDetectionResult { ImageWidth = 64, ImageHeight = 64, Markers = markers, Rejected = [] });
    }
}
