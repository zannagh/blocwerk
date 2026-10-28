// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.IO.Compression;
using Blocwerk.Core.Capture;
using Blocwerk.Core.Compute;
using Blocwerk.Core.Entities;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Runners;

/// <summary>
/// The splat worker when <see cref="GpuRunnerOptions.SplatOnRunners"/> is on: <c>splat-prepare</c> / <c>splat-finish</c> become
/// <see cref="GpuJobKind.SplatPrepare"/> / <see cref="GpuJobKind.SplatFinish"/> jobs a 3D runner claims, so the server runs no
/// splat worker. A submission is written as a runner envelope (<see cref="RunnerComputeEnvelope"/>, streamed from disk) and queued;
/// its id is <see cref="IdPrefix"/> + the job id. A job not done within <see cref="DeferAfter"/> answers
/// <see cref="ComputeFailureKind.Deferred"/>, so the capture worker never waits for a runner: the queue wakes the capture when the
/// result arrives. The result zip is served file by file like the worker would (<see cref="RunnerComputeResult"/>), every read
/// bounded by its entry's size. <c>splat</c> (training on the server) is not offered.
/// </summary>
public sealed class RunnerSplatClient(
    GpuJobQueue queue, ICaptureFileStore files, ILogger<RunnerSplatClient> logger, TimeSpan? deferAfter = null) : IComputeJobClient
{
    /// <summary>The prefix of a job id of this client (any other id is unknown here, so the caller submits again).</summary>
    public const string IdPrefix = "runner:";

    /// <summary>How long a poll may wait for a runner before the caller is told to stop waiting.</summary>
    public static readonly TimeSpan DefaultDeferAfter = TimeSpan.FromMinutes(2);

    /// <summary>What a finish envelope holds beyond the trained scene: <c>prepared.json</c> (64 MiB) and the rest.</summary>
    private const long FinishEnvelopeExtraBytes = 128L * 1024 * 1024;

    public TimeSpan DeferAfter { get; } = deferAfter ?? DefaultDeferAfter;

    /// <inheritdoc />
    public ComputeServiceKind Service => ComputeServiceKind.Splat;

    /// <summary>False with runners off (<c>RUNNERS__MODE=off</c>): nothing could ever take the jobs.</summary>
    public bool IsConfigured => queue.Options.Mode != GpuRunnerMode.Off;

    /// <summary>What a splat worker with the split kinds and the markerless outputs says; nothing is asked.</summary>
    public Task<ComputeHealth> GetHealthAsync(CancellationToken ct)
    {
        EnsureConfigured();
        return Task.FromResult(new ComputeHealth
        {
            Status = "ok",
            Service = "splat-runners",
            Protocol = "1",
            Kinds = [WallCaptureProcessor.PrepareKind, WallCaptureProcessor.FinishKind],
            PrepareOutputs = [MarkerlessCaptureSupport.SparseOutput, MarkerlessCaptureSupport.AnchorsOutput],
        });
    }

    /// <inheritdoc />
    public Task<string> SubmitJsonAsync(string kind, string json, CancellationToken ct) => throw NotOffered(kind);

    /// <summary>Refused: a runner job belongs to a capture (<see cref="ComputeJobOwner"/>).</summary>
    public Task<string> SubmitMultipartAsync(string kind, IReadOnlyList<ComputeJobPart> parts, CancellationToken ct) =>
        throw new ComputeJobException(ComputeFailureKind.Rejected, "A job for a 3D runner needs the capture it belongs to.");

    /// <summary>Writes the envelope into the store and queues the job for a runner.</summary>
    public async Task<string> SubmitMultipartAsync(string kind, IReadOnlyList<ComputeJobPart> parts, ComputeJobOwner owner, CancellationToken ct)
    {
        var jobKind = kind switch
        {
            WallCaptureProcessor.PrepareKind => GpuJobKind.SplatPrepare,
            WallCaptureProcessor.FinishKind => GpuJobKind.SplatFinish,
            _ => throw NotOffered(kind),
        };
        EnsureConfigured();
        var cap = jobKind == GpuJobKind.SplatPrepare ? queue.Options.MaxBundleBytes : queue.Options.MaxResultBytes + FinishEnvelopeExtraBytes;
        var (stored, bytes, sha) = await RunnerComputeEnvelope.StoreAsync(files, kind, parts, cap, ct);
        try
        {
            var job = await queue.EnqueueAsync(
                new GpuJob
                {
                    WallId = owner.WallId, CaptureId = owner.CaptureId, GeometryModelId = owner.ModelId ?? Guid.Empty, Kind = jobKind,
                    BundlePath = stored, BundleBytes = bytes, BundleSha256 = sha, PreparedPath = stored,
                },
                ct);
            return IdPrefix + job.Id.ToString("D");
        }
        catch
        {
            files.Delete(stored);
            throw;
        }
    }

    /// <summary>The job as the splat worker would report it; <see cref="ComputeFailureKind.Deferred"/> once it waited too long.</summary>
    public async Task<ComputeJobStatus> GetStatusAsync(string jobId, CancellationToken ct)
    {
        var job = await FindAsync(jobId, ct);
        switch (job.Status)
        {
            case GpuJobStatus.Succeeded:
                return Read(() => RunnerComputeResult.ToStatus(ResultPath(job), jobId));
            case GpuJobStatus.Failed or GpuJobStatus.Cancelled:
                return new ComputeJobStatus
                {
                    JobId = jobId,
                    Status = job.Status == GpuJobStatus.Failed ? ComputeJobStates.Failed : ComputeJobStates.Cancelled,
                    Error = job.Error ?? "the 3D runner job ended without a result",
                };
            default:
                if (queue.Clock.GetUtcNow() - job.CreatedAt >= DeferAfter)
                {
                    throw ComputeJobException.Deferred();
                }

                return new ComputeJobStatus
                {
                    JobId = jobId,
                    Status = job.Status == GpuJobStatus.Queued ? ComputeJobStates.Queued : ComputeJobStates.Running,
                    Progress = job.Progress,
                    Stage = job.Stage,
                };
        }
    }

    /// <inheritdoc />
    public Task<byte[]> DownloadFileAsync(string jobId, string name, CancellationToken ct) => DownloadFileAsync(jobId, name, long.MaxValue, ct);

    /// <summary>
    /// One file of the result, read in full into one array of the entry's size (known and capped at upload), never past it or
    /// <paramref name="maxBytes"/>.
    /// </summary>
    public Task<byte[]> DownloadFileAsync(string jobId, string name, long maxBytes, CancellationToken ct) =>
        ReadEntryAsync(
            jobId,
            name,
            async (stream, length, token) =>
            {
                var bytes = new byte[length];
                await stream.ReadExactlyAsync(bytes, token);
                return bytes;
            },
            Math.Min(maxBytes, Array.MaxLength),
            ct);

    /// <summary>One file of the result as a stream (a multi-GB bundle is never one array), bounded by its entry's size.</summary>
    /// <typeparam name="T">What <paramref name="read"/> makes of the file.</typeparam>
    public Task<T> ReadFileAsync<T>(string jobId, string name, Func<Stream, CancellationToken, Task<T>> read, CancellationToken ct) =>
        ReadEntryAsync(jobId, name, (stream, _, token) => read(stream, token), long.MaxValue, ct);

    /// <summary>Cancels a job that has not ended (it ends Cancelled); its runner learns it with its next call.</summary>
    public Task CancelAsync(string jobId, CancellationToken ct) => CloseAsync(jobId, keepResult: false, cancel: true, ct);

    /// <summary>
    /// A delivered job whose result was read is installed: its input envelope goes, and its result too unless
    /// <paramref name="keepResult"/> (it is read again later). A job that is still waiting is cancelled.
    /// </summary>
    public Task DoneWithAsync(string jobId, bool keepResult, CancellationToken ct) => CloseAsync(jobId, keepResult, cancel: false, ct);

    private async Task CloseAsync(string jobId, bool keepResult, bool cancel, CancellationToken ct)
    {
        if (TryParse(jobId) is not { } id || await queue.FindAsync(id, ct) is not { } job || job.Status is GpuJobStatus.Failed or GpuJobStatus.Cancelled)
        {
            return;
        }

        if (job.Status != GpuJobStatus.Succeeded || (cancel && job.InstalledAt is null))
        {
            await queue.CloseComputeAsync(id, "no longer needed by the server", ct, cancelled: true);
        }
        else if (!cancel && (job.InstalledAt is null || (!keepResult && job.ResultPath is not null)))
        {
            await queue.CloseComputeAsync(id, null, ct, dropResult: !keepResult);
        }
    }

    private static Guid? TryParse(string jobId) =>
        jobId.StartsWith(IdPrefix, StringComparison.Ordinal) && Guid.TryParse(jobId.AsSpan(IdPrefix.Length), out var id) ? id : null;

    private static ComputeJobException NotOffered(string kind) => new(
        ComputeFailureKind.NotConfigured, $"3D runners do not run {kind} jobs; this server prepares and finishes photo-real views on runners.");

    private static ComputeJobException Gone(string jobId) =>
        new(ComputeFailureKind.JobNotFound, $"The 3D runner job {jobId} is not known (any more).");

    private static T Read<T>(Func<T> read)
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            throw new ComputeJobException(ComputeFailureKind.Protocol, $"The 3D runner's result could not be read: {ex.Message}", ex);
        }
    }

    private async Task<T> ReadEntryAsync<T>(
        string jobId, string name, Func<Stream, int, CancellationToken, Task<T>> read, long maxBytes, CancellationToken ct)
    {
        var job = await FindAsync(jobId, ct);
        if (job.Status != GpuJobStatus.Succeeded)
        {
            throw new ComputeJobException(ComputeFailureKind.Rejected, $"The 3D runner job {jobId} has no result.");
        }

        using var zip = Read(() => ZipFile.OpenRead(ResultPath(job)));
        var entry = zip.GetEntry(name) ?? throw new ComputeJobException(ComputeFailureKind.Protocol, $"The runner's result has no file {name}.");
        if (entry.Length > maxBytes)
        {
            throw ComputeJobException.TooLarge(name, maxBytes);
        }

        try
        {
            await using var stream = new BoundedReadStream(entry.Open(), entry.Length);
            return await read(stream, (int)Math.Min(entry.Length, int.MaxValue), ct);
        }
        catch (InvalidDataException ex)
        {
            logger.LogWarning("Reading {File} of runner job {JobId} failed: {Reason}", name, jobId, ex.Message);
            throw new ComputeJobException(ComputeFailureKind.Protocol, $"The runner's file {name} could not be read.", ex);
        }
    }

    private async Task<GpuJob> FindAsync(string jobId, CancellationToken ct)
    {
        EnsureConfigured();
        var job = TryParse(jobId) is { } id ? await queue.FindAsync(id, ct) : null;
        return job is { Kind: GpuJobKind.SplatPrepare or GpuJobKind.SplatFinish } ? job : throw Gone(jobId);
    }

    private string ResultPath(GpuJob job) =>
        job.ResultPath is { } stored && files.ResolvePhysicalPath(stored) is { } path && File.Exists(path) ? path : throw Gone(IdPrefix + job.Id);

    private void EnsureConfigured()
    {
        if (!IsConfigured)
        {
            throw new ComputeJobException(
                ComputeFailureKind.NotConfigured, "SPLATSERVICE__ONRUNNERS needs 3D runners, but RUNNERS__MODE is off.");
        }
    }
}
