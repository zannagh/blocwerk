// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Recognition.Worker.Protocol;

namespace Blocwerk.Recognition.Worker.Runtime;

/// <summary>
/// Renews the claim's lease while the job runs: a progress call every sixth of <c>leaseSeconds</c> (5 s for the
/// interactive 30 s lease), each given that interval at most. A <c>404</c>/<c>410</c> answer means the job is over or was
/// taken away: the job is cancelled. Any other failure, a timeout included, is logged and the next beat tries again; only
/// the job's end stops the loop. Each answered beat counts as server contact (<see cref="WorkerHealth"/>).
/// </summary>
public sealed class LeaseHeartbeat : IAsyncDisposable
{
    private readonly CancellationTokenSource stop = new();
    private readonly Task loop;

    private LeaseHeartbeat(RunnerApiClient api, RunnerClaim claim, CancellationTokenSource job, WorkerHealth health, ILogger logger)
    {
        loop = BeatAsync(api, claim, job, health, logger);
    }

    public bool Gone { get; private set; }

    public static LeaseHeartbeat Start(RunnerApiClient api, RunnerClaim claim, CancellationTokenSource job, WorkerHealth health, ILogger logger) =>
        new(api, claim, job, health, logger);

    public static TimeSpan Interval(int leaseSeconds) => TimeSpan.FromSeconds(Math.Max(1, leaseSeconds / 6.0));

    public async ValueTask DisposeAsync()
    {
        await stop.CancelAsync();
        await loop;
        stop.Dispose();
    }

    private async Task BeatAsync(RunnerApiClient api, RunnerClaim claim, CancellationTokenSource job, WorkerHealth health, ILogger logger)
    {
        var interval = Interval(claim.LeaseSeconds);
        using var timer = new PeriodicTimer(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(stop.Token))
            {
                health.MarkAlive();
                if (!await BeatOnceAsync(api, claim, interval, health, logger))
                {
                    Gone = true;
                    await job.CancelAsync();
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            // The job ended first.
        }
    }

    /// <summary>One progress call; false when the server ended the job.</summary>
    private async Task<bool> BeatOnceAsync(RunnerApiClient api, RunnerClaim claim, TimeSpan timeout, WorkerHealth health, ILogger logger)
    {
        using var call = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
        call.CancelAfter(timeout);
        try
        {
            await api.ProgressAsync(claim.JobId, new RunnerProgress(claim.Kind), call.Token);
            health.MarkContact();
        }
        catch (JobGoneException ex)
        {
            logger.LogInformation("Job {JobId} ended on the server ({Status}); aborting it", claim.JobId, ex.Status);
            return false;
        }
        catch (Exception ex) when (!stop.IsCancellationRequested)
        {
            logger.LogWarning("Heartbeat for job {JobId} failed, retrying: {Type}: {Message}", claim.JobId, ex.GetType().Name, ex.Message);
        }

        return true;
    }
}
