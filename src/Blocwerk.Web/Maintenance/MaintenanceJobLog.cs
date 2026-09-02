namespace Blocwerk.Web.Maintenance;

/// <summary>
/// How a maintenance job talks back to the dashboard: a single replaceable progress line, and an
/// append-only log for the things worth keeping (every avatar rewritten, every image that failed).
/// </summary>
/// <param name="report">Replaces the current progress line.</param>
/// <param name="append">Adds a line to the log.</param>
public sealed class MaintenanceJobLog(Action<string> report, Action<string> append)
{
    /// <summary>Replaces the one-line "what is happening right now" message.</summary>
    public void Report(string message) => report(message);

    /// <summary>Adds a line to the retained log.</summary>
    public void Append(string message) => append(message);
}
