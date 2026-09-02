namespace Blocwerk.Web.Maintenance;

/// <summary>
/// A point-in-time view of the maintenance job slot, safe to read from a Blazor circuit while the
/// job itself runs on a background thread.
/// </summary>
/// <param name="Name">What is (or was last) running.</param>
/// <param name="IsRunning">True while the job is still going.</param>
/// <param name="StartedUtc">When it started.</param>
/// <param name="Elapsed">How long it has run, or ran for.</param>
/// <param name="Progress">The latest one-line progress message.</param>
/// <param name="Lines">The job's log, newest last, capped by the runner.</param>
/// <param name="Summary">The final summary line, once the job has finished.</param>
/// <param name="Error">The message of the exception that ended the job, if one did.</param>
/// <param name="CancellationRequested">True once a stop has been asked for.</param>
public sealed record MaintenanceJobState(
    string Name,
    bool IsRunning,
    DateTimeOffset StartedUtc,
    TimeSpan Elapsed,
    string Progress,
    IReadOnlyList<string> Lines,
    string? Summary,
    string? Error,
    bool CancellationRequested);
