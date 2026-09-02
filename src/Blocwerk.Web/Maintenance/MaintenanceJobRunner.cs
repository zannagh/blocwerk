using Blocwerk.Web.State;
using Microsoft.Extensions.DependencyInjection;

namespace Blocwerk.Web.Maintenance;

/// <summary>
/// Runs one admin-triggered maintenance job at a time, off the request thread, and holds the same
/// busy lease that an in-flight edit does so <c>/health/ready-to-deploy</c> answers 503 while it
/// works and the autodeploy hook cannot recreate the container mid-run.
/// </summary>
/// <remarks>
/// Nothing here starts on its own: there is no hosted service and no startup hook. A job exists
/// only because an admin pressed a button on <c>/administration</c>.
/// </remarks>
public sealed class MaintenanceJobRunner
{
    /// <summary>How many log lines are kept. Enough to see per-image failures, bounded so a long
    /// run cannot grow without limit in a singleton.</summary>
    private const int MaxLines = 500;

    private readonly IServiceScopeFactory scopes;
    private readonly EditActivityRegistry busy;
    private readonly IHostApplicationLifetime lifetime;
    private readonly ILogger<MaintenanceJobRunner> logger;
    private readonly Lock gate = new();
    private readonly List<string> lines = [];

    private string name = string.Empty;
    private bool running;
    private DateTimeOffset startedUtc;
    private TimeSpan elapsed;
    private string progress = string.Empty;
    private string? summary;
    private string? error;
    private CancellationTokenSource? cancellation;

    public MaintenanceJobRunner(
        IServiceScopeFactory scopes,
        EditActivityRegistry busy,
        IHostApplicationLifetime lifetime,
        ILogger<MaintenanceJobRunner> logger)
    {
        this.scopes = scopes;
        this.busy = busy;
        this.lifetime = lifetime;
        this.logger = logger;
    }

    /// <summary>True while a job occupies the slot.</summary>
    public bool IsRunning
    {
        get
        {
            lock (gate)
            {
                return running;
            }
        }
    }

    /// <summary>
    /// Starts <paramref name="work"/> in the background, or returns false when a job is already
    /// running. The work gets its own DI scope — the caller's circuit may be long gone by the time
    /// it finishes.
    /// </summary>
    public bool TryStart(string jobName, Func<IServiceProvider, MaintenanceJobLog, CancellationToken, Task<string>> work)
    {
        CancellationTokenSource source;

        lock (gate)
        {
            if (running)
            {
                return false;
            }

            running = true;
            name = jobName;
            startedUtc = DateTimeOffset.UtcNow;
            elapsed = TimeSpan.Zero;
            progress = "Starting...";
            summary = null;
            error = null;
            lines.Clear();
            source = CancellationTokenSource.CreateLinkedTokenSource(lifetime.ApplicationStopping);
            cancellation = source;
        }

        // Acquired here, not inside the job: TryStart must not return before the deploy gate has
        // gone busy, or a poll landing in that window would let the container be recreated.
        var lease = busy.Acquire(EditKind.Maintenance, wallId: null, userId: null);

        _ = Task.Run(() => RunAsync(jobName, work, source, lease));
        return true;
    }

    /// <summary>Asks the running job to stop at its next checkpoint.</summary>
    public void Cancel()
    {
        lock (gate)
        {
            cancellation?.Cancel();
        }
    }

    /// <summary>A snapshot the UI can render without touching the job's own state.</summary>
    public MaintenanceJobState Snapshot()
    {
        lock (gate)
        {
            return new MaintenanceJobState(
                name,
                running,
                startedUtc,
                running ? DateTimeOffset.UtcNow - startedUtc : elapsed,
                progress,
                [.. lines],
                summary,
                error,
                cancellation?.IsCancellationRequested ?? false);
        }
    }

    private async Task RunAsync(
        string jobName,
        Func<IServiceProvider, MaintenanceJobLog, CancellationToken, Task<string>> work,
        CancellationTokenSource source,
        IEditLease lease)
    {
        var log = new MaintenanceJobLog(Report, Append);
        string? result = null;
        string? failure = null;

        try
        {
            using var scope = scopes.CreateScope();
            result = await work(scope.ServiceProvider, log, source.Token);
        }
        catch (OperationCanceledException)
        {
            result = "Stopped before finishing.";
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Maintenance job {Job} failed", jobName);
            failure = ex.Message;
        }
        finally
        {
            lease.Dispose();
            source.Dispose();
        }

        // AFTER the lease has gone back, never before: the slot going free is what every caller
        // reads as "the job is over", and a runner that reported idle while still holding the
        // deploy gate would leave the admin page and /health/ready-to-deploy disagreeing. TryStart
        // acquires the lease before it returns for the mirror-image reason.
        Finish(result, failure);
    }

    private void Report(string message)
    {
        lock (gate)
        {
            progress = message;
        }
    }

    private void Append(string message)
    {
        lock (gate)
        {
            if (lines.Count >= MaxLines)
            {
                lines.RemoveAt(0);
            }

            lines.Add(message);
        }
    }

    private void Finish(string? finalSummary, string? failure)
    {
        lock (gate)
        {
            running = false;
            elapsed = DateTimeOffset.UtcNow - startedUtc;
            summary = finalSummary;
            error = failure;
            progress = failure is null ? finalSummary ?? "Done." : "Failed.";
            cancellation = null;
        }
    }
}
