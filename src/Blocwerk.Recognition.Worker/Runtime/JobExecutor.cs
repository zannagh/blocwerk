// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Runtime;
using Blocwerk.Recognition.Worker.Envelope;
using Blocwerk.Recognition.Worker.Jobs;
using Blocwerk.Recognition.Worker.Protocol;

namespace Blocwerk.Recognition.Worker.Runtime;

/// <summary>
/// Runs one claimed job: bundle (size and SHA-256 checked), the kind's handler under a lease heartbeat, the memory
/// watchdog and its run deadline (<see cref="RunBudget"/>), then the result zip, or <c>fail</c> with how it should count. A
/// job the server ended (<c>404</c>/<c>410</c>) is dropped without a call. The caller runs one job at a time; an abandoned
/// handler is awaited before returning, until the run deadline plus <see cref="RunBudget.SettleMargin"/>. Work that outlives
/// that, or memory the watchdog could not get back, ends the process (<see cref="WorkerExit"/>): native work cannot be
/// cancelled, only restarted away.
/// </summary>
public sealed class JobExecutor(
    RunnerApiClient api,
    IEnumerable<IRecognitionJobHandler> handlers,
    IMemoryProbe memory,
    WorkerOptions options,
    WorkerHealth health,
    WorkerExit exit,
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
        var heartbeat = LeaseHeartbeat.Start(api, claim, job, health, logger);
        var watchdog = MemoryWatchdog.Start(memory, options, job);
        var started = time.GetUtcNow();
        var settleBy = DateTimeOffset.MaxValue;
        RecognitionEnvelope? envelope = null;
        Task<object>? work = null;
        try
        {
            var handler = byKind.GetValueOrDefault(claim.Kind) ?? throw new InvalidOperationException($"Kind '{claim.Kind}' is not served here.");
            envelope = RecognitionEnvelope.Read(await api.BundleAsync(claim, job.Token), claim.Kind);
            var budget = RunBudget.Of(claim, envelope);
            settleBy = started + budget + RunBudget.SettleMargin;
            job.CancelAfter(budget);
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
            await SettleAsync(claim, work, envelope, watchdog.NotReleased, settleBy);
        }
    }

    private RunnerFailure? Failure(Exception ex, LeaseHeartbeat heartbeat, MemoryWatchdog watchdog, CancellationToken stopping) => ex switch
    {
        JobGoneException => null,
        OperationCanceledException when heartbeat.Gone => null,
        OperationCanceledException when watchdog.Exceeded => new RunnerFailure(
            $"The job needed more than this runner's {options.JobMaxMemoryMb} MB.", Retryable: true, FailureKind: RunnerFailure.Resource),
        OperationCanceledException when stopping.IsCancellationRequested => new RunnerFailure("The recognition worker is stopping.", Retryable: false, Shutdown: true),
        OperationCanceledException => new RunnerFailure("The job ran past its run deadline.", Retryable: true),
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

    /// <summary>Waits for an abandoned handler until <paramref name="settleBy"/>; ends the process when it or the memory does not let go.</summary>
    private async Task SettleAsync(RunnerClaim claim, Task<object>? work, RecognitionEnvelope? envelope, bool memoryNotReleased, DateTimeOffset settleBy)
    {
        if (memoryNotReleased)
        {
            logger.LogCritical("Job {JobId}: memory stayed above {Limit} MB after it was cancelled; restarting the worker", claim.JobId, options.JobMaxMemoryMb);
            exit.Request(WorkerExit.MemoryNotReleased);
            return;
        }

        if (work is not null)
        {
            var settled = work.ContinueWith(_ => { }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
            var left = settleBy - time.GetUtcNow();
            using var waited = new CancellationTokenSource();
            var deadline = Task.Delay(left > TimeSpan.Zero ? left : TimeSpan.Zero, time, waited.Token);
            var first = await Task.WhenAny(settled, deadline);
            await waited.CancelAsync();
            if (first != settled)
            {
                logger.LogCritical("Job {JobId}: its work did not end by the run deadline; restarting the worker", claim.JobId);
                exit.Request(WorkerExit.WorkNotSettled);
                return;
            }
        }

        if (envelope?.MaxPixels > CompactAbovePixels)
        {
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        }
    }
}
