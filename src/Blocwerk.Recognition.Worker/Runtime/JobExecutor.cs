// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Runtime;
using Blocwerk.Recognition.Worker.Envelope;
using Blocwerk.Recognition.Worker.Jobs;
using Blocwerk.Recognition.Worker.Protocol;

namespace Blocwerk.Recognition.Worker.Runtime;

/// <summary>
/// Runs one claimed job: bundle (size and SHA-256 checked), the kind's handler under a lease heartbeat and the memory
/// watchdog, then the result zip, or <c>fail</c> with how it should count. A job the server ended (<c>404</c>/<c>410</c>)
/// is dropped without a call. The caller runs one job at a time; an abandoned handler is awaited before returning.
/// </summary>
public sealed class JobExecutor(
    RunnerApiClient api,
    IEnumerable<IRecognitionJobHandler> handlers,
    IMemoryProbe memory,
    WorkerOptions options,
    TimeProvider time,
    ILogger<JobExecutor> logger)
{
    /// <summary>Design §4: the large object heap is compacted after a job above 24 MP.</summary>
    private const long CompactAbovePixels = 24_000_000;

    private readonly Dictionary<string, IRecognitionJobHandler> byKind = handlers.ToDictionary(h => h.Kind, StringComparer.Ordinal);

    public IReadOnlyList<string> Kinds => [.. byKind.Keys];

    public async Task RunAsync(RunnerClaim claim, CancellationToken stopping)
    {
        logger.LogInformation("Job {JobId} ({Kind}, lease {Lease} s, {Memory} MB) claimed", claim.JobId, claim.Kind, claim.LeaseSeconds, claim.RequiredMemoryMb);
        using var job = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        var heartbeat = LeaseHeartbeat.Start(api, claim, job, logger);
        var watchdog = MemoryWatchdog.Start(memory, options.JobMaxMemoryMb, options.WatchdogInterval, job);
        RecognitionEnvelope? envelope = null;
        Task<object>? work = null;
        try
        {
            var handler = byKind.GetValueOrDefault(claim.Kind) ?? throw new InvalidOperationException($"Kind '{claim.Kind}' is not served here.");
            var started = time.GetUtcNow();
            envelope = RecognitionEnvelope.Read(await api.BundleAsync(claim, job.Token), claim.Kind);
            work = Task.Run(() => handler.RunAsync(envelope, job.Token), CancellationToken.None);
            var result = await work.WaitAsync(job.Token);
            job.Token.ThrowIfCancellationRequested();
            await api.ResultAsync(claim.JobId, EnvelopeResult.Zip(claim, result, started, time.GetUtcNow()), job.Token);
            logger.LogInformation("Job {JobId} done", claim.JobId);
        }
        catch (Exception ex) when (ex is not UnauthorizedAccessException)
        {
            if (Failure(ex, heartbeat, watchdog, stopping) is { } failure)
            {
                await ReportAsync(claim, failure, ex);
            }
        }
        finally
        {
            await heartbeat.DisposeAsync();
            await watchdog.DisposeAsync();
            await Settle(work, envelope);
        }
    }

    private RunnerFailure? Failure(Exception ex, LeaseHeartbeat heartbeat, MemoryWatchdog watchdog, CancellationToken stopping) => ex switch
    {
        JobGoneException => null,
        OperationCanceledException when heartbeat.Gone => null,
        OperationCanceledException when watchdog.Exceeded => new RunnerFailure(
            $"The job needed more than this runner's {options.JobMaxMemoryMb} MB.", Retryable: true, FailureKind: RunnerFailure.Resource),
        OperationCanceledException when stopping.IsCancellationRequested => new RunnerFailure("The recognition worker is stopping.", Retryable: false, Shutdown: true),
        RecognitionInputException input => new RunnerFailure(input.Message, Retryable: false, FailureKind: input.FailureKind),
        _ => new RunnerFailure($"{ex.GetType().Name}: {ex.Message}", Retryable: true),
    };

    private async Task ReportAsync(RunnerClaim claim, RunnerFailure failure, Exception cause)
    {
        logger.LogWarning(cause, "Job {JobId} failed ({Kind}, retryable {Retryable}): {Reason}", claim.JobId, failure.FailureKind, failure.Retryable, failure.Reason);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await api.FailAsync(claim.JobId, failure, timeout.Token);
        }
        catch (Exception ex) when (ex is HttpRequestException or JobGoneException or OperationCanceledException)
        {
            logger.LogWarning("Could not report the failure of job {JobId}: {Message}", claim.JobId, ex.Message);
        }
    }

    private static async Task Settle(Task<object>? work, RecognitionEnvelope? envelope)
    {
        if (work is not null)
        {
            await work.ContinueWith(_ => { }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        }

        if (envelope?.MaxPixels > CompactAbovePixels)
        {
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        }
    }
}
