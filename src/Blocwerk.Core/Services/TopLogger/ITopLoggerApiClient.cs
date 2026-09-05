namespace Blocwerk.Core.Services.TopLogger;

/// <summary>
/// High-level TopLogger read API for a connected Blocwerk user. Handles resolving
/// the TopLogger user behind the stored token, paginating the logbook and
/// mapping raw responses into Blocwerk DTOs.
/// </summary>
public interface ITopLoggerApiClient
{
    /// <summary>
    /// Fetches all of the connected user's ticks, optionally only those logged at
    /// or after <paramref name="since"/>. Iterates the paginated climb-day feed
    /// and drills into each day's logs, respecting the client's request pacing.
    /// </summary>
    /// <param name="userId">The Blocwerk user whose TopLogger logbook to read.</param>
    /// <param name="since">
    /// When set, only ticks logged on or after this instant are returned and
    /// pagination stops once older days are reached.
    /// </param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The mapped ticks, newest first.</returns>
    /// <exception cref="TopLoggerAuthException">
    /// Thrown when the user is not connected or the session cannot be refreshed.
    /// </exception>
    Task<IReadOnlyList<TopLoggerTick>> GetTicksAsync(
        Guid userId,
        DateTimeOffset? since,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Cheap "is there anything new?" probe: fetches just the connected user's most recent climb-day /
    /// session (page 1, one row of the <c>statsAtDate desc</c> feed) without drilling into any per-day
    /// logs. Callers compare the returned date to their last sync to decide between a full pull, a
    /// single-session reconcile, or a skip. The <see cref="TopLoggerSessionSummary.DateKey"/> lets the
    /// caller then pull exactly that session's ticks via <see cref="GetSessionTicksAsync"/>.
    /// </summary>
    /// <param name="userId">The Blocwerk user whose TopLogger logbook to probe.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The newest session summary, or null when the user has no climb-days.</returns>
    /// <exception cref="TopLoggerAuthException">
    /// Thrown when the user is not connected or the session cannot be refreshed.
    /// </exception>
    Task<TopLoggerSessionSummary?> GetLatestSessionAsync(
        Guid userId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Fetches the ticks for a single climb-day / session, identified by its <c>yyyy-MM-dd</c> key.
    /// Drills into just that one day's logs — used to reconcile a session that may have grown since it
    /// was last imported (e.g. a mid-session sync that captured only the first ascents), so the delta
    /// can be imported without re-walking the whole logbook.
    /// </summary>
    /// <param name="userId">The Blocwerk user whose TopLogger logbook to read.</param>
    /// <param name="sessionDateKey">The day's <c>yyyy-MM-dd</c> key (from a session summary).</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The mapped ticks for that day, or an empty list when the key is blank.</returns>
    /// <exception cref="TopLoggerAuthException">
    /// Thrown when the user is not connected or the session cannot be refreshed.
    /// </exception>
    Task<IReadOnlyList<TopLoggerTick>> GetSessionTicksAsync(
        Guid userId,
        string sessionDateKey,
        CancellationToken cancellationToken = default);
}
