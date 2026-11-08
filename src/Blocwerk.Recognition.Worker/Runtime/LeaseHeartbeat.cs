// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Recognition.Worker.Protocol;

namespace Blocwerk.Recognition.Worker.Runtime;

/// <summary>
/// Renews the claim's lease while the job runs: a progress call every sixth of <c>leaseSeconds</c> (5 s for the
/// interactive 30 s lease). A <c>404</c>/<c>410</c> answer means the job is over or was taken away: the job is cancelled.
/// </summary>
public sealed class LeaseHeartbeat : IAsyncDisposable
{
    private readonly CancellationTokenSource stop = new();
    private readonly Task loop;

    private LeaseHeartbeat(RunnerApiClient api, RunnerClaim claim, CancellationTokenSource job, ILogger logger)
    {
        loop = BeatAsync(api, claim, job, logger);
    }

    public bool Gone { get; private set; }

    public static LeaseHeartbeat Start(RunnerApiClient api, RunnerClaim claim, CancellationTokenSource job, ILogger logger) =>
        new(api, claim, job, logger);

    public static TimeSpan Interval(int leaseSeconds) => TimeSpan.FromSeconds(Math.Max(1, leaseSeconds / 6.0));

    public async ValueTask DisposeAsync()
    {
        await stop.CancelAsync();
        await loop;
        stop.Dispose();
    }

    private async Task BeatAsync(RunnerApiClient api, RunnerClaim claim, CancellationTokenSource job, ILogger logger)
    {
        using var timer = new PeriodicTimer(Interval(claim.LeaseSeconds));
        try
        {
            while (await timer.WaitForNextTickAsync(stop.Token))
            {
                try
                {
                    await api.ProgressAsync(claim.JobId, new RunnerProgress(claim.Kind), stop.Token);
                }
                catch (JobGoneException ex)
                {
                    logger.LogInformation("Job {JobId} ended on the server ({Status}); aborting it", claim.JobId, ex.Status);
                    Gone = true;
                    await job.CancelAsync();
                    return;
                }
                catch (HttpRequestException ex)
                {
                    logger.LogWarning("Heartbeat for job {JobId} failed: {Message}", claim.JobId, ex.Message);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The job ended first.
        }
    }
}
