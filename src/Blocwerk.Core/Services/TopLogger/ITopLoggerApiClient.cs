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
    /// Cheap "is there anything new?" probe: fetches just the connected user's most recent climb-day
    /// (page 1, one row of the <c>statsAtDate desc</c> feed) without drilling into any per-day logs.
    /// Callers compare the returned date to their last sync to skip a full pull when nothing changed.
    /// </summary>
    /// <param name="userId">The Blocwerk user whose TopLogger logbook to probe.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The newest climb-day's <c>statsAtDate</c>, or null when the user has no climb-days.</returns>
    /// <exception cref="TopLoggerAuthException">
    /// Thrown when the user is not connected or the session cannot be refreshed.
    /// </exception>
    Task<DateTimeOffset?> GetLatestClimbDayAsync(
        Guid userId,
        CancellationToken cancellationToken = default);
}
