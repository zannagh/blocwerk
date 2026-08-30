namespace Blocwerk.Core.Services.TopLogger;

/// <summary>
/// Orchestrates connecting a Blocwerk user to TopLogger and importing their logbook into
/// <see cref="Entities.ExternalAscent"/> rows. Connecting is gated behind the user having a password
/// set, so a shared OAuth-only session can never attach a third-party token store to the account.
/// </summary>
public interface ITopLoggerImportService
{
    /// <summary>
    /// Stores the supplied token pair for the user and returns success without pulling any data —
    /// the caller runs the initial <see cref="SyncAsync"/> as a separate phase. Refused (no tokens
    /// stored) when the user has no password set.
    /// </summary>
    Task<TopLoggerConnectResult> ConnectAsync(
        Guid userId,
        string accessToken,
        string refreshToken,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Pulls new ticks since the last sync and upserts them as ascents, clustering each into an
    /// activity and mapping grades. Never throws on an auth failure — it flags the connection for
    /// reconnect and returns a failed result instead.
    /// </summary>
    Task<TopLoggerSyncResult> SyncAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes the user's TopLogger connection, optionally deleting every ascent imported from it.
    /// </summary>
    Task DisconnectAsync(Guid userId, bool deleteImportedAscents, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the current connection state for rendering the profile section.
    /// </summary>
    Task<TopLoggerStatus> GetStatusAsync(Guid userId, CancellationToken cancellationToken = default);
}
