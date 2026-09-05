namespace Blocwerk.Core.Enums;

/// <summary>
/// The outcome of the most recent TopLogger sync attempt, recorded on every run (including runs that
/// found no new data). A null value on the connection means no sync has ever been attempted.
/// </summary>
public enum TopLoggerSyncOutcome
{
    /// <summary>The attempt completed without error — whether or not it imported any new ascents.</summary>
    Success = 0,

    /// <summary>The attempt failed; the reason is carried in <c>LastError</c>.</summary>
    Failed = 1,
}
