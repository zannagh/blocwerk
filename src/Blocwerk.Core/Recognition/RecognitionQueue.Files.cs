// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Entities;
using Blocwerk.Core.Runners;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Recognition;

/// <summary>The runner's side of a job's files: the input envelope it downloads and the result zip it uploads.</summary>
public sealed partial class RecognitionQueue
{
    /// <summary>
    /// The physical path of the claimed job's input envelope. A missing or damaged input fails the job
    /// (<see cref="RunnerJobOutcome.Over"/>): no runner could do it.
    /// </summary>
    public async Task<(RunnerJobOutcome Outcome, string? Path)> InputForRunnerAsync(GpuRunner runner, Guid jobId, CancellationToken ct)
    {
        var (found, job) = await FindClaimedAsync(runner, jobId, ct);
        if (found != RunnerJobOutcome.Ok || job is null)
        {
            return (found, null);
        }

        var path = job.InputPath is null ? null : files.ResolvePhysicalPath(job.InputPath);
        if (path is not null && File.Exists(path) && new FileInfo(path).Length == job.InputBytes)
        {
            return (RunnerJobOutcome.Ok, path);
        }

        logger.LogError("Recognition job {JobId}: its input {Input} is missing or damaged; the job fails", job.Id, job.InputPath);
        await using var db = dbContextFactory.CreateDbContext();
        var ended = await FinishAsync(db, job, GpuJobStatus.Failed, "the input is missing or damaged on the server", null, ct);
        return (ended ? RunnerJobOutcome.Over : await LostAsync(runner, jobId, ct), null);
    }

    /// <summary>
    /// The runner's result upload (<see cref="RecognitionResultZip"/>): the job succeeds with the inline result. Refused before
    /// the body is read when the job is not the runner's.
    /// </summary>
    public async Task<RunnerJobOutcome> AcceptResultAsync(GpuRunner runner, Guid jobId, Stream body, string? contentEncoding, CancellationToken ct)
    {
        var encoding = contentEncoding?.Trim().ToLowerInvariant();
        if (encoding is not (null or "" or "identity" or "gzip"))
        {
            return RunnerJobOutcome.UnsupportedEncoding;
        }

        var (found, _) = await FindClaimedAsync(runner, jobId, ct);
        if (found != RunnerJobOutcome.Ok)
        {
            return found;
        }

        var (result, refused) = await RecognitionResultZip.ReadAsync(body, encoding == "gzip", ct);
        if (result is null)
        {
            logger.LogWarning("Runner {RunnerId} sent an unusable result for recognition job {JobId}: {Refused}", runner.Id, jobId, refused);
            return refused;
        }

        return await ResultAsync(runner, jobId, result, ct);
    }
}
