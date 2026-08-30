namespace Blocwerk.Core.Services.TopLogger;

/// <summary>
/// Outcome of <see cref="ITopLoggerImportService.ConnectAsync"/>. A failure never stores tokens; the
/// most common failure is the missing-password gate (<see cref="PasswordRequired"/>).
/// </summary>
/// <param name="Success">Whether the tokens were stored and the account connected.</param>
/// <param name="PasswordRequired">True when the connect was refused because the user has no password.</param>
/// <param name="Error">A human-readable failure reason, or null on success.</param>
/// <param name="Sync">The result of an initial sync, when one was folded in; null when the caller runs
/// the initial sync as a separate phase (the current flow).</param>
public sealed record TopLoggerConnectResult(
    bool Success,
    bool PasswordRequired,
    string? Error,
    TopLoggerSyncResult? Sync)
{
    public static TopLoggerConnectResult NeedsPassword() =>
        new(false, true, "Set a password before connecting TopLogger.", null);

    public static TopLoggerConnectResult Failed(string error) =>
        new(false, false, error, null);

    // A null sync means connect only stored the tokens; the caller runs the initial sync separately.
    public static TopLoggerConnectResult Connected(TopLoggerSyncResult? sync = null) =>
        new(sync?.Success ?? true, false, sync?.Error, sync);
}

/// <summary>
/// Outcome of a single <see cref="ITopLoggerImportService.SyncAsync"/> run: how many ascents were
/// imported, skipped as already present, and left needing a manual grade mapping. A failed sync
/// carries the reason and, when the token pair is dead, <see cref="NeedsReauth"/>.
/// </summary>
/// <param name="Success">Whether the sync completed without an error.</param>
/// <param name="NeedsReauth">True when the TopLogger session must be reconnected.</param>
/// <param name="Error">A human-readable failure reason, or null on success.</param>
/// <param name="Imported">Count of newly imported ascents.</param>
/// <param name="Skipped">Count of ticks skipped because they were already imported.</param>
/// <param name="UnmappedGrades">Count of imported ascents whose grade still needs mapping.</param>
public sealed record TopLoggerSyncResult(
    bool Success,
    bool NeedsReauth,
    string? Error,
    int Imported,
    int Skipped,
    int UnmappedGrades)
{
    public static TopLoggerSyncResult Ok(int imported, int skipped, int unmappedGrades) =>
        new(true, false, null, imported, skipped, unmappedGrades);

    public static TopLoggerSyncResult ReauthRequired(string error) =>
        new(false, true, error, 0, 0, 0);

    public static TopLoggerSyncResult Failed(string error) =>
        new(false, false, error, 0, 0, 0);
}

/// <summary>
/// The current TopLogger connection state for a user, for rendering the profile section.
/// </summary>
/// <param name="Connected">Whether a connection row with usable tokens exists.</param>
/// <param name="NeedsReauth">Whether the stored session was rejected and must be reconnected.</param>
/// <param name="LastSyncAt">When the last successful sync ran, or null if never.</param>
/// <param name="LastError">The last sync error, or null when healthy.</param>
/// <param name="AscentCount">How many ascents have been imported for this user.</param>
/// <param name="UnmappedGradeCount">How many imported ascents still need a grade mapping.</param>
public sealed record TopLoggerStatus(
    bool Connected,
    bool NeedsReauth,
    DateTimeOffset? LastSyncAt,
    string? LastError,
    int AscentCount,
    int UnmappedGradeCount)
{
    public static TopLoggerStatus Disconnected { get; } =
        new(false, false, null, null, 0, 0);
}
